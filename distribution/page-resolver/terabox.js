'use strict';

// TeraBox share -> direct download URL.
//
// TeraBox gates the original-quality file download behind a login. What a
// guest CAN get without an account is the transcoded HLS stream (usually
// 480p). So:
//   - with TERABOX_COOKIE set (the `ndus=...` pair from a logged-in
//     terabox.com) -> the real dlink, full quality
//   - without it -> the 480p .m3u8 stream URL, flagged as such
//
// Opens the share in the same patched headless browser used by /resolve
// and reads the tokens straight off the API calls the page makes.

const { chromium } = require('patchright');

const TERABOX_HOST =
  /(^|\.)(terabox|1024terabox|1024tera|teraboxapp|teraboxlink|terasharelink|4funbox|mirrobox|nephobox|momerybox|tibibox|freeterabox|gibibox)\.(com|app|net|link|co|online)$/i;

function isTeraboxUrl(u) {
  try {
    return TERABOX_HOST.test(new URL(u).host);
  } catch (e) {
    return false;
  }
}

function shareCode(u) {
  const url = new URL(u);
  let surl = url.searchParams.get('surl');
  if (!surl) {
    const m = url.pathname.match(/\/s\/1?([A-Za-z0-9_-]+)/);
    if (m) surl = m[1];
  }
  if (!surl) {
    throw new Error('no share code (surl) found in ' + u);
  }
  return surl.replace(/^1/, '');
}

function cookieObjects(cookie) {
  return cookie
    .split(';')
    .map((s) => s.trim())
    .filter(Boolean)
    .map((kv) => {
      const i = kv.indexOf('=');
      const name = i === -1 ? kv : kv.slice(0, i);
      const value = i === -1 ? '' : kv.slice(i + 1);
      return { name, value, domain: '.terabox.com', path: '/' };
    })
    .filter((c) => c.name);
}

function pickVideo(list) {
  const files = (list || []).filter((f) => String(f.isdir) !== '1');
  if (files.length === 0) return null;
  const vids = files.filter((f) => /\.(mp4|mkv|avi|flv|m4v|mov|ts|webm)$/i.test(f.server_filename || f.path || ''));
  const pool = vids.length ? vids : files;
  pool.sort((a, b) => Number(b.size || 0) - Number(a.size || 0));
  return pool[0];
}

