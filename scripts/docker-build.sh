#!/bin/bash
# Builds (and optionally pushes) the fossegrim Docker image without relying
# on GitHub Actions. Useful for building from a machine other than GitHub,
# or for testing an image before CI does.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

REGISTRY="${REGISTRY:-}"
TAG="local"
PUSH=false

usage() {
    cat <<EOF
Usage: $(basename "$0") [options]

Options:
  --tag TAG           Image tag to apply (default: local)
  --push              Push the image after building
  --registry REGISTRY Registry/namespace prefix, e.g. ghcr.io/slaptoast
                       (or set the REGISTRY env var). Required with --push.
  -h, --help           Show this help

Examples:
  $(basename "$0")
  $(basename "$0") --tag v1.2.3 --registry ghcr.io/slaptoast --push
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag) TAG="$2"; shift 2 ;;
        --push) PUSH=true; shift ;;
        --registry) REGISTRY="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

if $PUSH && [[ -z "$REGISTRY" ]]; then
    echo "Error: --push requires --registry (or REGISTRY env var)" >&2
    exit 1
fi

image="fossegrim"
if [[ -n "$REGISTRY" ]]; then
    image="$REGISTRY/$image"
fi
ref="$image:$TAG"

echo "==> Building $ref"
docker build -f "$REPO_ROOT/Dockerfile" -t "$ref" "$REPO_ROOT"

if $PUSH; then
    echo "==> Pushing $ref"
    docker push "$ref"
fi
