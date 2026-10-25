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

# Process-group IDs of the running servers, so a later run can stop them if
# this one dies without cleaning up (e.g. `kill -9`).
PID_FILE=/tmp/fossegrim-dev.pids

# `dotnet watch` forks the actual app (`dotnet exec .../*.dll`) as a child
# process and does not reliably forward a plain `kill <watch-pid>` to it --
# it mostly relies on the terminal delivering Ctrl+C to the whole foreground
# process group at once. Without `set -m` above, that left the child running
# after this script exited, orphaned and still holding its port, so the next
# `run-dev.sh` (or dotnet watch's own restart-on-change) would fail with
# "Address already in use". Killing the *negative* PID here signals the
# whole process group instead of just the watch wrapper.
#
# dotnet watch often ignores TERM entirely when it has no terminal, so the
# KILL after the grace period is what actually guarantees it's gone.
stop_groups() {
    for pid in "$@"; do
        [ -z "$pid" ] && continue
        kill -TERM "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null || true
    done

    # Give them a few seconds to shut down gracefully before forcing it.
    for _ in 1 2 3 4 5; do
        any_alive=false
        for pid in "$@"; do
            [ -z "$pid" ] && continue
            { kill -0 "-$pid" || kill -0 "$pid"; } 2>/dev/null && any_alive=true
        done
        [ "$any_alive" = true ] || break
        sleep 1
    done

    for pid in "$@"; do
        [ -z "$pid" ] && continue
        kill -KILL "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null || true
    done
}

cleanup() {
    if [ "$CLEANED_UP" = true ]; then
        return
    fi
    CLEANED_UP=true

    # The terminal may already be gone (window closed), so nothing here may
    # abort on a failed write.
    set +e
    [ -n "$WATCHDOG_PID" ] && kill -KILL "$WATCHDOG_PID" 2>/dev/null

    echo "" 2>/dev/null
    echo -e "${BLUE}Shutting down servers...${NC}" 2>/dev/null

    stop_groups "$API_PID" "$WEB_PID"
    rm -f "$PID_FILE"
}

# EXIT covers normal exits; the signal traps turn Ctrl+C, a closed terminal
# window (HUP), etc. into an exit so EXIT runs cleanup exactly once.
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
trap 'exit 131' QUIT

# Stop anything a previous run left behind. Only touch PIDs that are still
# dotnet processes, in case the OS has reused one for something else.
if [ -f "$PID_FILE" ]; then
    stale=()
    for pid in $(cat "$PID_FILE"); do
        ps -o command= -p "$pid" 2>/dev/null | grep -q dotnet && stale+=("$pid")
    done
    if [ ${#stale[@]} -gt 0 ]; then
        echo -e "${BLUE}Stopping servers left over from a previous run...${NC}"
        stop_groups "${stale[@]}"
    fi
    rm -f "$PID_FILE"
fi

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
echo "$API_PID" > "$PID_FILE"

# Wait a bit for API to start
sleep 3

# Start Web server
echo -e "${GREEN}Starting Fossegrim.Web on https://localhost:7049${NC}"
cd "$SRC_DIR/Fossegrim.Web"
dotnet watch run --no-hot-reload < /dev/null > /tmp/fossegrim-web.log 2>&1 &
WEB_PID=$!
echo "$WEB_PID" >> "$PID_FILE"

# Watchdog: if this script is killed in a way no trap can catch (`kill -9`,
# a crash), stop the servers once it's gone. It ignores the signals that
# would end this script so it outlives it; cleanup() kills it on a normal
# exit.
SCRIPT_PID=$$
(
    trap '' HUP INT TERM QUIT
    while kill -0 "$SCRIPT_PID" 2>/dev/null; do sleep 1; done
    stop_groups "$API_PID" "$WEB_PID"
    rm -f "$PID_FILE"
) < /dev/null > /dev/null 2>&1 &
WATCHDOG_PID=$!

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
