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
COPY src/Fossegrim.Lib/fossegrim.lib.csproj src/Fossegrim.Lib/
COPY src/Fossegrim.Contracts/Fossegrim.Contracts.csproj src/Fossegrim.Contracts/
RUN dotnet restore src/Fossegrim.Api/Fossegrim.Api.csproj

COPY src/Fossegrim.Api/ src/Fossegrim.Api/
COPY src/Fossegrim.Web/ src/Fossegrim.Web/
COPY src/Fossegrim.Lib/ src/Fossegrim.Lib/
COPY src/Fossegrim.Contracts/ src/Fossegrim.Contracts/
RUN dotnet publish src/Fossegrim.Api/Fossegrim.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S fossegrim && adduser -S fossegrim -G fossegrim \
    && mkdir -p /data /music \
    && chown -R fossegrim:fossegrim /app /data /music

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__DefaultConnection="Data Source=/data/fossegrim.db"

EXPOSE 8080
VOLUME ["/data", "/music"]
USER fossegrim

ENTRYPOINT ["dotnet", "Fossegrim.Api.dll"]
