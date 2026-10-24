# Fossegrim Development Guide

## Running the Development Environment

### Quick Start

Run both the API and Web projects simultaneously:

```bash
./run-dev.sh
```

This will start:
- **API Server**: http://localhost:5182 (https://localhost:7182)
- **Web Server**: http://localhost:5085 (https://localhost:7049)

### Viewing Logs

The script outputs logs to temporary files:

```bash
# API logs
tail -f /tmp/fossegrim-api.log

# Web logs
tail -f /tmp/fossegrim-web.log
```

### Stopping the Servers

Press `Ctrl+C` in the terminal running the script to stop both servers.

## Architecture

`Fossegrim.Web` is a standalone Blazor WebAssembly SPA - it runs entirely in the
browser and has no server-side code or database access of its own. It
authenticates against `Fossegrim.Api`'s `/api/auth` endpoints, stores the JWT
it gets back in the browser's `localStorage`, and calls every other `/api/...`
endpoint directly as a bearer-token client (the same way a mobile app would).
CORS on the Api allows the Web origins:
- http://localhost:5085
- https://localhost:7049

## Project Structure

```
fossegrim/
├── src/
│   ├── Fossegrim.Api/         # Standalone API (port 5182/7182) - owns the database
│   ├── Fossegrim.Web/         # Blazor WebAssembly SPA (port 5085/7049) - browser-only client of the Api
│   ├── Fossegrim.Contracts/   # Shared DTOs referenced by both Api and Web
│   ├── Fossegrim.Lib/         # Server-side data/services (EF Core, Identity, file scanning) - Api only
│   └── Fossegrim.Api.Tests/   # xUnit tests; runs the Api in-process against a throwaway SQLite db
├── run-dev.sh              # Development runner script
└── DEV-README.md          # This file
```

## Configuration

### API Base URL

The Web project's `wwwroot/appsettings.json` contains:
```json
{
  "ApiBaseUrl": "http://localhost:5182"
}
```

This is loaded by the Blazor WASM app at startup to configure which Api it talks to.

### Database

Only `Fossegrim.Api` touches the database (`fossegrim.db`, SQLite). `Fossegrim.Web` has no database access.

### Auth signing key

There's no JWT key in `appsettings.json`. Locally, with no key configured,
the Api makes a new random key every time it starts, so **every restart
(including `dotnet watch` rebuilds) signs you out**. The Web app handles the
resulting 401 by sending you back to the login page.

To stay signed in across restarts, give the Api a key file or a fixed key:

```bash
cd src/Fossegrim.Api
# either: generated once, then reused
dotnet user-secrets set "Jwt:KeyFile" "$HOME/.fossegrim/jwt.key"
# or: a fixed key
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)"
```

User-secrets are stored outside the repo, so they never get committed. See
`DOCKER.md` → *Auth signing key* for the full lookup order.

### CORS Policy

The API project allows CORS requests from the Web project origins:
- http://localhost:5085
- https://localhost:7049

## Development Workflow

1. Start both servers: `./run-dev.sh`
2. Navigate to https://localhost:7049 for the Web UI
3. Use https://localhost:7182/openapi for API documentation
4. Make changes - both projects use `dotnet watch` for hot reload
5. Stop servers with `Ctrl+C`

## Tests

```bash
dotnet test src/Fossegrim.sln
```

`Fossegrim.Api.Tests` starts the real API in-process (`WebApplicationFactory`)
with its own temporary SQLite database, migrated on startup just like
production, so endpoint tests go through auth, routing and EF Core for real.
`DatabaseTests` fails if the EF model changed without a migration. CI runs
the suite on every pull request (`.github/workflows/test.yml`).
