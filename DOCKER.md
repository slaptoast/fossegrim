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
# edit .env: set MUSIC_LIBRARY_PATH to your music folder

docker compose up --build
```

- App: http://localhost:8080 (API and Web UI, same origin)
- Log in with `admin@fossegrim.local` / `Admin123!` and change the password
  immediately -- that's a hardcoded seed account, not something Docker
  changes for you (`src/Fossegrim.Lib/Services/RoleSeeder.cs`).

## Cutting a release

Publishing is **version-tag-driven**, not automatic on every push to `main`
(see `.github/workflows/release.yml`). A `pull_request` still gets a
build-only sanity check -- Dockerfile/build breaks get caught in CI, but
nothing is pushed anywhere until you deliberately tag a release:

```bash
git tag v1.2.3
git push origin v1.2.3
```

That one push builds the image once and pushes it to **both**:

```
ghcr.io/slaptoast/fossegrim
docker.io/slaptoast/fossegrim
```

tagged `1.2.3`, `1.2`, `1`, and (as long as the tag isn't a prerelease like
`v1.2.3-beta.1`) `latest`. Use semver tags (`vMAJOR.MINOR.PATCH`) -- the
workflow parses the version straight out of the tag, there's no separate
version file to keep in sync.

### One-time setup: Docker Hub credentials

GHCR authenticates for free via the workflow's own `GITHUB_TOKEN` -- nothing
to configure. Docker Hub needs its own access token, which only you can
create (I don't have access to your Docker Hub account or this repo's
GitHub secrets):

1. Docker Hub -> your avatar -> **Account Settings** -> **Security** ->
   **New Access Token**. Give it Read & Write permissions.
2. In the GitHub repo -> **Settings** -> **Secrets and variables** ->
   **Actions** -> **New repository secret**, add two:
   - `DOCKERHUB_USERNAME` -- your Docker Hub username (`slaptoast`)
   - `DOCKERHUB_TOKEN` -- the access token from step 1

Until both secrets exist, a tag push will fail at the Docker Hub login step
(GHCR will still succeed on its own).

## Using the published image

```bash
docker pull docker.io/slaptoast/fossegrim:latest
# or
docker pull ghcr.io/slaptoast/fossegrim:latest
```

The first time a package is published from a public repo, GHCR may create it
as **private**. If `docker pull` gets a 403/denied for an anonymous pull, go
to the package's page on GitHub (your profile -> Packages) -> Package
settings -> change visibility to Public. Docker Hub repos default to public
already.

## Configuration

All runtime config is environment variables -- nothing needs to be rebuilt
into the image for a given deployment.

| Variable | Purpose |
|---|---|
| `JWT_KEY` | Secret signing key for auth tokens. Optional -- generated automatically if unset. See [Auth signing key](#auth-signing-key). |
| `JWT_ISSUER` / `JWT_AUDIENCE` / `JWT_EXPIRY_MINUTES` | JWT claims/expiry. Defaults match dev settings. |
| `MUSIC_LIBRARY_PATH` | Host path to your music library; mounted **read-write** at `/music` in the container. After startup, register `/music` (or a subfolder) as a media folder from the admin UI. |
| `PORT` | Host-side port to publish. |

### Auth signing key

Fossegrim signs login tokens with a secret key. Anyone who has that key can
forge a token for any user, admins included, so keep it private. You don't
have to set it up -- the container picks a key in this order:

1. **`JWT_KEY` / `Jwt__Key`**, if set to a non-empty value. Use this to
   manage the key yourself, e.g. from a secrets manager or so several
   instances share one key.
2. **The key file `/data/jwt.key`**, if it exists. If it doesn't, a random
   512-bit key is generated and written there on first start (readable
   only by the container user). This is the default for a normal install.

You'll see one of these lines in the startup logs:

```
Generated new JWT signing key at /data/jwt.key
Loaded JWT signing key from /data/jwt.key
```

(Neither line appears when `JWT_KEY` is set.)

**Keep `/data` on a persistent volume.** The compose file uses the
`fossegrim-data` named volume. With plain `docker run`, mount one yourself
(`-v fossegrim-data:/data`). Otherwise a recreated container gets an empty
`/data`, makes a new key, and everyone is signed out (and the database is
lost too).

**Supplying your own key.** Generate one and put it in `.env`:

```bash
openssl rand -base64 48
```

```
JWT_KEY=<paste output here>
```

Use at least 32 characters. Once `JWT_KEY` is set, `/data/jwt.key` is
ignored (not deleted).

**Rotating the key** (e.g. if you think it leaked, or to force everyone to
sign in again):

- Auto-generated key: delete the file and restart.
  ```bash
  docker compose exec fossegrim rm /data/jwt.key
  docker compose restart fossegrim
  ```
- Your own key: set `JWT_KEY` to a new value and run `docker compose up -d`.

Either way, every existing session ends and users have to sign in again.
Nothing else is affected: accounts, passwords, and the library are stored
separately.

**Moving the key file.** `Jwt__KeyFile` controls where the generated key
lives (default `/data/jwt.key`). To keep it outside the data volume, e.g. on
a separate secrets mount, set this in the compose `environment:` section.

**Backups.** A backup of the `fossegrim-data` volume includes `jwt.key`, so
store backups as securely as the key itself. Restoring without the key file
is fine: a new one is generated and users just sign in again.

### Music folder must be writable

Fossegrim assumes read-write access to `/music`: it edits ID3 tags in place
today, and will write fetched album art alongside your audio files in the
future. The container runs as an unprivileged, fixed user (`uid=100,
gid=101` -- Alpine's `fossegrim` system user), so whatever host folder you
point `MUSIC_LIBRARY_PATH` at needs to already be writable by that
uid/gid before you start the container, e.g.:

```bash
chown -R 100:101 /path/to/your/music
```

This is a one-time setup step per host/folder -- it's on you as the
operator to set permissions accordingly; the container doesn't attempt to
adjust its own permissions or the folder's ownership for you.

The API's SQLite database persists in the `fossegrim-data` named volume
(`/data/fossegrim.db` inside the container), independent of the image,
alongside the generated JWT signing key (`/data/jwt.key`). Treat that
volume as secret: anyone who can read `jwt.key` can mint login tokens.

Because the API serves the SPA itself, there's no separate public URL to
configure for the frontend and no CORS setup needed for this deployment --
everything is same-origin. (`Cors:AllowedOrigins` in `appsettings.json`
still exists and is only relevant if you ever run Web as a separate process
against this Api, the way local dev does.)

## Building the image by hand

If you're building/pushing outside of GitHub Actions (e.g. from your own
machine), use `scripts/docker-build.sh`:

```bash
./scripts/docker-build.sh                                    # build, tag :local
REGISTRY=slaptoast          ./scripts/docker-build.sh --push --tag 1.2.3  # Docker Hub
REGISTRY=ghcr.io/slaptoast  ./scripts/docker-build.sh --push --tag 1.2.3  # GHCR
```

You'll need to be logged in to whichever registry you're pushing to
(`docker login` / `docker login ghcr.io`) -- the script doesn't handle auth.
Run `./scripts/docker-build.sh --help` for all options.