async function resolveTerabox(rawUrl, opts = {}) {
  const {
    timeoutMs = 90000,
    cookie = process.env.TERABOX_COOKIE || '',
    debug = false,
    // Base URL this service is reachable at (Sonarr passes its configured
    // Page Resolver URL). Used to hand back a remuxed-stream fetch URL.
    selfUrl = process.env.PUBLIC_URL || '',
  } = opts;

  const surl = shareCode(rawUrl);

  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });

  try {
    const context = await browser.newContext({
      userAgent:
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
      viewport: { width: 1280, height: 900 },
      locale: 'en-US',
    });

    if (cookie) {
      await context.addCookies(cookieObjects(cookie));
    }

    const page = await context.newPage();
    page.on('popup', (p) => p.close().catch(() => {}));

    let dlink = null; // full-quality direct link (needs login)
    const streamUrls = []; // transcoded video .m3u8 candidates (guest)
    let fileName = null;
    let listInfo = null; // { shareid, uk, fs_id }

    page.on('request', (req) => {
      const u = req.url();
      // Video HLS only -- skip the M3U8_SUBTITLE_* track.
      if (/\/share\/streaming\?/i.test(u) && /type=M3U8/i.test(u) && !/type=M3U8_SUBTITLE/i.test(u)) {
        if (!streamUrls.includes(u)) streamUrls.push(u);
      }
      if (!dlink && /:\/\/[^/]*(terabox|1024tera|teraboxcdn|freeterabox|nephobox)[^/]*\/file\//i.test(u)) {
        dlink = u;
      }
    });

    page.on('response', async (res) => {
      const u = res.url();
      if (!/\/(api\/shorturlinfo|share\/list)/i.test(u)) return;
      try {
        const j = await res.json();
        if (j && j.errno === 0 && Array.isArray(j.list)) {
          const v = pickVideo(j.list);
          if (v) {
            fileName = fileName || v.server_filename || null;
            if (v.dlink) dlink = v.dlink;
            listInfo = {
              shareid: j.shareid || j.share_id,
              uk: j.uk || j.share_uk,
              fs_id: v.fs_id,
            };
          }
        }
      } catch (e) {
        /* not json */
      }
    });

    await page.goto(`https://www.terabox.com/sharing/link?surl=${surl}`, {
      waitUntil: 'domcontentloaded',
      timeout: timeoutMs,
    });
    await page.waitForLoadState('networkidle', { timeout: 15000 }).catch(() => {});

    // A click makes the page fetch share/list (and, when logged in, a dlink).
    await page
      .locator('button.download-btn, .download-btn, button:has-text("Download")')
      .first()
      .evaluate((el) => el.click())
      .catch(() => {});

    // Nudge the player so it requests the video HLS, then bump the
    // quality selector to whatever the highest option is.
    await page.locator('.vjs-big-play-button, button:has-text("Play")').first().click({ timeout: 4000 }).catch(() => {});
    await page.waitForTimeout(1500);
    await page.locator('.vjs-playback-resolution').first().click({ timeout: 3000 }).catch(() => {});
    await page.locator('.vjs-playback-resolution .vjs-menu-item').first().click({ timeout: 3000 }).catch(() => {});

    const deadline = Date.now() + Math.min(timeoutMs, 25000);
    while (!dlink && streamUrls.length === 0 && Date.now() < deadline) {
      await page.waitForTimeout(400);
    }
    // Give a beat for a higher-quality request after the resolution click.
    if (!dlink) await page.waitForTimeout(2500);

    // Full-quality link: follow it once to the final CDN URL.
    if (dlink) {
      let link = dlink;
      const r = await context.request
        .get(link, { maxRedirects: 0, timeout: 20000, headers: { 'User-Agent': 'Mozilla/5.0' } })
        .catch(() => null);
      const loc = r && r.headers && r.headers()['location'];
      if (loc) link = loc;
      return { link, filename: fileName, quality: 'original' };
    }

    // Guest fallback: the transcoded HLS stream (typically 480p). Hand
    // back a URL on this service that remuxes it to a single MP4 on the
    // fly, so Sonarr can download it like any other direct link.
    if (streamUrls.length) {
      // Prefer the highest advertised resolution.
      const rank = (u) => {
        const t = (u.match(/type=(M3U8[A-Z0-9_]+)/i) || [])[1] || '';
        const nums = t.match(/\d{3,4}/g);
        if (nums) return Math.max(...nums.map(Number));
        return /AUTO/i.test(t) ? 1 : 0;
      };
      streamUrls.sort((a, b) => rank(b) - rank(a));
      const streamUrl = streamUrls[0];
      const res = rank(streamUrl);
      const base = String(selfUrl || '').replace(/\/+$/, '');
      const link = base
        ? `${base}/terabox/fetch?src=${encodeURIComponent(streamUrl)}`
        : streamUrl;
      return {
        link,
        filename: fileName,
        quality: res ? `stream-${res}p` : 'stream',
        note:
          'TeraBox only gives guests a ~480p transcoded stream. Set ' +
          'TERABOX_COOKIE on page-resolver for the original file.',
      };
    }

    throw new Error(
      'TeraBox produced neither a download link nor a stream URL. The share ' +
        'likely needs a logged-in session -- set TERABOX_COOKIE on page-resolver.',
    );
  } catch (err) {
    if (debug) err.note = 'terabox surl=' + surl;
    throw err;
  } finally {
    await browser.close().catch(() => {});
  }
}

module.exports = { resolveTerabox, isTeraboxUrl };
