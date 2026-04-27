#!/usr/bin/env bash
# Deploy or update the jmw-agent on a single host.
#
# Usage:
#   scripts/deploy-agent.sh <nickname>
#   scripts/deploy-agent.sh --all
#
# Reads host registry from scripts/hosts.tsv and the shared agent.toml
# (server URL, PSK, pinned cert hash) from deploy/secrets/agent.toml.
# Per-OS install paths:
#   linux   -> /usr/local/bin/jmw-agent + /etc/jmw/agent.toml + systemd unit
#   darwin  -> /usr/local/bin/jmw-agent + /usr/local/etc/jmw/agent.toml + launchd plist
#   windows -> C:\Program Files\jmw-agent\jmw-agent.exe + C:\ProgramData\jmw-agent\agent.toml + Scheduled Task

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

HOSTS_FILE="$REPO_ROOT/scripts/hosts.tsv"
SECRETS_FILE="$REPO_ROOT/deploy/secrets/agent.toml"
BIN_DIR="$REPO_ROOT/bin"

if [[ ! -f "$SECRETS_FILE" ]]; then
  echo "missing $SECRETS_FILE — create it with server_url/psk/pinned_sha" >&2
  exit 1
fi

# ---- helpers ----

lookup_host() {
  local nickname="$1"
  awk -v nick="$nickname" '
    $0 !~ /^#/ && NF >= 5 && $1 == nick { print $1 "\t" $2 "\t" $3 "\t" $4 "\t" $5; found=1; exit }
    END { if (!found) exit 2 }
  ' "$HOSTS_FILE"
}

list_all() {
  awk '$0 !~ /^#/ && NF >= 5 { print $1 }' "$HOSTS_FILE"
}

build_binary() {
  local os="$1" arch="$2"
  # hassio is just a packaging target on top of a Linux binary.
  local goos="$os"
  [[ "$goos" == "hassio" ]] && goos="linux"

  local out="$BIN_DIR/jmw-agent-${goos}-${arch}"
  [[ "$goos" == "windows" ]] && out="${out}.exe"

  local version
  version=$(git describe --tags --always --dirty 2>/dev/null || echo dev)

  echo "==> building $goos/$arch (version $version)"
  mkdir -p "$BIN_DIR"
  CGO_ENABLED=0 GOOS="$goos" GOARCH="$arch" \
    go build -ldflags "-s -w -X github.com/walljm/jmwagent/internal/shared/version.Version=$version" \
    -o "$out" ./cmd/agent
  echo "$out"
}

render_config() {
  # $1 = os; emits an agent.toml on stdout with the right id_file path.
  local os="$1" id_file
  case "$os" in
    linux)   id_file="/var/lib/jmw-agent/agent.id" ;;
    darwin)  id_file="/usr/local/var/jmw-agent/agent.id" ;;
    windows) id_file='C:/ProgramData/jmw-agent/agent.id' ;;
    *) echo "unknown os: $os" >&2; return 1 ;;
  esac
  cat "$SECRETS_FILE"
  printf 'id_file       = "%s"\n' "$id_file"
}

# ---- per-OS deploy functions ----

deploy_linux() {
  local user="$1" host="$2" arch="$3" binary="$4"
  local cfg_tmp; cfg_tmp=$(mktemp)
  render_config linux > "$cfg_tmp"

  echo "==> shipping to $user@$host"
  scp -q "$binary" "$cfg_tmp" deploy/systemd/jmw-agent.service "$user@$host:/tmp/"
  rm -f "$cfg_tmp"

  ssh -t "$user@$host" "
    set -e
    sudo install -m 0755 /tmp/$(basename "$binary") /usr/local/bin/jmw-agent
    sudo mkdir -p /etc/jmw /var/lib/jmw-agent
    sudo install -m 0640 /tmp/$(basename "$cfg_tmp") /etc/jmw/agent.toml
    if [ ! -f /etc/systemd/system/jmw-agent.service ]; then
      sudo install -m 0644 /tmp/jmw-agent.service /etc/systemd/system/jmw-agent.service
      sudo systemctl daemon-reload
      sudo systemctl enable jmw-agent
    fi
    sudo systemctl restart jmw-agent
    rm -f /tmp/$(basename "$binary") /tmp/$(basename "$cfg_tmp") /tmp/jmw-agent.service
    sleep 1
    systemctl is-active jmw-agent
  "
}

deploy_darwin() {
  local user="$1" host="$2" arch="$3" binary="$4"
  local cfg_tmp; cfg_tmp=$(mktemp)
  render_config darwin > "$cfg_tmp"

  echo "==> shipping to $user@$host"
  scp -q "$binary" "$cfg_tmp" deploy/launchd/com.walljm.jmw.agent.plist "$user@$host:/tmp/"

  ssh -t "$user@$host" "
    set -e
    sudo install -m 0755 /tmp/$(basename "$binary") /usr/local/bin/jmw-agent
    sudo mkdir -p /usr/local/etc/jmw /usr/local/var/jmw-agent /usr/local/var/log
    sudo install -m 0640 /tmp/$(basename "$cfg_tmp") /usr/local/etc/jmw/agent.toml
    sudo chown -R root:wheel /usr/local/var/jmw-agent
    if [ ! -f /Library/LaunchDaemons/com.walljm.jmw.agent.plist ]; then
      sudo install -m 0644 /tmp/com.walljm.jmw.agent.plist /Library/LaunchDaemons/com.walljm.jmw.agent.plist
      sudo launchctl bootstrap system /Library/LaunchDaemons/com.walljm.jmw.agent.plist
    else
      sudo launchctl kickstart -k system/com.walljm.jmw.agent
    fi
    rm -f /tmp/$(basename "$binary") /tmp/$(basename "$cfg_tmp") /tmp/com.walljm.jmw.agent.plist
    sleep 1
    sudo launchctl print system/com.walljm.jmw.agent | grep -E '^[[:space:]]+state' || true
  "
  rm -f "$cfg_tmp"
}

