#!/usr/bin/env bash
# Initial deploy of the jmw-agent to a single host.
#
# Routine updates are handled by the agent's own self-updater (see
# internal/agent/updater); this script only exists for first-time
# installation on a new host, or for forcing a reinstall when the
# self-updater is wedged.
#
# Usage:
#   scripts/deploy-agent.sh <user@host> <os> <arch>
#   scripts/deploy-agent.sh --publish [--tag <extra-tag>]
#
# os:   linux | darwin | windows | hassio
# arch: amd64 | arm64
#
# Reads the shared agent.toml (server URL, PSK, pinned cert hash) from
# deploy/secrets/agent.toml. Per-OS install paths:
#   linux   -> /opt/jmw/bin/jmw-agent + /etc/jmw/agent.toml + systemd unit
#   darwin  -> /opt/jmw/bin/jmw-agent + /usr/local/etc/jmw/agent.toml + launchd plist
#   windows -> C:\Program Files\jmw-agent\jmw-agent.exe + C:\ProgramData\jmw-agent\agent.toml + Scheduled Task
#   hassio  -> /addons/jmw-agent/ on the HA host (Supervisor rebuilds)
#
# --publish builds & pushes a multi-arch (linux/amd64,linux/arm64) image to
# Docker Hub at $DOCKER_HUB_REPO (default: walljm/jmw-agent), tagged with
# `latest` and the current `git describe`.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

SECRETS_FILE="$REPO_ROOT/deploy/secrets/agent.toml"
BIN_DIR="$REPO_ROOT/bin"

# ---- helpers ----

build_binary() {
  local os="$1" arch="$2"
  # hassio is just a packaging target on top of a Linux binary.
  local goos="$os"
  [[ "$goos" == "hassio" ]] && goos="linux"

  local out="$BIN_DIR/jmw-agent-${goos}-${arch}"
  [[ "$goos" == "windows" ]] && out="${out}.exe"

  local version
  version=$(git describe --tags --always --dirty 2>/dev/null || echo dev)

  echo "==> building $goos/$arch (version $version)" >&2
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
  local target="$1" binary="$2"
  local cfg_tmp; cfg_tmp=$(mktemp)
  render_config linux > "$cfg_tmp"

  echo "==> shipping to $target"
  scp -q "$binary" "$cfg_tmp" deploy/systemd/jmw-agent.service "$target:/tmp/"
  rm -f "$cfg_tmp"

  ssh -t "$target" "
    set -e
    sudo mkdir -p /opt/jmw/bin /etc/jmw /var/lib/jmw-agent
    sudo install -m 0755 /tmp/$(basename "$binary") /opt/jmw/bin/jmw-agent
    sudo install -m 0640 /tmp/$(basename "$cfg_tmp") /etc/jmw/agent.toml
    sudo install -m 0644 /tmp/jmw-agent.service /etc/systemd/system/jmw-agent.service
    sudo systemctl daemon-reload
    sudo systemctl enable jmw-agent
    # Clean up legacy /usr/local/bin install if present.
    if [ -f /usr/local/bin/jmw-agent ]; then sudo rm -f /usr/local/bin/jmw-agent; fi
    sudo systemctl restart jmw-agent
    rm -f /tmp/$(basename "$binary") /tmp/$(basename "$cfg_tmp") /tmp/jmw-agent.service
    sleep 1
    systemctl is-active jmw-agent
  "
}

deploy_darwin() {
  local target="$1" binary="$2"
  local cfg_tmp; cfg_tmp=$(mktemp)
  render_config darwin > "$cfg_tmp"

  echo "==> shipping to $target"
  scp -q "$binary" "$cfg_tmp" deploy/launchd/com.walljm.jmw.agent.plist "$target:/tmp/"

  ssh -t "$target" "
    set -e
    sudo mkdir -p /opt/jmw/bin /usr/local/etc/jmw /usr/local/var/jmw-agent /usr/local/var/log
    sudo install -m 0755 /tmp/$(basename "$binary") /opt/jmw/bin/jmw-agent
    sudo install -m 0640 /tmp/$(basename "$cfg_tmp") /usr/local/etc/jmw/agent.toml
    sudo chown -R root:wheel /usr/local/var/jmw-agent
    # Always install the latest plist (path may have changed).
    sudo install -m 0644 /tmp/com.walljm.jmw.agent.plist /Library/LaunchDaemons/com.walljm.jmw.agent.plist
    if sudo launchctl print system/com.walljm.jmw.agent >/dev/null 2>&1; then
      sudo launchctl bootout system/com.walljm.jmw.agent || true
      # launchctl bootstrap right after bootout intermittently fails with
      # 'Bootstrap failed: 5: Input/output error' while the previous job is
      # still tearing down. Wait for it to actually disappear, then bootstrap.
      for _ in 1 2 3 4 5; do
        sudo launchctl print system/com.walljm.jmw.agent >/dev/null 2>&1 || break
        sleep 1
      done
    fi
    sudo launchctl bootstrap system /Library/LaunchDaemons/com.walljm.jmw.agent.plist
    # Clean up legacy /usr/local/bin install if present.
    if [ -f /usr/local/bin/jmw-agent ]; then sudo rm -f /usr/local/bin/jmw-agent; fi
    rm -f /tmp/$(basename "$binary") /tmp/$(basename "$cfg_tmp") /tmp/com.walljm.jmw.agent.plist
    sleep 1
    sudo launchctl print system/com.walljm.jmw.agent | grep -E '^[[:space:]]+state' || true
  "
  rm -f "$cfg_tmp"
}

