# JMW Agent — Home Assistant Add-on

Reports this Home Assistant host to a JMW server (inventory, heartbeat, network
probes, container list).

## Install (local add-on)

Supervisor builds the add-on image on the HA host from this directory. The
agent binary itself is pre-built on the dev machine and copied in.

### 1. Build the agent binaries

```sh
make build-linux-arm64   # HA Green / RPi
make build-linux-amd64   # NUC / x86 mini-PC
```

### 2. Stage the add-on directory

```sh
STAGE=/tmp/jmw-agent-addon
rm -rf "$STAGE" && cp -r deploy/hassio-addon/jmw-agent "$STAGE"
cp bin/jmw-agent-linux-arm64 "$STAGE/jmw-agent.aarch64"
cp bin/jmw-agent-linux-amd64 "$STAGE/jmw-agent.amd64"
# armv7 is declared in build.yaml but `make` doesn't cross-compile it — add a
# target with GOARCH=arm GOARM=7 if you need it.
```

### 3. Copy to `/addons/jmw-agent/` on the HA host

Easiest: install the **Samba share** add-on, then from your Mac:
```sh
cp -r /tmp/jmw-agent-addon /Volumes/addons/jmw-agent
```
(Or use *Studio Code Server*, or `scp` via *Advanced SSH & Web Terminal*.)

### 4. Install in Home Assistant

1. Settings → Add-ons → ⋮ → **Check for updates** (forces a `/addons` rescan).
2. **Local add-ons** → **JMW Agent**.
3. **Configuration** tab → fill in `psk` and `pinned_sha`. Get the SHA from
   the server: `openssl s_client -connect <server>:8443 -showcerts </dev/null \
   2>/dev/null | openssl x509 -noout -fingerprint -sha256`. Save.
4. **Info** tab → Install (Supervisor builds the image) → Start.
5. **Log** tab → confirm `Starting jmw-agent → https://…`.
6. Approve the new agent in the JMW server UI.

## Updating

Rebuild binary, re-stage, re-copy to `/addons/jmw-agent/`, bump `version:` in
`config.yaml`, then in HA: **Add-on → ⋮ → Rebuild**.

## Why an add-on instead of a raw `docker run`?

- Supervisor manages restarts, version pinning, log rotation, and survives
  HAOS major upgrades that wipe non-Supervisor containers.
- Config lives in the HA UI, not a hand-edited TOML file on disk.
- Add-on options are validated by Supervisor's schema.
- No need to disable Protection mode on the SSH add-on.

## Required permissions (already set in `config.yaml`)

| Flag | Why |
|---|---|
| `host_network: true` | Agent reports the host's real interfaces and hostname. |
| `host_pid: true` | Listening-port and process collectors see host processes. The host's filesystem is reachable via `/proc/1/root/...` thanks to this flag — that's what `JMW_HOST_ROOT` points at. |
| `full_access: true` | Privileged container (capabilities + AppArmor disabled) so the agent can read `/proc/1/root/proc`, `/proc/1/root/sys`, etc. without permission errors. |
| `docker_api: true` | Docker collector inventories other HA add-ons. |

These together are equivalent to a `--privileged --network=host --pid=host
-v /var/run/docker.sock:/var/run/docker.sock:ro` `docker run` (host filesystem
is read through `/proc/1/root` rather than an explicit bind mount).
