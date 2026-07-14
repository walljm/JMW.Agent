# Manual deploy notes

We are still in testing and are **not** using the `release.yml` CI/CD tag-push
flow yet (as of 2026-07-14 it's also wired to the wrong stack — see below).
Deploys are done by hand, directly to each host over SSH. This doc is the
reference so that doesn't have to get re-derived each time.

## Hosts

| Host | Role | Reached via | Arch/OS |
|---|---|---|---|
| `core.home` | Server (`jmw-server`) + one agent (`jmw-agent`, zone `local`, name "core") | `ssh core.home` (`walljm@192.168.1.54`, passwordless sudo) | x86_64, Ubuntu 24.04 |
| `cloud.home` | Agent only (`jmw-agent`, zone `cloud`, name "cloud") | `ssh cloud.home` (`walljm`, passwordless sudo) | x86_64, Debian 12 |

Both agents point at `https://agents.core.home` (Caddy in front of the
`jmw-server` systemd unit on core.home, Step CA cert chain).

## Build (from a dev machine, once for both targets)

```sh
# Server — self-contained single-file linux-x64
dotnet publish src/Server.Web/JMW.Discovery.Server.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o /tmp/jmw-publish/server

# Agent — self-contained single-file linux-x64 (same binary ships to both hosts)
dotnet publish src/Agent/JMW.Discovery.Agent.csproj -r linux-x64 -c Release \
  -p:PublishSingleFile=true -p:SelfContained=true -o /tmp/jmw-publish/agent
```

Run `dotnet build JMW.Discovery.slnx -c Release` and
`dotnet test test/Unit/JMW.Discovery.UnitTests.csproj` first — confirm a clean
build and green unit tests before publishing/deploying.

## Deploy: server (core.home)

```sh
scp /tmp/jmw-publish/server/JMW.Discovery.Server core.home:/tmp/JMW.Discovery.Server.new
rsync -az --delete src/Server.Web/wwwroot/ core.home:/tmp/jmw-wwwroot-new/
ssh core.home '
  sudo install -o jmw -g jmw -m 0755 /tmp/JMW.Discovery.Server.new /opt/jmw/bin/JMW.Discovery.Server
  sudo rsync -a --delete /tmp/jmw-wwwroot-new/ /var/lib/jmw/wwwroot/
  sudo chown -R jmw:jmw /var/lib/jmw/wwwroot
  rm -rf /tmp/JMW.Discovery.Server.new /tmp/jmw-wwwroot-new
  sudo systemctl restart jmw-server
  sudo systemctl is-active jmw-server
'
```

Binary: `/opt/jmw/bin/JMW.Discovery.Server` (owned `jmw:jmw`). Static content
root: `/var/lib/jmw/wwwroot` (`WorkingDirectory=/var/lib/jmw` in the unit —
wwwroot must be deployed there, not next to the binary). Config:
`/etc/jmw/server.env`. Unit: `jmw-server.service`.

## Deploy: agent (core.home)

```sh
scp /tmp/jmw-publish/agent/JMW.Discovery.Agent core.home:/tmp/JMW.Discovery.Agent.new
ssh core.home '
  sudo systemctl stop jmw-agent
  sudo install -m 0755 /tmp/JMW.Discovery.Agent.new /home/walljm/jmw-test/JMW.Discovery.Agent
  rm -f /tmp/JMW.Discovery.Agent.new
  sudo systemctl start jmw-agent
  sudo systemctl is-active jmw-agent
'
```

Binary lives at `/home/walljm/jmw-test/JMW.Discovery.Agent` on this host (not
`/opt/jmw/bin` — that's cloud.home's convention, see below). Config:
`/home/walljm/jmw-test/core-agent.json`. State dir: `state-gwifi/` alongside
it. Unit: `jmw-agent.service`.

## Deploy: agent (cloud.home)

```sh
scp /tmp/jmw-publish/agent/JMW.Discovery.Agent cloud.home:/tmp/JMW.Discovery.Agent.new
ssh cloud.home '
  sudo systemctl stop jmw-agent
  sudo install -m 0755 -o root -g root /tmp/JMW.Discovery.Agent.new /opt/jmw/bin/JMW.Discovery.Agent
  rm -f /tmp/JMW.Discovery.Agent.new
  sudo systemctl start jmw-agent
  sudo systemctl is-active jmw-agent
'
```

Binary: `/opt/jmw/bin/JMW.Discovery.Agent` (root:root — unit runs `User=root`,
`ReadWritePaths=/var/lib/jmw-agent /opt/jmw/bin` so the in-process self-updater
can replace it too). Config: `/etc/jmw/agent.json`. State dir:
`JMW_AGENT_STATE_DIR=/var/lib/jmw-agent`. Unit: `jmw-agent.service`.

## Verify after any deploy

```sh
ssh core.home 'sudo journalctl -u jmw-server --since "2 minutes ago" --no-pager | tail -15'
ssh core.home 'sudo journalctl -u jmw-agent  --since "2 minutes ago" --no-pager | tail -15'
ssh cloud.home 'sudo journalctl -u jmw-agent --since "2 minutes ago" --no-pager | tail -15'
```

Look for `Posted N batch(es), M facts. Server accepted N batch(es).` on each
agent and normal `POST /api/v1/agent/facts` 202s in the server log — not a
crash loop or repeated `Unhandled exception`.

## Gotchas

- `sudo` + shell glob/redirection resolves in the *unprivileged* calling
  shell before `sudo` runs — `sudo rm -rf /some/root/dir/*` or
  `sudo cmd < file` can silently no-op/fail if the invoking user can't read
  the target. Use `sudo bash -c '...'` for globs, or `sudo cat file | ...`
  for redirects.
- core.home's agent binary path (`/home/walljm/jmw-test/`) and cloud.home's
  (`/opt/jmw/bin/`) are genuinely different — don't copy one host's install
  command to the other without swapping the path.
- `release.yml` (the tag-push CI/CD flow) currently still builds/deploys the
  **legacy Go server** to the wrong binary name — do not cut a `vX.Y.Z` tag
  expecting it to deploy the real C# app. Rewriting that workflow is a
  separate, not-yet-done follow-up.
