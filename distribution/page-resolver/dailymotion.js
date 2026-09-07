'use strict';

// Dailymotion embed -> MP4 stream.
//
// Chinese-anime sites (animexin, donghuastream, ...) embed the actual
// video from Dailymotion. This drives the embed in the headless browser,
// grabs the HLS master manifest, picks the highest variant, and remuxes
// it to a single MP4 with ffmpeg -- piped straight to the caller so a
// plain HTTP downloader can save it.
//
//   GET /dailymotion/fetch?v=<id>[&referer=<page>][&max=1080]
//     -> streams video/mp4

const { chromium } = require('patchright');
const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ' +
  '(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36';

function videoId(input) {
  if (/^[A-Za-z0-9]+$/.test(input)) return input;
  const m = String(input).match(/dailymotion\.com\/(?:embed\/)?video\/([A-Za-z0-9]+)/i) ||
    String(input).match(/dai\.ly\/([A-Za-z0-9]+)/i);
  if (!m) throw new Error('no Dailymotion video id in ' + input);
  return m[1];
}

// Returns { playlistPath, height } -- a local .m3u8 with absolute,
// freshly-signed segment URLs for the highest variant <= maxHeight.
async function buildPlaylist(id, { referer = 'https://www.dailymotion.com/', maxHeight = 1080, timeoutMs = 60000 } = {}) {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  try {
    const context = await browser.newContext({ userAgent: UA, locale: 'en-US' });
    const page = await context.newPage();
    page.on('popup', (p) => p.close().catch(() => {}));

    const playlists = {};
    page.on('response', async (res) => {
      const u = res.url();
      if (/\.m3u8/i.test(u)) {
        try {
          const t = await res.text();
          if (t) playlists[u] = t;
        } catch (e) { /* */ }
      }
    });

    await page.goto(`https://www.dailymotion.com/embed/video/${id}`, {
      waitUntil: 'domcontentloaded',
      timeout: timeoutMs,
      referer,
    });
    await page.waitForTimeout(2500);
    await page.mouse.click(400, 220).catch(() => {});
    await page.keyboard.press('k').catch(() => {});

    // Wait for a master manifest to show up.
    const deadline = Date.now() + Math.min(timeoutMs, 25000);
    let masterUrl = null;
    while (Date.now() < deadline) {
      masterUrl = Object.keys(playlists).find((u) => playlists[u].includes('#EXT-X-STREAM-INF'));
      if (masterUrl) break;
      await page.waitForTimeout(500);
    }
    if (!masterUrl) throw new Error('no HLS master manifest from Dailymotion (video removed or geo-blocked?)');

    const lines = playlists[masterUrl].split('\n');
    const variants = [];
    for (let i = 0; i < lines.length; i++) {
      if (lines[i].startsWith('#EXT-X-STREAM-INF')) {
        const h = Number((lines[i].match(/RESOLUTION=\d+x(\d+)/) || [0, 0])[1]);
        variants.push({ h, uri: (lines[i + 1] || '').trim() });
      }
    }
    if (variants.length === 0) throw new Error('master manifest had no variants');
    variants.sort((a, b) => b.h - a.h);
    const pick = variants.find((v) => v.h <= maxHeight) || variants[variants.length - 1];
    const mediaUrl = /^https?:/.test(pick.uri) ? pick.uri : new URL(pick.uri, masterUrl).href;

    // Fetch the media playlist from inside the page (right origin + cookies).
    const mediaBody = await page.evaluate(async (mu) => {
      const r = await fetch(mu);
      return r.text();
    }, mediaUrl);
    if (!String(mediaBody).startsWith('#EXTM3U')) {
      throw new Error('Dailymotion media playlist fetch failed');
    }

    const absolute = mediaBody.replace(/^(?!#)(\S.*)$/gm, (line) =>
      /^https?:/.test(line) ? line : new URL(line, mediaUrl).href);

    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dm-'));
    const playlistPath = path.join(dir, 'stream.m3u8');
    fs.writeFileSync(playlistPath, absolute);
    return { playlistPath, height: pick.h, dir };
  } finally {
    await browser.close().catch(() => {});
  }
}

// GET handler: capture + remux + stream.
async function dailymotionFetch(req, res, query) {
  let id;
  try {
    id = videoId(query.get('v') || '');
  } catch (e) {
    res.writeHead(400);
    return res.end(e.message);
  }
  const referer = query.get('referer') || 'https://www.dailymotion.com/';
  const maxHeight = Number(query.get('max') || query.get('q') || 1080) || 1080;

  if (req.method === 'HEAD') {
    res.writeHead(200, { 'content-type': 'video/mp4' });
    return res.end();
  }

  let built;
  try {
    built = await buildPlaylist(id, { referer, maxHeight });
  } catch (err) {
    res.writeHead(502, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ error: String(err.message || err) }));
  }

  res.writeHead(200, {
    'content-type': 'video/mp4',
    'content-disposition': `attachment; filename="dailymotion-${id}-${built.height}p.mp4"`,
  });

  const ff = spawn('ffmpeg', [
    '-hide_banner', '-loglevel', 'error',
    '-allowed_extensions', 'ALL',
    '-protocol_whitelist', 'file,http,https,tcp,tls,crypto',
    '-i', built.playlistPath,
    '-c', 'copy',
    '-bsf:a', 'aac_adtstoasc',
    '-f', 'mp4',
    '-movflags', 'frag_keyframe+empty_moov+default_base_moof',
    'pipe:1',
  ]);
  ff.stdout.pipe(res);
  ff.stderr.on('data', (d) => process.stderr.write(d));
  const cleanup = () => {
    try { ff.kill('SIGKILL'); } catch (e) { /* */ }
    try { fs.rmSync(built.dir, { recursive: true, force: true }); } catch (e) { /* */ }
  };
  ff.on('close', () => { try { fs.rmSync(built.dir, { recursive: true, force: true }); } catch (e) { /* */ } });
  ff.on('error', () => { try { res.destroy(); } catch (e) { /* */ } cleanup(); });
  req.on('close', cleanup);
  res.on('close', cleanup);
}

function hasFfmpeg() {
  try {
    return spawnSync('ffmpeg', ['-version']).status === 0;
  } catch (e) {
    return false;
  }
}

module.exports = { dailymotionFetch, videoId, hasFfmpeg };
