'use strict';

// Rumble embed -> MP4 stream.
//
// donghuaworld.com's "Dark Server" embeds the video from Rumble (via a
// player.donghuaplanet.com wrapper). Rumble serves a plain HLS master at
// rumble.com/hls-vod/<token>/playlist.m3u8 whose variants and segments
// are range-slices of packed .tar files -- but the CDN honours the
// r_range query param on a plain GET, so no headless browser or tar
// extraction is needed: fetch master -> pick highest variant -> fetch
// chunklist -> absolutise -> ffmpeg remux to a seekable MP4.
//
//   GET /rumble/fetch?embed=<player url>            (donghuaplanet etc.)
//   GET /rumble/fetch?m3u8=<rumble hls-vod url>
//     [&referer=<page>][&max=2160]
//     -> streams video/mp4

const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { downloadEnglishVtt } = require('./hlssubs');

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ' +
  '(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36';

async function httpText(url, referer) {
  const headers = { 'User-Agent': UA, Accept: '*/*' };
  if (referer) headers.Referer = referer;
  const r = await fetch(url, { headers, redirect: 'follow' });
  if (!r.ok) throw new Error('GET ' + url + ' -> ' + r.status);
  return r.text();
}

// A player / server-hop page (donghuaplanet.com/vX, luciferdonghua .../v/N/,
// rumble.com/embed/X) -> the rumble hls-vod master URL. Follows one nested
// rumble/donghuaplanet iframe when the page is just a wrapper.
async function masterFromEmbed(embedUrl, referer, depth = 0) {
  // Rumble's embed JSON escapes its slashes ("https:\/\/...").
  const html = (await httpText(embedUrl, referer)).replace(/\\\//g, '/');

  // The hls-vod master lists every variant -- always prefer it.
  const master = html.match(/https?:\/\/rumble\.com\/hls-vod\/[A-Za-z0-9_-]+\/playlist\.m3u8/i);
  if (master) return master[0];

  if (depth < 2) {
    const nested = html.match(/<iframe[^>]*\bsrc=["'](https?:\/\/(?:rumble\.com\/embed|[a-z0-9.-]*donghuaplanet\.com)\/[^"']+)["']/i);
    if (nested) return masterFromEmbed(nested[1], embedUrl, depth + 1);
  }

  const fileProp = html.match(/["'](?:file|url)["']\s*:\s*["'](https?:\/\/[^"']+\.m3u8[^"']*)["']/i);
  if (fileProp) return fileProp[1];

  const anyM3u8 = html.match(/https?:\/\/[^"'\s]+\/playlist\.m3u8[^"'\s]*/i)
    || html.match(/https?:\/\/[^"'\s]+\.m3u8[^"'\s]*/i);
  if (anyM3u8) return anyM3u8[0];

  throw new Error('no HLS url found in embed ' + embedUrl);
}

async function httpBuffer(url, referer) {
  const headers = { 'User-Agent': UA, Accept: '*/*' };
  if (referer) headers.Referer = referer;
  const r = await fetch(url, { headers, redirect: 'follow' });
  if (!r.ok) throw new Error('GET ' + url + ' -> ' + r.status);
  return Buffer.from(await r.arrayBuffer());
}

// Rumble packs each HLS segment as a range-slice of a .tar
// ("...tar?r_file=media-0.ts&r_range=X-Y"); ffmpeg's HLS demuxer rejects
// the ".tar?" segment name even with -allowed_extensions ALL. So fetch
// every segment here and concatenate them into one MPEG-TS file (TS
// segments join cleanly at the byte level), which ffmpeg reads fine.
//
// Returns { tsPath, height, dir, subPath }.
async function buildStream({ embed, m3u8, referer, maxHeight = 1080 }) {
  const master = m3u8 || (await masterFromEmbed(embed, referer));
  const masterBody = await httpText(master, referer);

  const lines = masterBody.split('\n');
  const variants = [];
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].startsWith('#EXT-X-STREAM-INF')) {
      const h = Number((lines[i].match(/RESOLUTION=\d+x(\d+)/) || [0, 0])[1]);
      variants.push({ h, uri: (lines[i + 1] || '').trim() });
    }
  }
  if (variants.length === 0) throw new Error('Rumble master manifest had no variants');

  variants.sort((a, b) => b.h - a.h);
  const pick = variants.find((v) => v.h > 0 && v.h <= maxHeight) || variants[0];
  const chunkUrl = /^https?:/.test(pick.uri) ? pick.uri : new URL(pick.uri, master).href;

  const chunkBody = await httpText(chunkUrl, referer);
  if (!chunkBody.startsWith('#EXTM3U')) throw new Error('Rumble chunklist fetch failed');

  const segUrls = chunkBody
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith('#'))
    .map((l) => (/^https?:/.test(l) ? l : new URL(l, chunkUrl).href));
  if (segUrls.length === 0) throw new Error('Rumble chunklist had no segments');

  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'rm-'));
  const tsPath = path.join(dir, 'all.ts');
  const out = fs.createWriteStream(tsPath);

  // Fetch a few segments ahead so the write stays in order.
  const CONCURRENCY = 6;
  const bufs = new Array(segUrls.length);
  let next = 0;
  let writeIdx = 0;

  const flush = () => {
    while (writeIdx < segUrls.length && bufs[writeIdx] !== undefined) {
      out.write(bufs[writeIdx]);
      bufs[writeIdx] = undefined;
      writeIdx++;
    }
  };

  await new Promise((resolve, reject) => {
    let active = 0;
    let failed = null;
    const pump = () => {
      if (failed) return;
      if (writeIdx >= segUrls.length) return resolve();
      while (active < CONCURRENCY && next < segUrls.length && next < writeIdx + CONCURRENCY * 3) {
        const idx = next++;
        active++;
        httpBuffer(segUrls[idx], referer)
          .then((b) => { bufs[idx] = b; })
          .catch((e) => { failed = e; })
          .finally(() => { active--; flush(); failed ? reject(failed) : pump(); });
      }
    };
    pump();
  });

  await new Promise((resolve) => out.end(resolve));
  const subPath = await downloadEnglishVtt(masterBody, master, dir, referer);
  return { tsPath, height: pick.h, dir, subPath };
}

