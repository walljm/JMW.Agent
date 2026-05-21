#!/usr/bin/env bash
# Install a new jmw-server binary and restart the service.
#
# Run as root (via the gha-runner sudoers rule). Takes one argument:
# the absolute path to the new server binary (typically in the
# runner's workspace under .../dist/jmw-server-linux-amd64).
#
# Validates that the source is a regular file before doing anything.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "must run as root" >&2
  exit 1
fi

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <path-to-new-jmw-server>" >&2
  exit 2
fi

src="$1"

if [[ ! -f "$src" ]]; then
  echo "source not found or not a regular file: $src" >&2
  exit 3
fi

install -m 0755 "$src" /opt/jmw/bin/jmw-server
systemctl restart jmw-server
sleep 2
systemctl is-active jmw-server
