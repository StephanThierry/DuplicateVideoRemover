# Duplicate Video Remover - web interface in a container (e.g. on a Synology NAS).
#
#   docker build -t duplicate-video-remover .
#   docker run -d -p 8095:8080 -v /volume1/docker/dvr:/data -v /volume1/video:/videos duplicate-video-remover
#
# Then open http://<nas>:8095/?token=...  (the token is printed in the container log and kept in /data/.token)
#
# Volumes
#   /data    settings, database, screenshots, output. Keep it on the NAS (bind mount or named volume).
#   /videos  your videos. Mount one or more folders here and add them under "Folders to scan" in Settings.
#
# Environment
#   DVR_TOKEN  password-like token for the page (default: generated once, stored in /data/.token). "off" disables it.
#   DVR_DB     put the database and screenshots in another folder than /data (e.g. a second volume)
#
# The command-line version is in the image too:  docker exec <container> dvr-cli   (or dvr-cli clean / index)

# ---- Build (runs on the build machine's CPU, produces for the target CPU: amd64 or arm64) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet publish apps/Web -c Release -a $TARGETARCH --self-contained false -o /out/web \
 && dotnet publish apps/Cli -c Release -a $TARGETARCH --self-contained false -o /out/cli

# ---- Run ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# FFmpeg reads the videos and makes the screenshots
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /out/web /app/web
COPY --from=build /out/cli /app/cli
RUN printf '#!/bin/sh\nexec dotnet /app/cli/duplicatevideoremover.dll "$@"\n' > /usr/local/bin/dvr-cli \
 && chmod +x /usr/local/bin/dvr-cli \
 && mkdir -p /data /videos \
 && chown app:app /data

ENV DVR_DATA=/data \
    DVR_URLS=http://0.0.0.0:8080 \
    HOME=/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Runs as the image's non-root user. On a Synology, run it as the NAS user that owns the
# shared folders instead (docker-compose: user: "1026:100") - see docker-compose.yml.
USER app
VOLUME /data
EXPOSE 8080
WORKDIR /data
ENTRYPOINT ["dotnet", "/app/web/DuplicateVideoRemover.Web.dll", "--no-browser"]
