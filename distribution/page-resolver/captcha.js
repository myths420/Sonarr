'use strict';

// Cloudflare Turnstile solver. Uses a CapSolver-compatible createTask /
// getTaskResult API -- CapSolver natively, 2captcha via its newer
// api.2captcha.com endpoint (same task shapes). Set:
//
//   CAPTCHA_PROVIDER=capsolver | 2captcha       (default capsolver)
//   CAPTCHA_API_KEY=...
//   CAPTCHA_ENDPOINT=https://api.capsolver.com  (override if needed)

const PROVIDER = (process.env.CAPTCHA_PROVIDER || 'capsolver').toLowerCase();
const API_KEY = process.env.CAPTCHA_API_KEY || '';
const ENDPOINT = (process.env.CAPTCHA_ENDPOINT ||
  (PROVIDER === '2captcha' ? 'https://api.2captcha.com' : 'https://api.capsolver.com')
).replace(/\/+$/, '');

const enabled = () => !!API_KEY;

async function postJson(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  return res.json();
}

// Solve a Turnstile challenge for `websiteKey` on `websiteURL`.
// Returns the token string, or throws.
async function solveTurnstile({ websiteURL, websiteKey, action, cData, pagedata, timeoutMs = 120000 }) {
  if (!enabled()) {
    throw new Error('no CAPTCHA_API_KEY configured');
  }

  const task = {
    type: 'AntiTurnstileTaskProxyLess',
    websiteURL,
    websiteKey,
  };
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
    if (got.errorId) {
      throw new Error(`getTaskResult failed: ${got.errorDescription || got.errorCode}`);
    }
    if (got.status === 'ready') {
      const token = got.solution && (got.solution.token || got.solution.gRecaptchaResponse);
      if (!token) throw new Error('solver returned no token');
      return token;
    }
  }
  throw new Error('captcha solve timed out');
}

module.exports = { solveTurnstile, captchaEnabled: enabled, CAPTCHA_PROVIDER: PROVIDER };
