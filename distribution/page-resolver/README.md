# page-resolver

A tiny headless-browser (Playwright/Chromium) service that resolves
download links from pages that need a **click** to generate them — which
FlareSolverr / Byparr can't do. Built for `misterdonghua.in` (the download
host behind LuciferDonghua), but the endpoint is generic.

## Run it

```bash
cd distribution/page-resolver
docker build -t page-resolver .
docker run -d --name page-resolver --restart unless-stopped \
  -p 3000:3000 --shm-size=1g page-resolver
```

`--shm-size=1g` matters — Chromium crashes with the default 64 MB.

Health check: `curl http://localhost:3000/health` → `{"status":"ok"}`

## API

```
POST /resolve
{
  "url": "https://misterdonghua.in/#<hash>&dl=1",
  "clickText": "Get Video",                       // optional
  "resultSelector": "a[download], a[href*=\"/download?\"]",  // optional
  "resultAttr": "href",                            // optional
  "timeoutMs": 45000                               // optional
}
```

Success → `200 {"link": "https://<ip>/.../file.mp4/download?title=...", "filename": "...", "elapsedMs": 1234}`
Failure → `502 {"error": "...", "elapsedMs": 1234}`

## Turnstile (vik1ngfile)

Some hosts put a Cloudflare Turnstile widget in front of the link that
won't even render in an automated browser. Set a solver and the resolver
handles it automatically (detects the sitekey, solves, injects the token,
re-fires the flow):

```yaml
environment:
  # CapSolver / 2captcha (JSON createTask API):
  - CAPTCHA_PROVIDER=capsolver        # or: 2captcha
  - CAPTCHA_API_KEY=CAP-XXXXXXXX

  # OR a cheap / free-tier 2captcha clone (azcaptcha, anycaptcha, ...)
  # which use the old in.php / res.php form API:
  # - CAPTCHA_PROVIDER=legacy
  # - CAPTCHA_ENDPOINT=https://azcaptcha.com
  # - CAPTCHA_API_KEY=your-key
```

`GET /health` → `{"status":"ok","captcha":"legacy","captchaEndpoint":"https://azcaptcha.com"}`
when a solver is active. Without a key, a Turnstile page fails fast with
`502 {"error":"Cloudflare Turnstile is gating this link ..."}`.

## Dailymotion

`GET /dailymotion/fetch?v=<id>[&referer=<page>][&max=1080]` -> streams
`video/mp4`.

The Chinese-anime sites embed the real episode video from Dailymotion.
This drives the embed in the headless browser, grabs the HLS master,
picks the highest variant (<= `max`, default 1080), and `ffmpeg`-remuxes
it to a single MP4 piped straight to the caller. Sonarr's AnimeSite
release resolver adds this automatically as the top pick for any episode
page with a Dailymotion embed, whenever a Page Resolver URL is set.

Needs `ffmpeg` (bundled in the image).

## Wire it into Sonarr

Set **Settings → Indexers → (your AnimeSite indexer) → Page Resolver URL**
to `http://page-resolver:3000` (compose) or `http://<server-ip>:3000`.

The Scraping Script then calls `host.resolvePage(url)` for hosts that need
it (the default LuciferDonghua script does this for `misterdonghua.in`).
