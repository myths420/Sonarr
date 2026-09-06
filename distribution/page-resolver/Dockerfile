# Headless-browser resolver for click-gated download pages.
# The Playwright image already ships the matching Chromium + all its libs.
FROM mcr.microsoft.com/playwright:v1.48.2-jammy

ENV NODE_ENV=production \
    PORT=3000 \
    NAV_TIMEOUT_MS=45000

WORKDIR /app

COPY package.json ./
RUN npm install --omit=dev --no-audit --no-fund

COPY server.js ./

EXPOSE 3000

# Non-root user shipped by the Playwright image.
USER pwuser

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD node -e "require('http').get('http://127.0.0.1:'+(process.env.PORT||3000)+'/health',r=>process.exit(r.statusCode===200?0:1)).on('error',()=>process.exit(1))"

CMD ["node", "server.js"]