deploy_windows() {
  local user="$1" host="$2" arch="$3" binary="$4"
  local cfg_tmp; cfg_tmp=$(mktemp)
  render_config windows > "$cfg_tmp"

  echo "==> shipping to $user@$host"
  # Stage with stable names in the user's home dir (install-agent.ps1 reads them).
  scp -q "$binary"               "$user@$host:./jmw-agent.exe"
  scp -q "$cfg_tmp"              "$user@$host:./agent.toml"
  scp -q deploy/windows/install-agent.ps1 "$user@$host:./install-agent.ps1"
  rm -f "$cfg_tmp"

  ssh "$user@$host" 'powershell -NoProfile -ExecutionPolicy Bypass -File .\install-agent.ps1; Remove-Item -ErrorAction SilentlyContinue .\install-agent.ps1'
}

# Home Assistant OS / Supervised. Pushes the local-add-on directory to
# /addons/<slug>/ on the HA host. Supervisor still owns install/start/stop —
# this script only stages the files; the user clicks Install/Start (or
# Rebuild on update) in the HA UI. Requires the *Advanced SSH & Web Terminal*
# add-on to be running with sftp disabled is fine (we use tar over ssh).
#
# Pre-req: walljm:walljm must own /addons/jmw-agent on the HA host. One-time
# setup from the HA Web Terminal:
#   mkdir -p /addons/jmw-agent && chown walljm:walljm /addons/jmw-agent
deploy_hassio() {
  local user="$1" host="$2" arch="$3" binary="$4"

  # Map our arch names to HA BUILD_ARCH names.
  local ha_arch
  case "$arch" in
    arm64) ha_arch="aarch64" ;;
    amd64) ha_arch="amd64" ;;
    *) echo "unsupported hassio arch '$arch'" >&2; return 1 ;;
  esac

  local stage; stage=$(mktemp -d)
  trap "rm -rf $stage" RETURN
  cp -R deploy/hassio-addon/jmw-agent/. "$stage/"
  cp "$binary" "$stage/jmw-agent.${ha_arch}"

  echo "==> shipping add-on to $user@$host:/addons/jmw-agent (HA Supervisor will rebuild)"
  # Stream as tar so we don't depend on sftp; --exclude strips macOS
  # resource forks the BSD tar would otherwise emit.
  tar --exclude '._*' -C "$stage" -czf - . | \
    ssh "$user@$host" 'rm -f /addons/jmw-agent/* && tar -C /addons/jmw-agent -xzf -'

  cat <<EOF

next steps in the HA UI:
  Settings → Apps → Add-on Store → ⋮ → Check for updates
  Local add-ons → JMW Agent
    first time: fill psk + pinned_sha → Install → Start
    update:     ⋮ → Rebuild  (only needed when the binary or Dockerfile changes)
EOF
}

# ---- main ----

deploy_one() {
  local nickname="$1"
  local row
  if ! row=$(lookup_host "$nickname"); then
    echo "no host with nickname '$nickname' in $HOSTS_FILE" >&2
    return 1
  fi
  local _nick host user os arch
  IFS=$'\t' read -r _nick host user os arch <<<"$row"

  echo "===================="
  echo "deploy: $nickname  ($user@$host  $os/$arch)"
  echo "===================="

  local binary; binary=$(build_binary "$os" "$arch" | tail -1)

  case "$os" in
    linux)   deploy_linux   "$user" "$host" "$arch" "$binary" ;;
    darwin)  deploy_darwin  "$user" "$host" "$arch" "$binary" ;;
    windows) deploy_windows "$user" "$host" "$arch" "$binary" ;;
    hassio)  deploy_hassio  "$user" "$host" "$arch" "$binary" ;;
    *) echo "unknown os '$os' for $nickname" >&2; return 1 ;;
  esac
}

usage() {
  cat <<EOF
usage: $0 <nickname>
       $0 --all
       $0 --list

Hosts:
$(awk '$0 !~ /^#/ && NF >= 5 { printf "  %-15s %-15s %s/%s\n", $1, $2, $4, $5 }' "$HOSTS_FILE")
EOF
}

case "${1:-}" in
  ""|-h|--help) usage; exit 0 ;;
  --list) list_all; exit 0 ;;
  --all)
    for nick in $(list_all); do
      deploy_one "$nick" || echo "FAILED: $nick"
    done
    ;;
  *)
    deploy_one "$1"
    ;;
esac
