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

### Current Setup

The Web project currently has its own `/api` endpoints that directly access the database. This means:
- The Web project is self-contained and can run independently
- The API endpoints in the Web project mirror those in the API project
- No cross-origin requests are needed when using the Web project alone

### Future Setup (Optional)

If you want the Web project to use the external API project instead:

1. Update `ArtistList/Default.cshtml` to use the external API:
   ```javascript
   const response = await fetch('http://localhost:5182/api/artists');
   ```

2. CORS is already configured in the API project to allow requests from:
   - http://localhost:5085
   - https://localhost:7049

## Project Structure

```
fossegrim/
├── src/
│   ├── Fossegrim.Api/      # Standalone API (port 5182/7182)
│   ├── Fossegrim.Web/      # Web UI with embedded API endpoints (port 5085/7049)
│   └── Fossegrim.Lib/      # Shared library
├── run-dev.sh              # Development runner script
└── DEV-README.md          # This file
```

## Configuration

### API Base URL

The Web project's `appsettings.json` contains:
```json
{
  "ApiBaseUrl": "http://localhost:5182"
}
```

This can be used to configure which API the Web project should use.

### Database

Both projects share the same SQLite database: `fossegrim.db`

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
