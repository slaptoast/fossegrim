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

- App: http://localhost (API and Web UI, same origin, through the proxy on
  `HTTP_PORT`, default 80)
- The first visit opens the setup wizard. Create your admin account (the
  only required step), then optionally add media folders (your library is
  at `/music`), Last.fm, accounts and sign-ups, backups and update checks.
  Admins can run the wizard again from the Admin page.

### First-run setup

A fresh install has no accounts. Until the first one exists, the wizard's
"create admin account" step is open to anyone who can reach the server,
and whoever gets there first becomes the admin. Jellyfin and Navidrome work
the same way. Finish it before exposing the server publicly. Once an
account exists, that endpoint is closed for good, and every other setting
is admin-only.

Self-registration is **off** by default: the sign-in page has no "Create
account" link, and admins add accounts under Settings -> Accounts (or in
the wizard). Admins can open sign-ups there too.

Installs from before the wizard already have accounts, so they count as set
up and are never sent through it. Updating them does close
self-registration until an admin opens it again. The old
`admin@fossegrim.local` / `Admin123!` seed account is now only created in
local development (`run-dev.sh`), where the wizard never starts on its own.

### Security

**Use HTTPS.** Out of the box the server answers on plain HTTP. Before
exposing it beyond your own network, either let Fossegrim get a Let's
Encrypt certificate (see [HTTPS](#https)) or put it behind a reverse proxy
of your own that handles TLS (Caddy, Traefik, nginx, ...). Otherwise
passwords and tokens cross the network in the clear.

**Password guessing.** Ten wrong passwords in a row lock an account for
five minutes (the sign-in page says so), and each client address gets 20
sign-in attempts a minute (`RateLimiting__SignInPerMinute`). The bundled
proxy passes on the client's address, so that limit applies per client.
If another proxy sits in front of it, every request comes from that
proxy's address, so the limit is shared by everyone signing in at once,
which is still plenty for a household. If that proxy can rate-limit by the
real client address, that adds a per-client limit on top.

**Stream and cover URLs.** `<audio>` and `<img>` elements can't send an
Authorization header, so their URLs carry a token. It's a separate media
token that only `/stream` and `/cover` accept, so one that turns up in a
proxy's access log can play music but not reach anything else, and it
stops working when that device is signed out. Normal access tokens are
never accepted in a URL.

## HTTPS

`docker-compose.yml` runs two containers: the app, and **the proxy**
(`ghcr.io/slaptoast/fossegrim-proxy`, nginx with Fossegrim's config). Only
the proxy is published:

- **Plain HTTP on `HTTP_PORT`** (default 80) always works. This is what you
  want if another reverse proxy elsewhere on your network already handles
  HTTPS: point it at this port.
- **HTTPS on `HTTPS_PORT`** (default 443) starts as soon as there's a
  certificate in the shared `/certs` volume. The proxy checks every 30
  seconds and reloads nginx when the certificate changes, so renewals need
  no restart. A certificate nginx can't load (say, a key that doesn't
  match) is logged and ignored, and plain HTTP carries on.

### Let's Encrypt

For a server that faces the internet on its own (a Raspberry Pi on a home
connection, say), Fossegrim can get free certificates from
[Let's Encrypt](https://letsencrypt.org/) and renew them itself. Admins set
it up under **Settings -> HTTPS**: enter the domain name(s), optionally an
email address, agree to the subscriber agreement, and press **Get
Certificate**. It usually takes under a minute.

Before you do:

1. Point the domain's DNS at your public IP address (a dynamic DNS name
   works).
2. Forward ports **80** and **443** on your router to the server.
   Let's Encrypt checks each domain by fetching
   `http://<domain>/.well-known/acme-challenge/...` on port 80 (the
   HTTP-01 challenge), so port 80 must be reachable from the internet,
   and `HTTP_PORT` must be 80 or have port 80 forwarded to it. Wildcard
   certificates aren't supported (they need DNS challenges).
3. Try **Test mode** first. It uses Let's Encrypt's staging service, whose
   certificates browsers don't trust, but whose limits are far looser
   than the real service's (5 failed checks per domain per hour). Once a
   test certificate comes through, turn test mode off and get a real one.

Once a certificate is in place:

- **Renewal** is automatic. The app checks every hour and renews when a
  third of the certificate's lifetime is left (30 days for today's 90-day
  certificates). If a request fails, it tries again after 1 hour, then 2,
  4, ... up to once a day. Admins see "HTTPS certificate problem" in the
  footer until it succeeds, and the Settings card says what went wrong.
- **Plain HTTP redirects to HTTPS**, except Let's Encrypt's checks. Turn
  that off on the Settings card if something still needs plain HTTP. (It
  writes an `http-redirect-off` file into `/certs` for the proxy.) There's
  no HSTS, so turning HTTPS off later never leaves browsers stuck.
- **Turn Off** stops renewing and deletes the certificate, so the proxy
  goes back to plain HTTP.

The ACME account keys are stored in the database, encrypted with the
Data Protection keys. The certificate and its key are in the
`fossegrim-certs` volume, and backups include them (under `certs/`).

### Your own certificate

The proxy doesn't care where the certificate came from. To use one you
already have, put the full chain (server certificate first) and its
private key into the volume as `fullchain.pem` and `privkey.pem`:

```bash
docker compose cp fullchain.pem fossegrim:/certs/fullchain.pem
docker compose cp privkey.pem fossegrim:/certs/privkey.pem
```

Settings -> HTTPS shows it. Fossegrim never renews or replaces it unless
you set up Let's Encrypt there.

### Using your own reverse proxy instead

If you'd rather not run the bundled proxy at all, delete the `proxy`
service from `docker-compose.yml` and, in the `fossegrim` service, replace
`expose` with `ports: ["8080:8080"]` and remove `Https__CertificateDirectory`
and `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Settings -> HTTPS then just
says HTTPS is handled elsewhere. (Keep
`ASPNETCORE_FORWARDEDHEADERS_ENABLED` only if nothing but your proxy can
reach port 8080: it makes the app believe the client address in
`X-Forwarded-For`.)

## Cutting a release

Publishing is **version-tag-driven**, not automatic on every push to `main`
(see `.github/workflows/release.yml`). A `pull_request` still gets a
build-only sanity check -- Dockerfile/build breaks get caught in CI, but
nothing is pushed anywhere until you deliberately tag a release:

```bash
git tag v1.2.3
git push origin v1.2.3
```

That one push builds the images and pushes them to the GitHub Container
Registry and Docker Hub:

```
ghcr.io/slaptoast/fossegrim
ghcr.io/slaptoast/fossegrim-proxy
docker.io/slaptoast/fossegrim
docker.io/slaptoast/fossegrim-proxy
```

Each image is built once, so both registries carry the same image under
the same tags.

Both are built for `linux/amd64` and `linux/arm64` (a Raspberry Pi 3, 4
or 5 running a 64-bit OS); Docker pulls the right one. The app's .NET build
always runs on the build machine's own platform and only picks the target
platform's native libraries, so building for arm64 doesn't run the SDK
under emulation (see the Dockerfile). 32-bit ARM isn't built.

tagged `1.2.3`, `1.2`, `1`, and (as long as the tag isn't a prerelease like
`v1.2.3-beta.1`) `latest`. Use semver tags (`vMAJOR.MINOR.PATCH`) -- the
workflow parses the version straight out of the tag, there's no separate
version file to keep in sync.

Once the image is pushed, the workflow creates a
[GitHub Release](https://github.com/slaptoast/fossegrim/releases) for the
tag. Its notes are generated from the pull requests merged since the
previous release, and tags with a hyphen (`v1.2.3-rc.1`) are marked as
prereleases.

On a stable release, the workflow also copies `README.md` (and
`proxy/README.md` for the proxy) to the Docker Hub repository pages, which
don't follow the GitHub repo by themselves.

The workflow logs in to GHCR and creates the release with its own
`GITHUB_TOKEN`. Docker Hub needs two repository secrets (Settings ->
Secrets and variables -> Actions):

- `DOCKERHUB_USERNAME`: the Docker Hub account (`slaptoast`).
- `DOCKERHUB_TOKEN`: a Docker Hub personal access token with **Read, Write,
  Delete** scope. Pushing only needs Read & Write; updating the repository
  description needs Delete as well.

Create the `fossegrim` and `fossegrim-proxy` repositories on Docker Hub (as
public) before the first release. If the login or push to Docker Hub fails,
the whole release fails, GHCR push included, so the two never drift apart.
To check the setup without cutting a release, run the workflow by hand on
`main` (Actions -> Release -> Run workflow): it pushes only a `sha-...` tag
and creates no GitHub Release.

## Using the published image

```bash
docker pull ghcr.io/slaptoast/fossegrim:latest
# or, from Docker Hub
docker pull slaptoast/fossegrim:latest
```

To use the Docker Hub images with Compose, change the two `image:` lines in
`docker-compose.yml` to `slaptoast/fossegrim:latest` and
`slaptoast/fossegrim-proxy:latest`. The update check always reads
ghcr.io, whichever registry you pull from; the tags are the same.

The first time a package is published from a public repo, GHCR may create it
as **private**. If `docker pull` gets a 403/denied for an anonymous pull, go
to the package's page on GitHub (your profile -> Packages) -> Package
settings -> change visibility to Public. The update check reads the tags
anonymously too, so it also needs the package to be public.

## Configuration

All runtime config is environment variables -- nothing needs to be rebuilt
into the image for a given deployment.

| Variable | Purpose |
|---|---|
| `JWT_KEY` | Secret signing key for auth tokens. Optional -- generated automatically if unset. See [Auth signing key](#auth-signing-key). |
| `JWT_ISSUER` / `JWT_AUDIENCE` / `JWT_EXPIRY_MINUTES` | JWT claims, and how long an access token lasts (default 60 minutes; apps renew them with their refresh token). Stream and cover URLs use separate media tokens, which last `Jwt__MediaExpiryMinutes` (default 7 days). |
| `RateLimiting__SignInPerMinute` | Sign-in, registration and password-change attempts allowed per client address per minute (default 20). See [Security](#security). |
| `MUSIC_LIBRARY_PATH` | Host path to your music library; mounted **read-write** at `/music` in the container. After startup, register `/music` (or a subfolder) as a media folder from the admin UI. |
| `HTTP_PORT` | Host port the proxy serves plain HTTP on (default 80). Let's Encrypt needs port 80 to reach it. See [HTTPS](#https). |
| `HTTPS_PORT` | Host port the proxy serves HTTPS on once there's a certificate (default 443). |
| `BACKUP_PATH` | Host folder for backup zips, mounted at `/backups`. Must be writable by uid 100 / gid 101. See [Backups](#backups). |
| `LASTFM_API_KEY` / `LASTFM_API_SECRET` | Last.fm API application credentials. Optional -- usually entered on the admin Settings page instead; when set, these override it. See [Last.fm](#lastfm). |

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
`/data`, makes a new key, and the database is lost with it, so everyone has
to sign in again.

**Supplying your own key.** Generate one and put it in `.env`:

```bash
openssl rand -base64 48
```

```
JWT_KEY=<paste output here>
```

Use at least 32 characters. Once `JWT_KEY` is set, `/data/jwt.key` is
ignored (not deleted).

**Rotating the key** (e.g. if you think it leaked):

- Auto-generated key: delete the file and restart.
  ```bash
  docker compose exec fossegrim rm /data/jwt.key
  docker compose restart fossegrim
  ```
- Your own key: set `JWT_KEY` to a new value and run `docker compose up -d`.

Either way, every token signed with the old key stops working, including
any forged with it. Signed-in browsers and apps don't notice: they swap
their refresh token (stored in the database, not signed with the key) for
new tokens and carry on. To sign a device out, use **Profile -> Signed-in
devices**; changing your password signs out all your other devices.

**Signed-in devices.** Each sign-in (a browser, or an app that sends a
`ClientName` to `/api/auth/login`) is a session that never expires on its
own. It ends when the user signs it out from their Profile page, logs out
on that device, or changes their password, and a signed-out device's
tokens stop working immediately.

**Moving the key file.** `Jwt__KeyFile` controls where the generated key
lives (default `/data/jwt.key`). To keep it outside the data volume, e.g. on
a separate secrets mount, set this in the compose `environment:` section.

**Backups.** A backup of the `fossegrim-data` volume includes `jwt.key`, so
store backups as securely as the key itself. Restoring without the key file
is fine: a new one is generated, and signed-in devices refresh their
tokens as after a rotation.

### Cover cache

Album covers are read from the audio files (or a `cover.jpg`/`cover.png`
beside them), and resized when a client asks for a smaller one
(`/cover/{albumId}?size=256`). Both are cached in `/data/cover-cache` (set
by `Covers__CacheDirectory`). The cache is never backed up and can be
deleted at any time; covers are re-extracted as they're requested.

### Forgotten passwords

"Forgot password?" on the sign-in page works in one of two ways:

- **With email set up** (Settings -> Email): it emails the person a reset
  link. Enter any SMTP server (your email provider's, often with an app
  password, or a service like Mailgun or Postmark), the address emails come
  from, and the address people open Fossegrim on, which emailed links start
  with. "Send a test email" checks it. The SMTP password is stored
  encrypted, like the Last.fm secret.
- **Without email**: it tells the person to ask an admin, who makes them a
  link under Settings -> Accounts -> Reset link and sends it however they
  like.

A link works once, for 24 hours. Using it unlocks the account if too many
wrong passwords had locked it, and signs it out on every device. The page
never says whether an address has an account, and each account gets at most
one reset email every five minutes.

If you're the only admin and can't sign in, get a link from inside the
container:

```sh
docker compose exec fossegrim fossegrim reset-password you@example.com
```

It prints the link (starting with Fossegrim's address if email is set up;
otherwise put the address you open Fossegrim on in front of it).

### Last.fm

Each user can link their own Last.fm account from their Profile page, after
which Fossegrim shows what they're playing on Last.fm ("now playing") and
scrobbles each track once they've heard 90% of it. If Last.fm is unreachable, scrobbles are retried in the background
for up to 14 days, the longest Last.fm accepts; the Listens page shows each
listen's Last.fm status. The server needs one Last.fm
API application for this (it belongs to whoever runs the server, not to any
user; everyone links their own account to it):

1. Create one at https://www.last.fm/api/account/create. The callback URL
   field can be left blank; Fossegrim supplies it on every link.
2. As an admin, enter the API key and shared secret under Settings ->
   Last.fm. They're checked with Last.fm before saving and take effect
   immediately.

Alternatively, set `LASTFM_API_KEY` and `LASTFM_API_SECRET` in `.env` and
`docker compose up -d`. When both are set they take precedence, and the
Settings page shows Last.fm as managed by the environment.

The saved shared secret and users' Last.fm session keys are stored
encrypted in the database. The encryption keys live in
`/data/dataprotection-keys` (set by `DataProtection__KeysDirectory`), so
keep that on the persistent volume along with everything else. If they're
lost, nothing breaks, but the secret has to be re-entered and every user
has to link Last.fm again. Like `jwt.key`, treat backups of `/data` as
sensitive.

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

### Library scans

Scans run in the background, one at a time, with a progress bar on the
Admin page and under Settings -> Configured Folders, and a "Scanning 42%"
note in the top bar for admins.

- **Quick scan** (the default): finds every audio file, then reads tags only
  from files that are new or whose size or modified time changed. Tracks
  whose file is gone are removed. If a file turns up somewhere else with
  the same size and tags, it counts as moved and keeps its play history and
  playlist entries.
- **Full rescan**: re-reads every file.
- **Right-click an album or artist -> Rescan**: re-reads just the folders
  holding its tracks.

As a safeguard, nothing is removed from a media folder that's missing or
has no audio files at all, since that usually means a drive isn't mounted.
The scan reports this as a warning instead.

MP3, FLAC, M4A, OGG and Opus files are scanned and streamed.

#### Starting scans from other applications

Create an API key under **Settings -> API Keys**. The key is shown once,
and only its hash is stored. A key can start scans and check on them,
nothing else:

```bash
# Scan every media folder (quick scan)
curl -X POST -H "X-Api-Key: fsg_..." http://localhost/api/external/scan

# Scan one folder, e.g. from a Lidarr "on import" custom script
curl -X POST -H "X-Api-Key: fsg_..." -H "Content-Type: application/json" \
  -d '{"path": "/music/Artist/Album", "full": false}' \
  http://localhost/api/external/scan

# Queue, current scan with progress, and recent results
curl -H "X-Api-Key: fsg_..." http://localhost/api/external/scan
```

`path` is as Fossegrim sees it, inside the container, so it must be inside
a configured media folder (e.g. `/music/...`). If the other application
mounts the same library at a different path, translate the prefix in your
script. Starting a scan returns `202` with the queued job. Its `id` can be
polled at `/api/external/scan/{id}`. Asking for a scan that's already
waiting returns the waiting one rather than queueing a second.

### Renaming files and library health

**Settings -> File Naming** holds one template for the whole server,
relative to each media folder, e.g. `{artist}/{album}/{track:00} - {title}`.
Tokens are `{artist}`, `{album}`, `{title}`, `{track}` (or `{track:00}`),
`{year}` and `{genre}`; the extension is kept. **Rename files...** on an
album, artist or track's right-click menu (or "Preview renaming the whole
library") shows old -> new names before anything moves.

- A file is never overwritten. Clashes are skipped and listed.
- When every track leaves a folder for the same new folder, the folder's
  other files (`cover.jpg` etc.) go along with them.
- Folders left empty are removed.
- Play history and playlists are unaffected.

**Admin -> Library Health** lists:

- **ID3 tag versions**: MP3s whose old ID3v1 tag disagrees with the ID3v2
  tag Fossegrim reads, which other players may show instead. Fixing
  rewrites ID3v1 from ID3v2. Files are checked as they're scanned, so run
  a full rescan once to check a library scanned before this existed.
- **File names**: files not named as the template says, with a button to
  rename them.

### Update checks

Release images know their own version (`APP_VERSION`, from the release
tag). About a minute after startup and then once a day, the server asks
the GitHub Container Registry (ghcr.io) for the newest stable release of
`ghcr.io/slaptoast/fossegrim` and compares the two. Floating tags (`latest`, `1.2`,
`1`), CI tags (`sha-...`, `pr-...`) and prereleases are ignored. When a
newer release exists, admins see an "Update available" badge in the footer,
and the Admin page shows the details. To update, pull the new image and
recreate the container:

```bash
docker compose pull && docker compose up -d
```

Admins can turn the daily check off on the Admin page. "Check Now" still
works when it's off. Development builds (`dev`, e.g. `docker compose up
--build`) have no version to compare and never contact ghcr.io.

### Backups

Admins can back up from **Settings -> Backups**, either on demand or on a
schedule: every N hours, daily, on chosen weekdays, or on a day of the
month, at a time in a chosen time zone. Each backup is a zip written to
`/backups`, which `docker-compose.yml` maps to `BACKUP_PATH` on the host:

```
fossegrim-backup-20261029-030000.zip
├── fossegrim.db            # consistent copy, taken with SQLite's online backup API
├── jwt.key                 # sign-in key (when generated into /data)
├── dataprotection-keys/    # decrypt the saved Last.fm secret and session keys
├── certs/                  # the HTTPS certificate and key, if there is one
└── manifest.json           # app version, time, last applied migration
```

Point `BACKUP_PATH` at a different disk from the data volume if you can.
A backup on the same disk won't survive losing that disk. The folder has
to be writable by the container's user:

```bash
mkdir -p /path/to/backups && chown 100:101 /path/to/backups
```

After each successful backup, old ones are pruned by the retention
settings: keep the newest N, and/or delete any older than N days. Manual
backups count toward the limits. The newest backup is never pruned, and
files in the folder that aren't named like Fossegrim backups are never
touched. A backup missed while the server was down runs once when it
comes back up.

Backups contain `jwt.key` and the Data Protection keys, so treat them like
the `/data` volume itself. Fossegrim writes each zip readable by the
container user only (mode 600); keep it that way if you copy them
elsewhere.

**Restoring** is manual. Stop the container, replace the contents of
`/data` with the zip's contents, then start it again. With the default
named volume:

```bash
docker compose down
docker run --rm -v fossegrim_fossegrim-data:/data -v /path/to/backups:/backups alpine \
  sh -c 'rm -rf /data/* && unzip -o /backups/fossegrim-backup-20261029-030000.zip -d /data \
         && rm /data/manifest.json && chown -R 100:101 /data'
docker compose up -d
```

To restore the HTTPS certificate too (otherwise Fossegrim just asks Let's
Encrypt for a new one), copy `certs/` into the certificate volume before
starting again:

```bash
docker run --rm -v fossegrim_fossegrim-certs:/certs -v /path/to/backups:/backups alpine \
  sh -c 'unzip -o -j /backups/fossegrim-backup-20261029-030000.zip "certs/*" -d /certs \
         && chown -R 100:101 /certs && chmod 600 /certs/privkey.pem'
```

The restore above puts a `certs/` folder into `/data` too; it's harmless
there, or delete it. (Check the volume names with `docker volume ls`.
They're prefixed with the compose project name.) Restoring onto an older Fossegrim version than the
one that made the backup isn't supported. `manifest.json` records which
version that was.

## Building the image by hand

If you're building/pushing outside of GitHub Actions (e.g. from your own
machine), use `scripts/docker-build.sh`:

```bash
./scripts/docker-build.sh                                    # build, tag :local
REGISTRY=ghcr.io/slaptoast  ./scripts/docker-build.sh --push --tag 1.2.3  # GHCR
REGISTRY=docker.io/slaptoast ./scripts/docker-build.sh --push --tag 1.2.3  # Docker Hub
```

You'll need to be logged in to the registry first (`docker login ghcr.io`,
or `docker login` for Docker Hub)
-- the script doesn't handle auth.
Run `./scripts/docker-build.sh --help` for all options.
