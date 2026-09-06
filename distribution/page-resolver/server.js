'use strict';

// Headless-browser resolver for JS-gated download pages that
// FlareSolverr/Byparr can't handle because they need a click (e.g.
// misterdonghua.in's "Get Video" button, which only runs its encrypted
// download-link API after the click). misterdonghua also sniffs for
// headless / webdriver / adblock, so this uses stealth and leaves the
// site's own analytics alone -- it only blocks the pop-under ad networks.
//
//   POST /resolve
//   { "url": "https://misterdonghua.in/#<hash>&dl=1",
//     "clickText": "Get Video",              // optional
//     "resultSelector": "a[download], a[href*=\"/download?\"]", // optional
//     "resultAttr": "href",                  // optional
//     "timeoutMs": 45000,                    // optional
//     "debug": false }                       // optional: include page text on failure
//   -> 200 { "link": "https://...mp4...", "filename": "...", "elapsedMs": N }
//   -> 502 { "error": "...", "elapsedMs": N }

const http = require('http');
const { chromium } = require('playwright-extra');
const stealth = require('puppeteer-extra-plugin-stealth')();

chromium.use(stealth);

const PORT = parseInt(process.env.PORT || '3000', 10);
const NAV_TIMEOUT = parseInt(process.env.NAV_TIMEOUT_MS || '45000', 10);

// Pop-under / redirect ad networks only. NOT analytics -- misterdonghua
// treats a blocked analytics request as "adblock detected" and refuses.
const BLOCK_HOSTS = (process.env.BLOCK_HOSTS ||
  'popads,popcash,propellerads,adsterra,exoclick,juicyads,admaven,' +
  'attirecideryeah,brigadedelegatesandbox,cacklegrievingtank,' +
  'gigglemagnetismunaired,prahmnatured,aphacicfable,ukankingwithea,' +
  'onclickalgo,hilltopads,adnium,clickadu,2mdn.net'
).split(',').map((s) => s.trim()).filter(Boolean);

let browserPromise = null;
function getBrowser() {
  if (!browserPromise) {
    browserPromise = chromium.launch({
      headless: true,
      args: [
        '--no-sandbox',
        '--disable-dev-shm-usage',
        '--disable-blink-features=AutomationControlled',
        '--disable-features=IsolateOrigins,site-per-process',
      ],
    });
  }
  return browserPromise;
}

async function resolve(opts) {
  const {
    url,
    clickText = 'Get Video',
    resultSelector = 'a[download], a[href*="/download?"], a.vds-download-button',
    resultAttr = 'href',
    timeoutMs = NAV_TIMEOUT,
    debug = false,
  } = opts;

  if (!url || !/^https?:\/\//i.test(url)) {
    throw new Error('a valid absolute url is required');
  }
  const pageHost = new URL(url).host;

  const browser = await getBrowser();
  const context = await browser.newContext({
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
    viewport: { width: 1920, height: 1080 },
    screen: { width: 3840, height: 2160 },
    locale: 'en-US',
  });

  await context.addInitScript(() => {
    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
    // A couple of the cheaper headless tells.
    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
    if (!window.chrome) window.chrome = { runtime: {} };
  });

  await context.route('**/*', (route) => {
    const u = route.request().url();
    if (BLOCK_HOSTS.some((h) => u.includes(h))) return route.abort();
    return route.continue();
  });

  const page = await context.newPage();

  context.on('page', (p) => {
    if (p !== page) p.close().catch(() => {});
  });
  page.on('popup', (p) => p.close().catch(() => {}));

  try {
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: timeoutMs });
    // Let the SPA hydrate.
    await page.waitForLoadState('networkidle', { timeout: 10000 }).catch(() => {});

    if (clickText) {
      const btn = page.getByText(clickText, { exact: false }).first();
      await btn.waitFor({ state: 'visible', timeout: timeoutMs });
      // These sites float an invisible ad <div> over the button so a normal
      // click is intercepted. Fire the element's own handler directly.
      await btn.evaluate((el) => el.click());
    }

    const link = await page.waitForFunction(
      ([sel, attr, host]) => {
        for (const node of document.querySelectorAll(sel)) {
          const v = node.getAttribute(attr) || node[attr] || '';
          if (!/^https?:\/\//i.test(v)) continue;
          try {
            if (new URL(v).host === host) continue;
          } catch (e) {
            continue;
          }
          return v;
        }
        return null;
      },
      [resultSelector, resultAttr, pageHost],
      { timeout: timeoutMs, polling: 300 },
    ).then((h) => h.jsonValue());

    let filename = null;
    try {
      filename = (await page.title()) || null;
    } catch (e) {
      /* ignore */
    }

    return { link, filename };
  } catch (err) {
    if (debug) {
      let text = '';
      try {
        text = (await page.evaluate(() => document.body.innerText)).slice(0, 1500);
      } catch (e) {
        /* ignore */
      }
      err.debugText = text;
    }
    throw err;
  } finally {
    await context.close().catch(() => {});
  }
}

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/health') {
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end('{"status":"ok"}');
  }
  if (req.method !== 'POST' || req.url !== '/resolve') {
    res.writeHead(404);
    return res.end('not found');
  }

  let body = '';
  req.on('data', (c) => {
    body += c;
    if (body.length > 1e6) req.destroy();
  });
  req.on('end', async () => {
    let opts;
    try {
      opts = JSON.parse(body || '{}');
    } catch (e) {
      res.writeHead(400, { 'content-type': 'application/json' });
      return res.end('{"error":"invalid json"}');
    }

    const started = Date.now();
    try {
      const result = await resolve(opts);
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ ...result, elapsedMs: Date.now() - started }));
    } catch (err) {
      res.writeHead(502, { 'content-type': 'application/json' });
      res.end(JSON.stringify({
        error: String(err && err.message ? err.message : err),
        debug: err && err.debugText ? err.debugText : undefined,
        elapsedMs: Date.now() - started,
      }));
    }
  });
});

server.listen(PORT, () => {
  // eslint-disable-next-line no-console
  console.log(`page-resolver listening on :${PORT} (blocking ${BLOCK_HOSTS.length} ad hosts)`);
});
