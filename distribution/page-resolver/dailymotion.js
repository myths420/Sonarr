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
async function downloadSegments(mediaBody, mediaUrl, dir, referer, name = 'all') {
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

  const outPath = path.join(dir, name + (isFmp4 ? '.mp4' : '.ts'));
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

// URI of the separate audio rendition a variant points at, or null when
// the variant carries its own audio. Newer (AV1) Dailymotion variants keep
// the audio out of the video segments: the STREAM-INF line names an
// AUDIO="<group>" and the master lists it as
//   #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="<group>",...,URI="audio.m3u8"
function pickAudioUri(masterBody, masterUrl, streamInfLine) {
  const group = (streamInfLine.match(/AUDIO="([^"]+)"/i) || [])[1];
  if (!group) return null;

  const entries = [];
  for (const line of masterBody.split('\n')) {
    if (!/^#EXT-X-MEDIA:/i.test(line) || !/TYPE=AUDIO/i.test(line)) continue;
    if ((line.match(/GROUP-ID="([^"]*)"/i) || [])[1] !== group) continue;
    const uri = (line.match(/URI="([^"]+)"/i) || [])[1];
    if (uri) entries.push({ uri, isDefault: /DEFAULT=YES/i.test(line) });
  }
  if (entries.length === 0) return null;

  const chosen = entries.find((e) => e.isDefault) || entries[0];
  return /^https?:/.test(chosen.uri) ? chosen.uri : new URL(chosen.uri, masterUrl).href;
}

// Returns { streamPath, isFmp4, height, dir, subPath, audio } -- one local
// file with the full video for the highest variant <= maxHeight, plus the
// separate audio file ({ path, isFmp4 }) when the variant has one.
async function buildPlaylist(id, { referer = 'https://www.dailymotion.com/', maxHeight = 1080, timeoutMs = 60000, player = '' } = {}) {
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

    // Sites embed a specific player (geo.dailymotion.com/player/<id>.html);
    // the generic /embed/video/<id> page is "Forbidden" for videos
    // restricted to that player, so load the same player the site does.
    const embedUrl = player
      ? `https://geo.dailymotion.com/player/${player}.html?video=${id}`
      : `https://www.dailymotion.com/embed/video/${id}`;
    await page.goto(embedUrl, {
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
        variants.push({ h, uri: (lines[i + 1] || '').trim(), info: lines[i] });
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
    try {
      const seg = await downloadSegments(mediaBody, mediaUrl, dir, referer);
      const subPath = await downloadEnglishVtt(playlists[masterUrl], masterUrl, dir, referer);

      let audio = null;
      const audioUrl = pickAudioUri(playlists[masterUrl], masterUrl, pick.info);
      if (audioUrl) {
        const audioBody = await page.evaluate(async (au) => {
          const r = await fetch(au);
          return r.text();
        }, audioUrl);
        if (!String(audioBody).startsWith('#EXTM3U')) {
          throw new Error('Dailymotion audio playlist fetch failed');
        }
        audio = await downloadSegments(audioBody, audioUrl, dir, referer, 'audio');
      }

      return { streamPath: seg.path, isFmp4: seg.isFmp4, height: pick.h, dir, subPath, audio };
    } catch (err) {
      try { fs.rmSync(dir, { recursive: true, force: true }); } catch (e) { /* */ }
      throw err;
    }
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

  // res 'close' is the real client-disconnect signal; req 'close' fires as soon
  // as the request body is consumed, so it never cleaned up after a lost client.
  let clientGone = false;
  res.on('close', () => { clientGone = true; });

  let built;
  try {
    const player = /^[A-Za-z0-9]+$/.test(query.get('player') || '') ? query.get('player') : '';
    built = await buildPlaylist(id, { referer, maxHeight, player });
  } catch (err) {
    res.writeHead(502, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ error: String(err.message || err) }));
  }
  if (clientGone) {
    try { fs.rmSync(built.dir, { recursive: true, force: true }); } catch (e) { /* */ }
    return;
  }

  // Remux the concatenated stream to a real, seekable MP4 (moov atom at
  // the front via +faststart). -c copy with the real segment timestamps
  // -- no -fflags +genpts, which mangled the timeline (fast / jumpy
  // playback). aac_adtstoasc only for MPEG-TS; fMP4 audio is already ASC.
  const outPath = path.join(built.dir, 'out.mp4');
  const remux = () =>
    new Promise((resolve, reject) => {
      const args = ['-hide_banner', '-loglevel', 'error', '-i', built.streamPath];
      let next = 1;
      let audioIdx = -1;
      let subIdx = -1;
      if (built.audio) { args.push('-i', built.audio.path); audioIdx = next++; }
      if (built.subPath) { args.push('-i', built.subPath); subIdx = next++; }
      if (built.audio) {
        // Video from the video file, audio from its own rendition.
        args.push('-map', '0:v', '-map', audioIdx + ':a');
      } else {
        args.push('-map', '0');
      }
      if (subIdx >= 0) args.push('-map', subIdx + ':0');
      args.push('-c', 'copy');
      const audioIsTs = built.audio ? !built.audio.isFmp4 : !built.isFmp4;
      if (audioIsTs) args.push('-bsf:a', 'aac_adtstoasc');
      if (subIdx >= 0) args.push('-c:s', 'mov_text', '-metadata:s:s:0', 'language=eng');
      args.push('-movflags', '+faststart', '-avoid_negative_ts', 'make_zero', '-y', outPath);
      const ff = spawn('ffmpeg', args);
      let errTail = '';
      ff.stderr.on('data', (d) => { errTail = (errTail + d).slice(-2000); process.stderr.write(d); });
      ff.on('error', reject);
      ff.on('close', (code) => (code === 0 ? resolve() : reject(new Error('ffmpeg exit ' + code + ': ' + errTail))));
      res.on('close', () => { try { ff.kill('SIGKILL'); } catch (e) { /* */ } });
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
  res.on('close', () => { stream.destroy(); done(); });
}

function hasFfmpeg() {
  try {
    return spawnSync('ffmpeg', ['-version']).status === 0;
  } catch (e) {
    return false;
  }
}

module.exports = { dailymotionFetch, videoId, hasFfmpeg };
