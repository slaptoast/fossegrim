#!/bin/bash

# Fossegrim Development Runner
# This script runs both the API and Web projects simultaneously

set -e
set -m   # job control: puts each backgrounded `dotnet watch` in its own
         # process group, so we can signal that whole group (watch + the
         # app process it spawns) instead of just the watch wrapper -- see
         # cleanup() below.

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}  Fossegrim Development Environment${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

# Store the script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"

# Check if src directory exists
if [ ! -d "$SRC_DIR" ]; then
    echo -e "${RED}Error: src directory not found${NC}"
    exit 1
fi

CLEANED_UP=false

# `dotnet watch` forks the actual app (`dotnet exec .../*.dll`) as a child
# process and does not reliably forward a plain `kill <watch-pid>` to it --
# it mostly relies on the terminal delivering Ctrl+C to the whole foreground
# process group at once. Without `set -m` above, that left the child running
# after this script exited, orphaned and still holding its port, so the next
# `run-dev.sh` (or dotnet watch's own restart-on-change) would fail with
# "Address already in use". Killing the *negative* PID here signals the
# whole process group instead of just the watch wrapper.
cleanup() {
    if [ "$CLEANED_UP" = true ]; then
        return
    fi
    CLEANED_UP=true

    echo ""
    echo -e "${BLUE}Shutting down servers...${NC}"

    for pid in "$API_PID" "$WEB_PID"; do
        [ -z "$pid" ] && continue
        kill -TERM "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null || true
    done

    # Give them a few seconds to shut down gracefully before forcing it.
    for _ in 1 2 3 4 5; do
        any_alive=false
        for pid in "$API_PID" "$WEB_PID"; do
            [ -z "$pid" ] && continue
            kill -0 "$pid" 2>/dev/null && any_alive=true
        done
        [ "$any_alive" = true ] || break
        sleep 1
    done

    for pid in "$API_PID" "$WEB_PID"; do
        [ -z "$pid" ] && continue
        kill -KILL "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null || true
    done
}

# Set up trap to cleanup on exit
trap cleanup EXIT INT TERM

# Start API server
# stdin is /dev/null, not this terminal: with `set -m` above, each of these
# runs in its own process group, so if dotnet watch tried to read the
# terminal or put it in raw mode (for its interactive "press r to restart"
# keybinds) while backgrounded, the kernel would stop it outright (SIGTTIN/
# SIGTTOU). Non-tty stdin makes dotnet watch skip that entirely.
echo -e "${GREEN}Starting Fossegrim.Api on http://localhost:5182${NC}"
cd "$SRC_DIR/Fossegrim.Api"
dotnet watch run --no-hot-reload < /dev/null > /tmp/fossegrim-api.log 2>&1 &
API_PID=$!

# Wait a bit for API to start
sleep 3

# Start Web server
echo -e "${GREEN}Starting Fossegrim.Web on https://localhost:7049${NC}"
cd "$SRC_DIR/Fossegrim.Web"
dotnet watch run --no-hot-reload < /dev/null > /tmp/fossegrim-web.log 2>&1 &
WEB_PID=$!

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  Servers Running${NC}"
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}API:  http://localhost:5182${NC}"
echo -e "${GREEN}      https://localhost:7182${NC}"
echo -e "${GREEN}Web:  http://localhost:5085${NC}"
echo -e "${GREEN}      https://localhost:7049${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "API Logs: tail -f /tmp/fossegrim-api.log"
echo "Web Logs: tail -f /tmp/fossegrim-web.log"
echo ""
echo "Press Ctrl+C to stop both servers"
echo ""

# Wait for both processes
wait $API_PID $WEB_PID
