# Deploying jmw-agent to an ASUSTOR NAS (Docker)

Tested on ADM 4.x, x86_64 models (AS6810T, AS5304T). For ARM Lockerstor/Drivestor
models, swap `linux/amd64` for `linux/arm64` in the build command and image tag.

The agent uses the `JMW_HOST_ROOT` environment variable so a single binary works
both natively (env unset) and inside a container observing the host (env set
to the bind-mount point — `/host` in this image). Without this prefix the agent
would report container internals (overlayfs, container hostname, etc.) instead
of the actual NAS.

## 1. Install Docker on the NAS

ADM → App Central → install **Portainer** (pulls in the Docker engine
dependency). Enable SSH under Settings → Services if you want to manage from
the command line.

## 2. Build the image

From the repo root on your dev machine:

```sh
docker buildx build \
  --platform=linux/amd64 \
  -f deploy/docker/Dockerfile.agent \
  --build-arg VERSION="$(git describe --tags --always --dirty)" \
  -t walljm/jmw-agent:latest \
  --push .
```

(Substitute your own registry. For ARM NAS units use `--platform=linux/arm64`.)
The `VERSION` build-arg is stamped into the binary and shown in the server UI;
omitting it makes the agent report `dev`. The `scripts/deploy-agent.sh --publish`
helper wraps this command, builds multi-arch (amd64 + arm64), tags both
`latest` and the current git-describe (e.g. `v1.3.0`), and pushes to Docker
Hub. Override the repo with `DOCKER_HUB_REPO=...`.

## 3. Stage the agent config on the NAS

SSH into the NAS and create a config directory:

```sh
sudo mkdir -p /volume1/jmw-agent/etc /volume1/jmw-agent/data
sudo chown -R 65532:65532 /volume1/jmw-agent
sudo tee /volume1/jmw-agent/etc/agent.toml <<'EOF'
server_url    = "https://192.168.1.54:8443"
psk           = "<your PSK>"
pinned_sha    = "<server cert SHA-256>"
id_file       = "/data/agent.id"
interval_secs = 30
EOF
```

The container runs as the `nonroot` user (UID 65532), hence the `chown`.

## 4. Run the container

```sh
docker run -d \
  --name jmw-agent \
  --restart=always \
  --network=host \
  --pid=host \
  --privileged \
  -v /:/host:ro \
  -v /volume1/jmw-agent/data:/data \
  -v /volume1/jmw-agent/etc:/etc/jmw-agent:ro \
  walljm/jmw-agent:latest
```

Flag rationale:

- `--network=host` — agent sees the NAS's real interfaces and hostname (used
  by REQ-038 ICMP probes and by network inventory). Without it the agent's
  socket-table scan falls back to parsing `/host/proc/1/net/{tcp,tcp6,udp,udp6}`,
  which still surfaces host listening ports but without process names/PIDs.
- `--pid=host` — needed for `who`, `ps`, and the listening-port collector to
  see host processes. Drop this if you only care about the metrics under
  `--network=host` + `JMW_HOST_ROOT`.
- `--privileged` — required for `smartctl` to open raw disk devices
  (`/dev/sda`, etc.) and read SMART data. If you prefer a tighter grant, omit
  `--privileged` and instead pass one `--device=/dev/sdX` flag per disk.
- `-v /:/host:ro` — host filesystem mounted read-only; the agent reads `/proc`,
  `/sys`, `/etc/os-release`, `/etc/hostname`, and `statfs`'s mountpoints
  through this prefix.
- `-v /volume1/jmw-agent/data:/data` — persists the agent's identity file so
  re-creating the container does not register a new agent.

## 5. Verify

The new agent should appear as **pending** in the server UI at
`https://192.168.1.54:8443`. Approve it. Confirm:

- `Hostname` is the NAS's name (set under Settings → General → Server Name in
  ADM), not a container ID.
- `OS / Distro` reads "ADM" or the underlying Linux distro string from the
  NAS's `/etc/os-release`.
- Disks show `/volume1`, `/volume2`, etc. with the NAS's actual capacity.
- `Virtualization` is `none` (the dockerenv check is bypassed when
  `JMW_HOST_ROOT` is set).

Reboot the NAS once and confirm the container restarts automatically and
keeps the same agent ID.

## What this deployment does NOT give you

- **Survival across major ADM upgrades**: ADM 4 → 5 has historically required
  Docker reinstalls. You'll need to redo step 1 if that happens.
- **Docker container inventory**: the agent's Docker collector reads
  `/var/run/docker.sock` from inside its own filesystem. To inventory the
  containers running on the NAS, also bind-mount the socket:
  `-v /var/run/docker.sock:/var/run/docker.sock:ro`.

## Auto-updating the agent

The agent has a built-in self-update mechanism (downloads a new binary from
the server, verifies SHA-256, and re-execs in place). Inside Docker, however,
the binary lives on the container's read-only image layer, so the in-process
updater cannot rewrite it. Two options:

1. **Watchtower (recommended for Docker)**: run
   [`containrrr/watchtower`](https://containrrr.dev/watchtower/) on the NAS
   to pull `walljm/jmw-agent:latest` on a schedule. The server's heartbeat
   response will still advertise updates, but the container-side updater will
   fail with a permission error (logged at WARN and harmless). Watchtower
   handles the actual swap by recreating the container.

   ```sh
   docker run -d --name watchtower --restart=always \
     -v /var/run/docker.sock:/var/run/docker.sock \
     containrrr/watchtower --interval 3600 jmw-agent
   ```

2. **Rebuild + redeploy manually**: re-run `scripts/deploy-agent.sh --publish`
   on your dev machine to push a new image tag, then `docker pull
   walljm/jmw-agent:latest && docker rm -f jmw-agent` on the NAS and re-run
   the `docker run` command from step 4.

For Linux/macOS bare-metal installs and Windows installs, no extra setup is
needed — the agent updates itself when the server publishes a new release.
