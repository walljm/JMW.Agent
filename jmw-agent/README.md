# JMW Agent - Home Assistant Add-on

Reports this Home Assistant host to a JMW server (inventory, heartbeat, network
probes, container list).

## Install

The normal install path is a Home Assistant add-on repository backed by a
prebuilt Docker image. Supervisor pulls `docker.io/walljm/jmw-agent-ha:<version>`
from Docker Hub; it does not build from a copied `/addons` directory.

1. Settings -> Add-ons -> Add-on Store -> menu -> **Repositories**.
2. Add `https://github.com/walljm/JMW.Agent`.
3. **JMW Agent Add-ons** -> **JMW Agent**.
4. **Configuration** tab -> fill in `psk` and `pinned_sha`. Get the SHA from
   the server: `openssl s_client -connect <server>:8443 -showcerts </dev/null \
   2>/dev/null | openssl x509 -noout -fingerprint -sha256`. Save.
5. **Info** tab -> Install -> Start.
6. **Log** tab -> confirm `Starting jmw-agent -> https://...`.
7. Approve the new agent in the JMW server UI.

## Updating

For a release:

1. Bump `version:` in `jmw-agent/config.yaml` to the release version without
   the `v` prefix, e.g. `2.3.0` for tag `v2.3.0`.
2. Tag and push the release. The release workflow builds and pushes
   `docker.io/walljm/jmw-agent-ha:<version>`.
3. In HA: Add-on Store -> menu -> **Check for updates**, then update
   **JMW Agent**.

No SSH/SCP copy is needed for routine updates. The release workflow fails if
the add-on manifest version does not match the pushed release tag.

## Break-Glass Local Update

If the add-on repository or registry path is unavailable, the SSH helper can
still stage a local add-on directory on the HA host:
```sh
scripts/deploy-agent.sh walljm@<ha-host> hassio arm64
```
Then in HA: **Add-on -> menu -> Rebuild**.

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
| `host_pid: true` | Listening-port and process collectors see host processes. |
| `full_access: true` | Privileged container access for host `/proc`, `/sys`, devices, and mount facts. |
| `docker_api: true` | Docker collector inventories other HA add-ons. |

These together are equivalent to a `--privileged --network=host --pid=host`
container with Docker socket access, but Supervisor owns the lifecycle and
configuration.
