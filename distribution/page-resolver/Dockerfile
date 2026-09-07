# Headless-browser resolver for click-gated download pages.
# Base = Playwright's image (Chromium + all its libs already installed).
# We use patchright (a patched Playwright drop-in that removes the
# runtime leaks Cloudflare fingerprints) with that same Chromium.
FROM mcr.microsoft.com/playwright:v1.48.2-jammy

ENV NODE_ENV=production \
    PORT=3000 \
    NAV_TIMEOUT_MS=45000 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

WORKDIR /app

COPY package.json ./
RUN npm install --omit=dev --no-audit --no-fund \
    && npx patchright install chromium

COPY server.js captcha.js ./

EXPOSE 3000

USER pwuser

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD node -e "require('http').get('http://127.0.0.1:'+(process.env.PORT||3000)+'/health',r=>process.exit(r.statusCode===200?0:1)).on('error',()=>process.exit(1))"

CMD ["node", "server.js"]