async function rumbleFetch(req, res, query) {
  const embed = query.get('embed') || '';
  const m3u8 = query.get('m3u8') || '';
  if (!embed && !m3u8) {
    res.writeHead(400);
    return res.end('need ?embed= or ?m3u8=');
  }

  const referer = query.get('referer') || '';
  const maxHeight = Number(query.get('max') || query.get('q') || 1080) || 1080;

  if (req.method === 'HEAD') {
    res.writeHead(200, { 'content-type': 'video/mp4' });
    return res.end();
  }

  let built;
  try {
    built = await buildStream({ embed, m3u8, referer, maxHeight });
  } catch (err) {
    res.writeHead(502, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ error: String(err.message || err) }));
  }

  const outPath = path.join(built.dir, 'out.mp4');
  const remux = () =>
    new Promise((resolve, reject) => {
      // No -fflags +genpts: the concatenated TS keeps real timestamps,
      // and forcing PTS regeneration made playback race / skip around.
      const args = ['-hide_banner', '-loglevel', 'error', '-i', built.tsPath];
      if (built.subPath) args.push('-i', built.subPath);
      args.push('-map', '0');
      if (built.subPath) args.push('-map', '1:0');
      args.push('-c', 'copy', '-bsf:a', 'aac_adtstoasc');
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
    'content-disposition': `attachment; filename="rumble-${built.height || 'src'}p.mp4"`,
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

module.exports = { rumbleFetch, masterFromEmbed, buildStream, hasFfmpeg };
