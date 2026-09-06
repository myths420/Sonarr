'use strict';

// Cloudflare Turnstile solver. Two API dialects:
//
//  CAPTCHA_PROVIDER=capsolver | 2captcha     -> CapSolver-style
//     createTask / getTaskResult (JSON tasks). Native for CapSolver;
//     works for 2captcha via api.2captcha.com.
//
//  CAPTCHA_PROVIDER=legacy                    -> the old 2captcha
//     in.php / res.php form API, which most cheap / free-tier clones use
//     (azcaptcha, anycaptcha, cap.guru, ...). Set CAPTCHA_ENDPOINT to the
//     service's base URL, e.g. https://azcaptcha.com
//
// Common:
//   CAPTCHA_API_KEY=...
//   CAPTCHA_ENDPOINT=...   (override the default base URL)

const PROVIDER = (process.env.CAPTCHA_PROVIDER || 'capsolver').toLowerCase();
const API_KEY = process.env.CAPTCHA_API_KEY || '';

const DEFAULT_ENDPOINT = {
  capsolver: 'https://api.capsolver.com',
  '2captcha': 'https://api.2captcha.com',
  legacy: 'https://2captcha.com',
}[PROVIDER] || 'https://api.capsolver.com';

const ENDPOINT = (process.env.CAPTCHA_ENDPOINT || DEFAULT_ENDPOINT).replace(/\/+$/, '');

const enabled = () => !!API_KEY;

async function postJson(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  return res.json();
}

async function solveCapSolver({ websiteURL, websiteKey, action, cData, pagedata, timeoutMs }) {
  const task = { type: 'AntiTurnstileTaskProxyLess', websiteURL, websiteKey };
  const meta = {};
  if (action) meta.action = action;
  if (cData) meta.cdata = cData;
  if (pagedata) meta.chlPageData = pagedata;
  if (Object.keys(meta).length) task.metadata = meta;

  const created = await postJson(`${ENDPOINT}/createTask`, { clientKey: API_KEY, task });
  if (created.errorId || !created.taskId) {
    throw new Error(`createTask failed: ${created.errorDescription || created.errorCode || JSON.stringify(created)}`);
  }

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 3000));
    const got = await postJson(`${ENDPOINT}/getTaskResult`, { clientKey: API_KEY, taskId: created.taskId });
    if (got.errorId) throw new Error(`getTaskResult failed: ${got.errorDescription || got.errorCode}`);
    if (got.status === 'ready') {
      const token = got.solution && (got.solution.token || got.solution.gRecaptchaResponse);
      if (!token) throw new Error('solver returned no token');
      return token;
    }
  }
  throw new Error('captcha solve timed out');
}

async function solveLegacy({ websiteURL, websiteKey, action, cData, timeoutMs }) {
  const inParams = new URLSearchParams({
    key: API_KEY,
    method: 'turnstile',
    sitekey: websiteKey,
    pageurl: websiteURL,
    json: '1',
  });
  if (action) inParams.set('action', action);
  if (cData) inParams.set('data', cData);

  const submit = await fetch(`${ENDPOINT}/in.php`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: inParams.toString(),
  }).then((r) => r.json());

  if (String(submit.status) !== '1') {
    throw new Error(`in.php rejected: ${submit.request || submit.error_text || JSON.stringify(submit)}`);
  }
  const id = submit.request;

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 5000));
    const got = await fetch(`${ENDPOINT}/res.php?key=${encodeURIComponent(API_KEY)}&action=get&id=${id}&json=1`)
      .then((r) => r.json());
    if (String(got.status) === '1') return got.request;
    if (got.request && got.request !== 'CAPCHA_NOT_READY') {
      throw new Error(`res.php error: ${got.request}`);
    }
  }
  throw new Error('captcha solve timed out');
}

async function solveTurnstile(args) {
  if (!enabled()) throw new Error('no CAPTCHA_API_KEY configured');
  const opts = { timeoutMs: 120000, ...args };
  return PROVIDER === 'legacy' ? solveLegacy(opts) : solveCapSolver(opts);
}

module.exports = { solveTurnstile, captchaEnabled: enabled, CAPTCHA_PROVIDER: PROVIDER, CAPTCHA_ENDPOINT: ENDPOINT };
