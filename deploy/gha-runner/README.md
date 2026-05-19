# GitHub Actions self-hosted runner — core-services

CI/CD for this repo runs entirely on a single self-hosted runner on
`core-services` (192.168.1.54). Both `.github/workflows/ci.yml` and
`.github/workflows/release.yml` target the runner via the label
`core-services`.

The release workflow does two privileged things on the box:

1. Installs a new `jmw-server` binary and restarts the systemd service.
2. Publishes new `jmw-agent-<os>-<arch>` binaries into
   `/var/lib/jmw/releases/<version>/` so the running server can serve
   them to agents via the auto-updater.

Rather than handing the runner broad sudo, we install two small wrapper
scripts (`gha-deploy-server`, `gha-publish-agent-release`) at
`/opt/jmw/bin/` and allow the runner to invoke only those, with
NOPASSWD. Each script validates its own arguments.

## One-time setup on core-services

All commands run as root unless noted.

### 1. Create the runner user

```sh
useradd --system --create-home --shell /bin/bash --home-dir /opt/gha-runner gha-runner
```

The runner needs Go, Docker (with buildx), and the GitHub CLI for the
`gh release create` step. Install them if not already present:

```sh
apt-get update
apt-get install -y curl tar git ca-certificates jq

# Go (match the version in go.mod; bump as needed)
GO_VERSION=1.26.0
curl -fsSL "https://go.dev/dl/go${GO_VERSION}.linux-amd64.tar.gz" \
  | tar -C /usr/local -xzf -
ln -sf /usr/local/go/bin/go     /usr/local/bin/go
ln -sf /usr/local/go/bin/gofmt  /usr/local/bin/gofmt

# Docker + buildx (skip if already installed for other workloads)
curl -fsSL https://get.docker.com | sh
usermod -aG docker gha-runner

# GitHub CLI
type -p curl >/dev/null || apt-get install -y curl
curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
  | gpg --dearmor -o /usr/share/keyrings/githubcli-archive-keyring.gpg
chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
  > /etc/apt/sources.list.d/github-cli.list
apt-get update
apt-get install -y gh
```

### 2. Install the helper scripts

Copy the two wrappers from this directory into `/opt/jmw/bin/` and the
sudoers rule into `/etc/sudoers.d/`:

```sh
install -m 0755 -o root -g root \
  deploy/gha-runner/gha-deploy-server.sh         /opt/jmw/bin/gha-deploy-server
install -m 0755 -o root -g root \
  deploy/gha-runner/gha-publish-agent-release.sh /opt/jmw/bin/gha-publish-agent-release

install -m 0440 -o root -g root \
  deploy/gha-runner/sudoers-gha-runner /etc/sudoers.d/gha-runner
visudo -cf /etc/sudoers.d/gha-runner # sanity check
```

Verify from the `gha-runner` account:

```sh
sudo -u gha-runner sudo -n -l
# Should list:
#   (root) NOPASSWD: /opt/jmw/bin/gha-deploy-server
#   (root) NOPASSWD: /opt/jmw/bin/gha-publish-agent-release
```

### 3. Install + register the runner

In the GitHub UI:
**Settings → Actions → Runners → New self-hosted runner → Linux x64.**
Copy the registration token from that page (single-use, ~1 hour TTL).

Then on core-services:

```sh
sudo -iu gha-runner
mkdir -p ~/runner && cd ~/runner

# Use the latest runner release. Older runners (e.g. v2.319.1) cannot
# parse the registration token format GitHub issues today, so don't pin
# to an old version unless you know it understands the current token.
RUNNER_VERSION=$(curl -fsSL https://api.github.com/repos/actions/runner/releases/latest | jq -r .tag_name | sed s/^v//)
curl -fsSL -o actions-runner.tar.gz \
  "https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-linux-x64-${RUNNER_VERSION}.tar.gz"
tar xzf actions-runner.tar.gz
rm actions-runner.tar.gz

./config.sh \
  --url https://github.com/walljm/JMW.Agent \
  --token <TOKEN_FROM_GITHUB_UI> \
  --name core-services \
  --labels core-services \
  --work _work \
  --unattended
exit
```

