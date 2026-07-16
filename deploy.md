# Deploy notes

**Deploys are CI/CD.** Pushing a strict-semver tag (`vX.Y.Z` — no prerelease/build
suffix) runs `.github/workflows/release.yml` on the self-hosted runner on
core.home, which builds, signs, releases, and deploys everything. The manual
steps at the bottom of this doc are a **fallback only** (CI down, or a one-off
test binary).

## Hosts

| Host | Role | Reached via | Arch/OS |
|---|---|---|---|
| `core.home` | Server (`jmw-server`) + one agent (`jmw-agent`, zone `local`, name "core") | `ssh core.home` (`walljm@192.168.1.54`, passwordless sudo) | x86_64, Ubuntu 24.04 |
| `cloud.home` | Agent only (`jmw-agent`, zone `cloud`, name "cloud") | `ssh cloud.home` (`walljm`, passwordless sudo) | x86_64, Debian 12 |

Both agents point at `https://agents.core.home` (Caddy in front of the
`jmw-server` systemd unit on core.home, Step CA cert chain).

## Cutting a release

```sh
# 1. Confirm a clean build and green tests first.
dotnet build JMW.Discovery.slnx -c Release
dotnet test test/Unit/JMW.Discovery.UnitTests.csproj
# When the change touches SQL, migrations, or [DatabaseCommand] signatures, also:
dotnet test test/Integration/JMW.Discovery.IntegrationTests.csproj   # needs Docker

# 2. Tag and push.
git tag v3.1.1
git push origin v3.1.1
```

The tag push triggers `release.yml` (self-hosted runner on core.home — deploy
steps are local commands, not SSH):

1. **Build** — `dotnet publish` of the server (linux-x64/arm64) and the agent
   (linux/macos/windows, x64/arm64), stamped `-p:Version=<tag>` (this overrides
   the `<Version>` dev default in `JMW.Discovery.Server.csproj`).
2. **Sign** — agent binaries signed with `src/Tools/UpdateSign`; checksums
   cosign-signed.
3. **GitHub release** — binaries + `SHA256SUMS` attached to a release for the tag.
4. **Docker** — multi-arch `walljm/jmw-agent:<tag>` + `:latest` pushed to
   Docker Hub and cosign-signed.
5. **Deploy server** — `sudo /opt/jmw/bin/gha-deploy-server` installs the binary
   at `/opt/jmw/bin/JMW.Discovery.Server` + wwwroot at `/var/lib/jmw/wwwroot`
   and restarts `jmw-server`.
6. **Publish agent release** — `sudo /opt/jmw/bin/gha-publish-agent-release`
   copies the signed agent binaries into `$JMW_RELEASES_DIR`; agents then
   **self-update over the heartbeat** (see AGENTS.md "Agent self-update") —
   no per-host agent installs needed.

The two `gha-*` helper scripts are root-owned, live only on core.home (not in
this repo), and are the only commands the runner may sudo
(`/etc/sudoers.d/gha-runner`).

## Verify after a release

The running server logs its version at startup and shows it at the foot of the
UI sidebar (`ServerVersion.cs` — `<version>+<git sha>`), so confirming which
build is live is a glance at either. Then:

```sh
ssh core.home 'sudo journalctl -u jmw-server --since "5 minutes ago" --no-pager | tail -15'
ssh core.home 'sudo journalctl -u jmw-agent  --since "5 minutes ago" --no-pager | tail -15'
ssh cloud.home 'sudo journalctl -u jmw-agent --since "5 minutes ago" --no-pager | tail -15'
```

Look for the `JMW Discovery Server <version> starting.` line, agents
self-updating to the new version and then
`Posted N batch(es), M facts. Server accepted N batch(es).`, and normal
`POST /api/v1/agent/facts` 202s — not a crash loop or repeated
`Unhandled exception`.

To read the version off a binary directly:
`grep -aoEm1 '[0-9]+\.[0-9]+\.[0-9]+\+[0-9a-f]{40}' /opt/jmw/bin/JMW.Discovery.Server`
(the first match is the app; later `10.0.x+…` matches are the bundled .NET runtime).

## Manual deploy (fallback only)

Only when CI is unavailable or you need to drop a one-off test binary. Note a
manual server publish is stamped with the csproj `<Version>` dev default, and a
manual agent drop into `$JMW_RELEASES_DIR` must still follow the clean-semver +
signing rules in AGENTS.md or the fleet will ignore it.

```sh
# Build (from a dev machine, once for both targets)
dotnet publish src/Server.Web/JMW.Discovery.Server.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o /tmp/jmw-publish/server
dotnet publish src/Agent/JMW.Discovery.Agent.csproj -r linux-x64 -c Release \
  -p:PublishSingleFile=true -p:SelfContained=true -o /tmp/jmw-publish/agent
```

### Server (core.home)

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

### Agent (core.home)

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

### Agent (cloud.home)

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

## Gotchas

- `sudo` + shell glob/redirection resolves in the *unprivileged* calling
  shell before `sudo` runs — `sudo rm -rf /some/root/dir/*` or
  `sudo cmd < file` can silently no-op/fail if the invoking user can't read
  the target. Use `sudo bash -c '...'` for globs, or `sudo cat file | ...`
  for redirects.
- core.home's agent binary path (`/home/walljm/jmw-test/`) and cloud.home's
  (`/opt/jmw/bin/`) are genuinely different — don't copy one host's install
  command to the other without swapping the path.
