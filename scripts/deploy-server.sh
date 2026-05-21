#!/usr/bin/env bash
# Deploy or update the jmw-server.
#
# Unlike the agent, the server has no self-updater, so this script
# is used both for initial install and for routine updates.
#
# Usage: scripts/deploy-server.sh <user@host> [arch]
#
# arch defaults to amd64. OS is always linux (the server only
# ships for linux today).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <user@host> [arch]" >&2
  exit 1
fi

target="$1"
arch="${2:-amd64}"
os="linux"

VERSION=$(git describe --tags --always --dirty 2>/dev/null || echo dev)
OUT="bin/jmw-server-${os}-${arch}"

echo "==> building $os/$arch (version $VERSION)"
mkdir -p bin
CGO_ENABLED=0 GOOS="$os" GOARCH="$arch" \
  go build -ldflags "-s -w -X github.com/walljm/jmwagent/internal/shared/version.Version=$VERSION" \
  -o "$OUT" ./cmd/server

echo "==> shipping to $target"
scp -q "$OUT" "$target:/tmp/jmw-server.new"
scp -q deploy/systemd/jmw-server.service "$target:/tmp/jmw-server.service"

ssh -t "$target" '
  set -e
  sudo mkdir -p /opt/jmw/bin
  sudo install -m 0755 /tmp/jmw-server.new /opt/jmw/bin/jmw-server
  sudo install -m 0644 /tmp/jmw-server.service /etc/systemd/system/jmw-server.service
  sudo systemctl daemon-reload
  # Clean up legacy /usr/local/bin install if present.
  if [ -f /usr/local/bin/jmw-server ]; then sudo rm -f /usr/local/bin/jmw-server; fi
  sudo systemctl restart jmw-server
  rm -f /tmp/jmw-server.new /tmp/jmw-server.service
  sleep 1
  systemctl is-active jmw-server
'
