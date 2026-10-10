# Mandarin DAC Bridge for Linux (DietPi, Debian, Ubuntu…), x86-64 or arm64.
#
#   docker compose up -d --build
#
# Needs the host's network (UPnP discovery is multicast) and its sound
# devices (/dev/snd) — see docker-compose.yml.
#
# Spotify Connect (optional) is Spotify Soloist, which may not be
# redistributed: the page's Download button fetches it from Spotify into
# /data. PulseAudio is here for it: each DAC's Soloist plays into a private
# PulseAudio server with no sound card, only a pipe the bridge reads.
#
# Qobuz Connect (optional) is QobuzProxy, which is Python: the page's Install
# puts it in a private Python environment in /data. Caldera Headless
# (optional) is downloaded by the page too.

# Built ahead of time (Native AOT) to one program: no .NET in the final image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY src/MandarinDacBridge/ src/MandarinDacBridge/
RUN dotnet publish src/MandarinDacBridge -c Release -o /out --nologo

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg pulseaudio libatomic1 python3 python3-venv \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/mandarin-dac-bridge /app/mandarin-dac-bridge
ENV PORT=55500 DATA_DIR=/data
VOLUME /data
EXPOSE 55500
STOPSIGNAL SIGTERM
ENTRYPOINT ["/app/mandarin-dac-bridge"]
