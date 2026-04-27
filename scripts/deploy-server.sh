#!/usr/bin/env bash
# Deploy or update the jmw-server on its host (default: core-services).
#
# Usage: scripts/deploy-server.sh [nickname]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

NICK="${1:-core-services}"
HOSTS_FILE="$REPO_ROOT/scripts/hosts.tsv"

row=$(awk -v nick="$NICK" '$0 !~ /^#/ && $1 == nick { print $1 "\t" $2 "\t" $3 "\t" $4 "\t" $5; exit }' "$HOSTS_FILE")
if [[ -z "$row" ]]; then
  echo "no host '$NICK' in $HOSTS_FILE" >&2
  exit 1
fi
IFS=$'\t' read -r _ host user os arch <<<"$row"

if [[ "$os" != "linux" ]]; then
  echo "server deploy currently supports linux only (host '$NICK' is $os)" >&2
  exit 1
fi

VERSION=$(git describe --tags --always --dirty 2>/dev/null || echo dev)
OUT="bin/jmw-server-${os}-${arch}"

echo "==> building $os/$arch (version $VERSION)"
mkdir -p bin
CGO_ENABLED=0 GOOS="$os" GOARCH="$arch" \
  go build -ldflags "-s -w -X github.com/walljm/jmwagent/internal/shared/version.Version=$VERSION" \
  -o "$OUT" ./cmd/server

echo "==> shipping to $user@$host"
scp -q "$OUT" "$user@$host:/tmp/jmw-server.new"

ssh -t "$user@$host" '
  set -e
  sudo install -m 0755 /tmp/jmw-server.new /usr/local/bin/jmw-server
  sudo systemctl restart jmw-server
  rm -f /tmp/jmw-server.new
  sleep 1
  systemctl is-active jmw-server
'