On Ubuntu 24.04 the runner's bundled `installdependencies.sh` will try
to install `libicu55` / `libicu52`, which don't exist on noble. That's
harmless — the runner ships its own .NET-bundled ICU and starts fine
without the system packages. You can skip running `installdependencies.sh`
entirely on 22.04+.

### 4. Install as a systemd service

Back as root:

```sh
cd /opt/gha-runner/runner
./svc.sh install gha-runner
./svc.sh start
systemctl status 'actions.runner.walljm-JMW.Agent.core-services.service'
```

The runner now survives reboots.

### 5. Repository secrets

In **Settings → Secrets and variables → Actions**, add:

| Name                | Value                                   |
| ------------------- | --------------------------------------- |
| `DOCKER_HUB_USER`   | Docker Hub username (`walljm`)          |
| `DOCKER_HUB_TOKEN`  | Docker Hub access token (not password)  |

`GITHUB_TOKEN` is provided automatically by Actions; no setup needed.

### 6. Lock down Actions settings

In **Settings → Actions → General**:

- *Fork pull request workflows from outside collaborators* → **Require approval for all outside collaborators** (or disable entirely).
- *Workflow permissions* → **Read repository contents and packages permissions** with **Allow GitHub Actions to create and approve pull requests** off.

In **Settings → Actions → Runner groups** (Org-level only, skip if personal):
- Restrict the `Default` runner group / `core-services` runner to this single repository.

## Updating the helper scripts later

The wrappers are checked into the repo at
`deploy/gha-runner/gha-deploy-server.sh` and
`deploy/gha-runner/gha-publish-agent-release.sh`. When you change either,
re-run the install commands from step 2. The runner does **not**
automatically pick up changes — the deployed copies at `/opt/jmw/bin/`
are what sudoers grants permission to execute.

## Release flow

```sh
git tag v1.5.0 && git push origin v1.5.0
```

Then watch the Actions tab. The `release` workflow:

1. Builds 5 agent + 2 server binaries with `version=v1.5.0` stamped in.
2. Creates a GitHub Release at <https://github.com/walljm/JMW.Agent/releases/tag/v1.5.0> with all binaries + `SHA256SUMS`.
3. Pushes `walljm/jmw-agent:v1.5.0` and `:latest` to Docker Hub (Watchtower picks it up on the NAS boxes).
4. Installs the new `jmw-server` on core-services and restarts the service (~2s of downtime).
5. Drops the agent binaries into `/var/lib/jmw/releases/v1.5.0/`. The running server's release scanner notices on next sweep, and agents auto-update on their next heartbeat cycle.

The manual scripts ([scripts/deploy-server.sh](../../scripts/deploy-server.sh), [scripts/deploy-agent.sh](../../scripts/deploy-agent.sh)) remain available for initial host bring-up and break-glass deploys; they don't conflict with the CI/CD path.

## Tag format

`release.yml` triggers only on strict semver tags: `vMAJOR.MINOR.PATCH`,
no prereleases, no build metadata. Tags like `v1.5.0-rc1` will not start
a release; build them locally with `scripts/deploy-agent.sh --publish`
or `scripts/deploy-server.sh` if you need to ship a prerelease manually.

## Security notes

- The runner runs as user `gha-runner` with no login shell access expected. Don't add it to `sudo`, `wheel`, or `docker` for any reason beyond what's strictly listed here.
- The two sudoers entries are the only privileged operations the runner can perform. Both go through scripts owned by root that validate their inputs.
- The runner has full read access to the repo (cloned into its workspace) and to Docker Hub via the secret. A compromise of the runner is equivalent to a compromise of those.
- CI runs on `push` only — not `pull_request`. Self-hosted runners and PRs from forks are a known foot-gun; if/when external contributors arrive, switch CI to `pull_request_target` with explicit approval gating or move CI to a GitHub-hosted runner.
