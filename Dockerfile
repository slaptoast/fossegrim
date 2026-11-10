# syntax=docker/dockerfile:1
#
# Single-image build: Fossegrim.Api hosts both the JSON API and the built
# Fossegrim.Web (Blazor WebAssembly) static files from one process/port.
# Api.csproj references Web.csproj, so `dotnet publish` on the Api alone
# already builds Web and drops its output into the Api's wwwroot -- see
# Program.cs's UseBlazorFrameworkFiles/UseStaticFiles/MapFallbackToFile.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY src/Fossegrim.Api/Fossegrim.Api.csproj src/Fossegrim.Api/
COPY src/Fossegrim.Web/Fossegrim.Web.csproj src/Fossegrim.Web/
COPY src/Fossegrim.Lib/Fossegrim.Lib.csproj src/Fossegrim.Lib/
COPY src/Fossegrim.Contracts/Fossegrim.Contracts.csproj src/Fossegrim.Contracts/
# Built for this SDK image's own platform (linux-musl-x64 or -arm64), so only
# that platform's native libraries (SkiaSharp, SQLite) go into the image.
# See ApiRuntimeIdentifier in Fossegrim.Api.csproj.
RUN dotnet --info | awk '$1 == "RID:" { print $2; exit }' > /tmp/rid \
    && dotnet restore src/Fossegrim.Api/Fossegrim.Api.csproj -p:ApiRuntimeIdentifier=$(cat /tmp/rid)

COPY src/Fossegrim.Api/ src/Fossegrim.Api/
COPY src/Fossegrim.Web/ src/Fossegrim.Web/
COPY src/Fossegrim.Lib/ src/Fossegrim.Lib/
COPY src/Fossegrim.Contracts/ src/Fossegrim.Contracts/
RUN dotnet publish src/Fossegrim.Api/Fossegrim.Api.csproj -c Release -o /app --no-restore -p:ApiRuntimeIdentifier=$(cat /tmp/rid)

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Set by the release workflow from the git tag that triggered the build
# (e.g. VERSION=1.2.3 for tag v1.2.3), so the running app and its published
# image tag always agree -- see /api/version and the footer that shows it.
# Defaults to "dev" for anything built without it, e.g. local `docker
# compose up --build`.
ARG VERSION=dev

# tzdata: backup schedules run at a wall-clock time in a chosen time zone.
# /usr/local/bin/fossegrim runs the app's commands, e.g.
# `fossegrim reset-password you@example.com` (see DOCKER.md).
RUN apk add --no-cache tzdata \
    && addgroup -S fossegrim && adduser -S fossegrim -G fossegrim \
    && mkdir -p /data /music /backups \
    && printf '#!/bin/sh\nexec dotnet /app/Fossegrim.Api.dll "$@"\n' > /usr/local/bin/fossegrim \
    && chmod 755 /usr/local/bin/fossegrim \
    && chown -R fossegrim:fossegrim /app /data /music /backups

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__DefaultConnection="Data Source=/data/fossegrim.db" \
    Jwt__KeyFile=/data/jwt.key \
    DataProtection__KeysDirectory=/data/dataprotection-keys \
    Covers__CacheDirectory=/data/cover-cache \
    Backup__Directory=/backups \
    APP_VERSION=$VERSION

EXPOSE 8080
VOLUME ["/data", "/music", "/backups"]
USER fossegrim

ENTRYPOINT ["dotnet", "Fossegrim.Api.dll"]
