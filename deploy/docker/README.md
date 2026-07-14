# Deploying the agent via Docker

For devices where a native binary install isn't practical (NAS appliances,
anything without a package manager or systemd) — run the agent as a
container instead. Native installs (systemd service running the
self-contained binary from a GitHub release) remain the default for
regular Linux/macOS/Windows hosts; see `AGENTS.md` → "Agent".

## Why this looks different from a typical container

The agent's job is to *observe the host it's running on* — hostname, OS,
disks, processes, listening ports. Docker isolates all of those by default,
so a naively-run container reports its own internal identity instead of the
host's. The flags below undo that isolation deliberately, on purpose, for
this one container. There is no code-level "host root" override in the
agent — every flag here is a standard Docker mechanism.

| What | Why it's wrong by default | Fix |
|---|---|---|
| Hostname | Container gets its own UTS namespace | `--uts=host` |
| Listening ports / processes | Container gets its own network + PID namespace | `--network=host --pid=host` |
| `/etc/os-release` | Baked into the base image, not the host's | bind-mount over the same path |
| Docker container inventory | Agent looks for `/var/run/docker.sock` | bind-mount over the same path |
| SMART disk data | `smartctl` needs raw device access | `--privileged`, or one `--device=/dev/sdX` per disk |
| CPU / memory / uptime / `/sys` hardware facts | Already work correctly, no flag needed | procfs counters and sysfs aren't mount-namespaced by Docker |

**Filesystem capacity is the one real limitation.** Mount namespaces
*are* isolated (unlike UTS/PID/network, Docker has no `--mount=host`),
so the agent's filesystem collector (`DriveInfo.GetDrives()`) only sees
mounts that exist inside the container. To report capacity for a NAS
volume, bind-mount it at the same path it has on the host, e.g.
`-v /volume1:/volume1:ro`. Any mount you don't bind-mount this way simply
won't show up — there's no way around that with a container.

## 1. Get the image

CI publishes a multi-arch (`linux/amd64` + `linux/arm64`) image to Docker Hub
on every tagged release:

```
walljm/jmw-agent:latest
walljm/jmw-agent:vX.Y.Z
```

`docker pull walljm/jmw-agent:latest` on the device is normally all you need.
To build it yourself instead:

```sh
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -f deploy/docker/Dockerfile.agent \
  -t walljm/jmw-agent:latest \
  --push \
  .
```

(Drop `--push` and use `--load` with a single `--platform` to build locally
without a registry.)

## 2. Stage the agent config

```sh
sudo mkdir -p /opt/jmw-agent/etc /opt/jmw-agent/data
sudo tee /opt/jmw-agent/etc/agent.json >/dev/null <<'EOF'
{
  "server_url": "https://monitor.example.com",
  "name": "nas-01",
  "zone": "10.0.0.0/8",
  "interval": 30
}
EOF
```

`server_url` must be `https://` (see `AGENTS.md` → "Agent"). `/opt/jmw-agent/data`
is where `agent.json`'s implicit state directory (`/var/lib/jmw-agent` inside
the container, see step 3) persists the generated agent ID and API key —
losing it means the agent re-registers as a brand-new agent on next start.

## 3. Run the container

```sh
docker run -d \
  --name jmw-agent \
  --restart=always \
  --uts=host \
  --network=host \
  --pid=host \
  --privileged \
  -v /etc/os-release:/etc/os-release:ro \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -v /opt/jmw-agent/etc/agent.json:/etc/jmw-agent/agent.json:ro \
  -v /opt/jmw-agent/data:/var/lib/jmw-agent \
  walljm/jmw-agent:latest
```

Add one `-v /volumeN:/volumeN:ro` per host mount point you want capacity
reported for (see the limitation above). If you'd rather not grant
`--privileged`, drop it and add one `--device=/dev/sdX` per disk instead —
`smartctl` needs raw access to the specific device nodes, nothing broader.

## 4. Verify

The agent should appear as **pending** in the server UI. Approve it, then confirm:

- Hostname matches the device's real hostname, not a container ID.
- OS/distro reads the device's real `/etc/os-release`, not the image's Debian base.
- Disks/mounts show the device's real capacity for anything you bind-mounted in step 3.
- `docker restart jmw-agent` keeps the same agent ID (state volume persisted).

## Auto-updating the agent

The native binary has a built-in self-update (server offers a signed newer
build over the heartbeat, agent verifies SHA-256 + signature and re-execs in
place — see `AGENTS.md` → "Agent self-update"). That mechanism replaces a
file on disk; inside a container the binary lives in the image's read-only
layers, so it doesn't apply here, and containerized agents will not offer
themselves an update.

Use [Watchtower](https://containrrr.dev/watchtower/) instead — it polls the
registry and recreates the container when a newer `walljm/jmw-agent:latest`
is pushed:

```sh
docker run -d \
  --name watchtower \
  --restart=always \
  -v /var/run/docker.sock:/var/run/docker.sock \
  containrrr/watchtower --interval 3600 jmw-agent
```

Watchtower recreates the container from the new image using the same
`docker run` flags Docker remembers from the original run — no need to
reissue the command above after an update.
