# Running Fossegrim in Docker

Fossegrim ships as a **single image**. `Fossegrim.Api` hosts both the JSON
API and the built Blazor WebAssembly app (`Fossegrim.Web`) from one process
on one port -- the standard ASP.NET "hosted Blazor WebAssembly" pattern.
`Fossegrim.Api.csproj` references `Fossegrim.Web.csproj`, so `dotnet
publish` on the Api automatically builds Web and copies its static output
into the Api's own `wwwroot`; `Program.cs` serves it via
`UseBlazorFrameworkFiles`/`UseStaticFiles`/`MapFallbackToFile`. One image,
one version, one tag to bump.

(Local development is unchanged -- `run-dev.sh` still runs Api and Web as
two separate `dotnet watch` processes on separate ports; see
`DEV-README.md`. The single-image build only matters for `docker build`.)

## Quick start (local build)

```bash
cp .env.example .env
# edit .env: set JWT_KEY, and MUSIC_LIBRARY_PATH to your music folder

docker compose up --build
```

- App: http://localhost:8080 (API and Web UI, same origin)
- Log in with `admin@fossegrim.local` / `Admin123!` and change the password
  immediately -- that's a hardcoded seed account, not something Docker
  changes for you (`src/Fossegrim.Lib/services/RoleSeeder.cs`).

## Using the published image

CI builds and pushes the image to GitHub Container Registry on every push
to `main` (see `.github/workflows/docker-publish.yml`):

```bash
docker pull ghcr.io/slaptoast/fossegrim:latest
```

Tagged as `latest` (on `main`), by branch name, by semver (on `vX.Y.Z`
tags), and by short commit SHA.

The first time a package is published from a public repo, GHCR may create it
as **private**. If `docker pull` gets a 403/denied for an anonymous pull, go
to the package's page on GitHub (your profile -> Packages) -> Package
settings -> change visibility to Public.

## Configuration

All runtime config is environment variables -- nothing needs to be rebuilt
into the image for a given deployment.

| Variable | Purpose |
|---|---|
| `JWT_KEY` | Secret signing key for auth tokens. **Required**, no default -- generate with `openssl rand -base64 48`. |
| `JWT_ISSUER` / `JWT_AUDIENCE` / `JWT_EXPIRY_MINUTES` | JWT claims/expiry. Defaults match dev settings. |
| `MUSIC_LIBRARY_PATH` | Host path to your music library; mounted read-only at `/music` in the container. After startup, register `/music` (or a subfolder) as a media folder from the admin UI. |
| `PORT` | Host-side port to publish. |

The API's SQLite database persists in the `fossegrim-data` named volume
(`/data/fossegrim.db` inside the container), independent of the image.

Because the API serves the SPA itself, there's no separate public URL to
configure for the frontend and no CORS setup needed for this deployment --
everything is same-origin. (`Cors:AllowedOrigins` in `appsettings.json`
still exists and is only relevant if you ever run Web as a separate process
against this Api, the way local dev does.)

## Building the image by hand

If you're building/pushing outside of GitHub Actions (e.g. from your own
machine), use `scripts/docker-build.sh`:

```bash
./scripts/docker-build.sh                      # build, tag :local
REGISTRY=ghcr.io/slaptoast ./scripts/docker-build.sh --push --tag v1.2.3
```

Run `./scripts/docker-build.sh --help` for all options.
