#!/usr/bin/env bash
# Install agent binaries for a tagged release into the server's releases
# directory so the auto-updater can serve them.
#
# Run as root (via the gha-runner sudoers rule). Takes two arguments:
#   $1  version  — must match strict semver vMAJOR.MINOR.PATCH
#   $2  srcdir   — directory containing the built agent binaries
#
# Files are installed as user `jmw` (the server's runtime user) at:
#   /var/lib/jmw/releases/<version>/jmw-agent-<os>-<arch>[.exe]
#   /var/lib/jmw/releases/<version>/jmw-agent-<os>-<arch>[.exe].sig
#
# The script enumerates an explicit list of expected binaries — anything
# else in srcdir is ignored.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "must run as root" >&2
  exit 1
fi

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <version> <srcdir>" >&2
  exit 2
fi

version="$1"
srcdir="$2"

if [[ ! "$version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "version must be strict semver vX.Y.Z, got: $version" >&2
  exit 3
fi

if [[ ! -d "$srcdir" ]]; then
  echo "srcdir not found or not a directory: $srcdir" >&2
  exit 4
fi

dest="/var/lib/jmw/releases/${version}"
install -d -o jmw -g jmw -m 0755 "$dest"

binaries=(
  "jmw-agent-linux-amd64"
  "jmw-agent-linux-arm64"
  "jmw-agent-darwin-amd64"
  "jmw-agent-darwin-arm64"
  "jmw-agent-windows-amd64.exe"
)

for name in "${binaries[@]}"; do
  src="${srcdir}/${name}"
  if [[ ! -f "$src" ]]; then
    echo "missing expected binary: $src" >&2
    exit 5
  fi
  sig="${src}.sig"
  if [[ ! -f "$sig" ]]; then
    echo "missing expected update signature: $sig" >&2
    exit 6
  fi
  install -o jmw -g jmw -m 0755 "$src" "${dest}/${name}"
  install -o jmw -g jmw -m 0644 "$sig" "${dest}/${name}.sig"
done

# Copy SHA256SUMS too if present (informational; server doesn't read it).
if [[ -f "${srcdir}/SHA256SUMS" ]]; then
  install -o jmw -g jmw -m 0644 "${srcdir}/SHA256SUMS" "${dest}/SHA256SUMS"
fi

echo "published ${#binaries[@]} signed agent binaries to $dest"