deploy_windows() {
  local target="$1" binary="$2"
  local cfg_tmp; cfg_tmp=$(mktemp)
  render_config windows > "$cfg_tmp"

  echo "==> shipping to $target"
  # Stage with stable names in the user's home dir (install-agent.ps1 reads them).
  scp -q "$binary"               "$target:./jmw-agent.exe"
  scp -q "$cfg_tmp"              "$target:./agent.toml"
  scp -q deploy/windows/install-agent.ps1 "$target:./install-agent.ps1"
  rm -f "$cfg_tmp"

  ssh "$target" 'powershell -NoProfile -ExecutionPolicy Bypass -File .\install-agent.ps1; Remove-Item -ErrorAction SilentlyContinue .\install-agent.ps1'
}

# Home Assistant OS / Supervised. Pushes the local-add-on directory to
# /addons/<slug>/ on the HA host. Supervisor still owns install/start/stop —
# this script only stages the files; the user clicks Install/Start (or
# Rebuild on update) in the HA UI. Requires the *Advanced SSH & Web Terminal*
# add-on to be running.
#
# Pre-req: the SSH user must own /addons/jmw-agent on the HA host. One-time
# setup from the HA Web Terminal:
#   mkdir -p /addons/jmw-agent && chown <user>:<user> /addons/jmw-agent
deploy_hassio() {
  local target="$1" arch="$2" binary="$3"

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

  echo "==> shipping add-on to $target:/addons/jmw-agent (HA Supervisor will rebuild)"
  # Stream as tar so we don't depend on sftp; --exclude strips macOS
  # resource forks the BSD tar would otherwise emit.
  tar --exclude '._*' -C "$stage" -czf - . | \
    ssh "$target" 'rm -f /addons/jmw-agent/* && tar -C /addons/jmw-agent -xzf -'

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
  local target="$1" os="$2" arch="$3"

  echo "===================="
  echo "deploy: $target  ($os/$arch)"
  echo "===================="

  local binary; binary=$(build_binary "$os" "$arch" | tail -1)

  case "$os" in
    linux)   deploy_linux   "$target" "$binary" ;;
    darwin)  deploy_darwin  "$target" "$binary" ;;
    windows) deploy_windows "$target" "$binary" ;;
    hassio)  deploy_hassio  "$target" "$arch" "$binary" ;;
    *) echo "unknown os '$os'" >&2; return 1 ;;
  esac
}

usage() {
  cat <<EOF
usage: $0 <user@host> <os> <arch>
       $0 --publish [--tag <extra-tag>]

  os:   linux | darwin | windows | hassio
  arch: amd64 | arm64

This script is for *initial* deploys to a new host. Running agents
self-update via the built-in updater; you should not need to re-run
this for routine releases.
EOF
}

# publish_docker_image builds a multi-arch container image of the agent
# and pushes it to Docker Hub using buildx. Tagged as `latest` plus the
# current git-describe (e.g. v1.2.0). Set DOCKER_HUB_REPO to override
# the default repo. Requires `docker buildx` and a logged-in client.
publish_docker_image() {
  local repo="${DOCKER_HUB_REPO:-walljm/jmw-agent}"
  local extra_tag="$1" # optional caller-supplied additional tag
  local version
  version=$(git describe --tags --always --dirty 2>/dev/null || echo dev)

  if ! command -v docker >/dev/null 2>&1; then
    echo "docker not found in PATH — cannot publish" >&2
    return 1
  fi
  if ! docker buildx version >/dev/null 2>&1; then
    echo "docker buildx not available — install/enable buildx and retry" >&2
    return 1
  fi

  # Reuse a named builder so the cache survives across invocations.
  if ! docker buildx inspect jmw-builder >/dev/null 2>&1; then
    docker buildx create --name jmw-builder --use >/dev/null
  else
    docker buildx use jmw-builder >/dev/null
  fi

  local tag_args=( -t "${repo}:${version}" -t "${repo}:latest" )
  if [[ -n "$extra_tag" ]]; then
    tag_args+=( -t "${repo}:${extra_tag}" )
  fi

  echo "==> publishing ${repo} (tags: ${version}, latest${extra_tag:+, $extra_tag}) for linux/amd64,linux/arm64"
  docker buildx build \
    --platform linux/amd64,linux/arm64 \
    --build-arg "VERSION=${version}" \
    -f deploy/docker/Dockerfile.agent \
    "${tag_args[@]}" \
    --push \
    .
}

case "${1:-}" in
  ""|-h|--help) usage; exit 0 ;;
  --publish)
    shift
    extra_tag=""
    if [[ "${1:-}" == "--tag" ]]; then
      extra_tag="${2:-}"
      shift 2 || true
    fi
    publish_docker_image "$extra_tag"
    ;;
  *)
    if [[ $# -lt 3 ]]; then
      usage; exit 1
    fi
    if [[ ! -f "$SECRETS_FILE" ]]; then
      echo "missing $SECRETS_FILE — create it with server_url/psk/pinned_sha" >&2
      exit 1
    fi
    deploy_one "$1" "$2" "$3"
    ;;
esac
