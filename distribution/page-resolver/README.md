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

## Wire it into Sonarr

Set **Settings → Indexers → (your AnimeSite indexer) → Page Resolver URL**
to `http://page-resolver:3000` (compose) or `http://<server-ip>:3000`.

The Scraping Script then calls `host.resolvePage(url)` for hosts that need
it (the default LuciferDonghua script does this for `misterdonghua.in`).
