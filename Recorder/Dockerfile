# Recorder sidecar image.
#
# node:22-bookworm-slim is glibc/x64, which matches the prebuilt binaries for
# @discordjs/voice's bundled DAVE library (@snazzah/davey), and our encryption
# (libsodium-wrappers, WASM) and opus (opusscript, pure JS) deps need no native
# build. So the slim image installs cleanly with no compiler toolchain.
#
# If `npm install` ever fails trying to compile a native dependency, switch the
# base to `node:22-bookworm` and add before the install:
#   RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ && rm -rf /var/lib/apt/lists/*
FROM node:22-bookworm-slim

WORKDIR /app

# Install deps first for better layer caching.
COPY package.json package-lock.json* ./
RUN npm install --omit=dev

COPY . .

ENV NODE_ENV=production
EXPOSE 8080

CMD ["node", "src/index.mjs"]
