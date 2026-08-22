#!/bin/bash

# Fossegrim Development Runner
# This script runs both the API and Web projects simultaneously

set -e

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

# Cleanup function to kill background processes
cleanup() {
    echo ""
    echo -e "${BLUE}Shutting down servers...${NC}"
    if [ ! -z "$API_PID" ]; then
        kill $API_PID 2>/dev/null || true
    fi
    if [ ! -z "$WEB_PID" ]; then
        kill $WEB_PID 2>/dev/null || true
    fi
    exit 0
}

# Set up trap to cleanup on exit
trap cleanup EXIT INT TERM

# Start API server
echo -e "${GREEN}Starting Fossegrim.Api on http://localhost:5182${NC}"
cd "$SRC_DIR/Fossegrim.Api"
dotnet watch run --no-hot-reload > /tmp/fossegrim-api.log 2>&1 &
API_PID=$!

# Wait a bit for API to start
sleep 3

# Start Web server
echo -e "${GREEN}Starting Fossegrim.Web on https://localhost:7049${NC}"
cd "$SRC_DIR/Fossegrim.Web"
dotnet watch run --no-hot-reload > /tmp/fossegrim-web.log 2>&1 &
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
