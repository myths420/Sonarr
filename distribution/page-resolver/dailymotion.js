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
const { downloadEnglishVtt } = require('./hlssubs');
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

async function fetchBuffer(url, referer) {
  const headers = { 'User-Agent': UA, Accept: '*/*' };
  if (referer) headers.Referer = referer;
  const r = await fetch(url, { headers, redirect: 'follow' });
  if (!r.ok) throw new Error('GET ' + url + ' -> ' + r.status);
  return Buffer.from(await r.arrayBuffer());
}

// Download an HLS media playlist's init + media segments into one file
// (fMP4 fragments and MPEG-TS packets both concatenate cleanly at the
// byte level). Handing ffmpeg a single local file -- rather than the
// .m3u8 -- avoids the timestamp mangling (fast / jumpy playback) that
// -fflags +genpts over an HLS input caused.
// Returns { path, isFmp4 }.
async function downloadSegments(mediaBody, mediaUrl, dir, referer) {
  const abs = (u) => (/^https?:/.test(u) ? u : new URL(u, mediaUrl).href);
  const lines = mediaBody.split('\n');
  const parts = [];
  let isFmp4 = false;

  for (const line of lines) {
    const map = line.match(/^#EXT-X-MAP:.*URI="([^"]+)"/i);
    if (map) { parts.push(abs(map[1])); isFmp4 = true; continue; }
    const t = line.trim();
    if (t && !t.startsWith('#')) parts.push(abs(t));
  }
  if (parts.length === 0) throw new Error('media playlist had no segments');

  const outPath = path.join(dir, isFmp4 ? 'all.mp4' : 'all.ts');
  const out = fs.createWriteStream(outPath);
  const bufs = new Array(parts.length);
  let writeIdx = 0;

  const flush = () => {
    while (writeIdx < parts.length && bufs[writeIdx] !== undefined) {
      out.write(bufs[writeIdx]);
      bufs[writeIdx] = undefined;
      writeIdx++;
    }
  };

  const CONCURRENCY = 6;
  let next = 0;
  await new Promise((resolve, reject) => {
    let active = 0;
    let failed = null;
    const pump = () => {
      if (failed) return reject(failed);
      if (writeIdx >= parts.length) return resolve();
      while (active < CONCURRENCY && next < parts.length && next < writeIdx + CONCURRENCY * 4) {
        const idx = next++;
        active++;
        fetchBuffer(parts[idx], referer)
          .then((b) => { bufs[idx] = b; })
          .catch((e) => { failed = e; })
          .finally(() => { active--; flush(); pump(); });
      }
    };
    pump();
  });

  await new Promise((r) => out.end(r));
  return { path: outPath, isFmp4 };
}

// Returns { streamPath, isFmp4, height, dir } -- one local file with the
// full video for the highest variant <= maxHeight.
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

    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dm-'));
    const seg = await downloadSegments(mediaBody, mediaUrl, dir, referer);
    const subPath = await downloadEnglishVtt(playlists[masterUrl], masterUrl, dir, referer);
    return { streamPath: seg.path, isFmp4: seg.isFmp4, height: pick.h, dir, subPath };
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

  // Remux the concatenated stream to a real, seekable MP4 (moov atom at
  // the front via +faststart). -c copy with the real segment timestamps
  // -- no -fflags +genpts, which mangled the timeline (fast / jumpy
  // playback). aac_adtstoasc only for MPEG-TS; fMP4 audio is already ASC.
  const outPath = path.join(built.dir, 'out.mp4');
  const remux = () =>
    new Promise((resolve, reject) => {
      const args = ['-hide_banner', '-loglevel', 'error', '-i', built.streamPath];
      if (built.subPath) args.push('-i', built.subPath);
      args.push('-map', '0');
      if (built.subPath) args.push('-map', '1:0');
      args.push('-c', 'copy');
      if (!built.isFmp4) args.push('-bsf:a', 'aac_adtstoasc');
      if (built.subPath) args.push('-c:s', 'mov_text', '-metadata:s:s:0', 'language=eng');
      args.push('-movflags', '+faststart', '-avoid_negative_ts', 'make_zero', '-y', outPath);
      const ff = spawn('ffmpeg', args);
      let errTail = '';
      ff.stderr.on('data', (d) => { errTail = (errTail + d).slice(-2000); process.stderr.write(d); });
      ff.on('error', reject);
      ff.on('close', (code) => (code === 0 ? resolve() : reject(new Error('ffmpeg exit ' + code + ': ' + errTail))));
      req.on('close', () => { try { ff.kill('SIGKILL'); } catch (e) { /* */ } });
    });

  try {
    await remux();
  } catch (err) {
    try { fs.rmSync(built.dir, { recursive: true, force: true }); } catch (e) { /* */ }
    if (!res.headersSent) {
      res.writeHead(502, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: String(err.message || err) }));
    } else {
      res.destroy();
    }
    return;
  }

  const size = fs.statSync(outPath).size;
  res.writeHead(200, {
    'content-type': 'video/mp4',
    'content-length': size,
    'content-disposition': `attachment; filename="dailymotion-${id}-${built.height}p.mp4"`,
  });

  const stream = fs.createReadStream(outPath);
  stream.pipe(res);
  const done = () => { try { fs.rmSync(built.dir, { recursive: true, force: true }); } catch (e) { /* */ } };
  stream.on('close', done);
  stream.on('error', () => { res.destroy(); done(); });
  req.on('close', () => { stream.destroy(); done(); });
}

function hasFfmpeg() {
  try {
    return spawnSync('ffmpeg', ['-version']).status === 0;
  } catch (e) {
    return false;
  }
}

module.exports = { dailymotionFetch, videoId, hasFfmpeg };
