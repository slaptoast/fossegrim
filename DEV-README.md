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
│   └── Fossegrim.Lib/         # Server-side data/services (EF Core, Identity, file scanning) - Api only
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
