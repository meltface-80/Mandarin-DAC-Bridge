# Mandarin DAC Bridge for Linux (DietPi, Debian, Ubuntu…), x86-64 or arm64.
#
#   docker compose up -d --build
#
# Needs the host's network (UPnP discovery is multicast) and its sound
# devices (/dev/snd) — see docker-compose.yml.

# The ALSA helper: a small C program, built here.
FROM node:22-bookworm-slim AS helper
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc libc6-dev libasound2-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY helper/ helper/
RUN gcc -O2 -Wall -o dachelper helper/dachelper-linux.c -lasound -lpthread

FROM node:22-bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg libasound2 ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY package.json bridge.js ./
COPY lib/ lib/
COPY public/ public/
COPY --from=helper /src/dachelper bin/dachelper
ENV PORT=55500 DATA_DIR=/data NODE_ENV=production
VOLUME /data
EXPOSE 55500
STOPSIGNAL SIGTERM
CMD ["node", "bridge.js"]
