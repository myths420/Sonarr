'use strict';

// Headless-browser resolver for JS-gated download pages that
// FlareSolverr/Byparr can't handle:
//  - pages that only reveal the link after a *click* (misterdonghua.in
//    "Get Video" -> encrypted download API)
//  - pages behind a Cloudflare Turnstile widget that never even renders in
//    an automated browser (vik1ngfile) -- solved via CAPTCHA_API_KEY.
//
//   POST /resolve
//   { "url": "https://...",
//     "clickText": "Get Video",              // optional; "" to skip
//     "resultSelector": "a[download], a[href*=\"/download?\"]",  // optional
//     "resultAttr": "href",                  // optional
//     "solveCaptcha": true|false,            // optional; default: auto
//     "timeoutMs": 45000, "debug": false }
//   -> 200 { "link": "...", "filename": "...", "elapsedMs": N }
//   -> 502 { "error": "...", "elapsedMs": N }

const http = require('http');
// patchright = Playwright with the CDP runtime leaks patched out
// (Runtime.enable, console API, closed shadow roots) that Cloudflare's
// bot check fingerprints. Drop-in for playwright's chromium.
const { chromium } = require('patchright');
const { solveTurnstile, captchaEnabled, CAPTCHA_PROVIDER, CAPTCHA_ENDPOINT } = require('./captcha');

const PORT = parseInt(process.env.PORT || '3000', 10);
const NAV_TIMEOUT = parseInt(process.env.NAV_TIMEOUT_MS || '45000', 10);

// Pop-under / redirect ad networks only. NOT analytics -- some sites treat
// a blocked analytics request as "adblock detected" and refuse.
const BLOCK_HOSTS = (process.env.BLOCK_HOSTS ||
  'popads,popcash,propellerads,adsterra,exoclick,juicyads,admaven,' +
  'onclickalgo,hilltopads,adnium,clickadu,heiscoquettef,' +
  'attirecideryeah,brigadedelegatesandbox,cacklegrievingtank,' +
  'gigglemagnetismunaired,prahmnatured,aphacicfable,ukankingwithea'
).split(',').map((s) => s.trim()).filter(Boolean);

let browserPromise = null;
function getBrowser() {
  if (!browserPromise) {
    // patchright manages the automation-flag masking itself -- don't add
    // --disable-blink-features=AutomationControlled or stealth plugins.
    browserPromise = chromium.launch({
      headless: true,
      args: ['--no-sandbox', '--disable-dev-shm-usage'],
    });
  }
  return browserPromise;
}

// Poll the page until the result selector holds a real off-site URL.
function waitForLink(page, resultSelector, resultAttr, pageHost, timeoutMs) {
  return page
    .waitForFunction(
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
    )
    .then((h) => h.jsonValue());
}

// Pull the Turnstile sitekey (+ callback name) out of the page.
function readTurnstileParams(page) {
  return page.evaluate(() => {
    const el = document.querySelector('[data-sitekey]');
    if (el) {
      return { sitekey: el.getAttribute('data-sitekey'), callback: el.getAttribute('data-callback') || null };
    }
    const html = document.documentElement.innerHTML;
    const sk = html.match(/sitekey\s*[:=]\s*["']([0-9A-Za-z_-]{10,})["']/i);
    const cb = html.match(/callback\s*[:=]\s*["']?([A-Za-z_$][\w$]*)/i);
    return sk ? { sitekey: sk[1], callback: cb ? cb[1] : null } : null;
  });
}

async function injectTurnstileToken(page, token, callbackName) {
  await page.evaluate(
    ([tok, cb]) => {
      document.querySelectorAll(
        'input[name="cf-turnstile-response"], input[name="g-recaptcha-response"], textarea[name="cf-turnstile-response"]',
      ).forEach((n) => {
        n.value = tok;
        n.dispatchEvent(new Event('input', { bubbles: true }));
        n.dispatchEvent(new Event('change', { bubbles: true }));
      });
      const fns = [];
      if (cb && typeof window[cb] === 'function') fns.push(window[cb]);
      if (typeof window.cloudflareCallback === 'function') fns.push(window.cloudflareCallback);
      if (typeof window.turnstileCallback === 'function') fns.push(window.turnstileCallback);
      if (typeof window.onCaptchaSuccess === 'function') fns.push(window.onCaptchaSuccess);
      fns.forEach((f) => {
        try {
          f(tok);
        } catch (e) {
          /* ignore */
        }
      });
    },
    [token, callbackName],
  );
}

async function resolve(opts) {
  const {
    url,
    clickText = 'Get Video',
    resultSelector = 'a[download], a[href*="/download?"], a.vds-download-button, a#download-link[href], a[href*="/d/"]',
    resultAttr = 'href',
    solveCaptcha,
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
    await page.waitForLoadState('networkidle', { timeout: 10000 }).catch(() => {});

    if (clickText) {
      const btn = page.getByText(clickText, { exact: false }).first();
      await btn.waitFor({ state: 'visible', timeout: timeoutMs }).catch(() => {});
      // An invisible ad <div> is often floated over the button.
      await btn.evaluate((el) => el.click()).catch(() => {});
    }

    // First shot: maybe it's already there (misterdonghua after the click).
    try {
      const link = await waitForLink(page, resultSelector, resultAttr, pageHost, Math.min(timeoutMs, 20000));
      return { link, filename: (await page.title().catch(() => null)) || null };
    } catch (e) {
      /* fall through to captcha */
    }

    // Second shot: is there a Turnstile gating the link?
    const params = solveCaptcha === false ? null : await readTurnstileParams(page).catch(() => null);
    if (params && params.sitekey) {
      if (!captchaEnabled()) {
        throw new Error(
          `Cloudflare Turnstile is gating this link (sitekey ${params.sitekey}). ` +
          'Set CAPTCHA_PROVIDER + CAPTCHA_API_KEY on page-resolver to solve it.',
        );
      }
      const token = await solveTurnstile({
        websiteURL: page.url(),
        websiteKey: params.sitekey,
        timeoutMs: Math.max(timeoutMs, 120000),
      });
      await injectTurnstileToken(page, token, params.callback);

      if (clickText) {
        await page.getByText(clickText, { exact: false }).first()
          .evaluate((el) => el.click()).catch(() => {});
      }

      const link = await waitForLink(page, resultSelector, resultAttr, pageHost, timeoutMs);
      return { link, filename: (await page.title().catch(() => null)) || null };
    }

    throw new Error('result selector never produced an off-site link');
  } catch (err) {
    if (debug) {
      try {
        err.debugText = (await page.evaluate(() => document.body.innerText)).slice(0, 1500);
      } catch (e) {
        /* ignore */
      }
    }
    throw err;
  } finally {
    await context.close().catch(() => {});
  }
}

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/health') {
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({
      status: 'ok',
      captcha: captchaEnabled() ? CAPTCHA_PROVIDER : false,
      captchaEndpoint: captchaEnabled() ? CAPTCHA_ENDPOINT : undefined,
    }));
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
  console.log(
    `page-resolver on :${PORT} | captcha: ${captchaEnabled() ? CAPTCHA_PROVIDER : 'off'} | blocking ${BLOCK_HOSTS.length} ad hosts`,
  );
});
