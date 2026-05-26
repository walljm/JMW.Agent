# JMW Agent

Go monorepo: `cmd/server` (dashboard + API) and `cmd/agent` (metrics + network sensor).
The agent runs natively or inside Docker using `JMW_HOST_ROOT` to read host `/proc`/`/sys`.

## Common workflows

### Build and test
```sh
make build          # bin/jmw-server + bin/jmw-agent for host OS
make build-all      # cross-builds: linux/darwin × amd64/arm64
make test           # go test ./...
make vet            # go vet ./...
```
Always cross-build with `GOOS=linux GOARCH=amd64` when adding Linux-only collector code.

### Add a new Linux metric
1. Add the proto field in `internal/shared/proto/inventory.go`.
2. Implement in the appropriate `_linux.go` file under `internal/agent/collect/`; use `hostfs.Path()` for every `/proc` or `/sys` read.
3. Add a matching stub (return nil/zero) in `_other.go` if the interface requires it.
4. Wire the new field into the payload in `collect.go`.

### Deploy to a NAS (Docker)
See `deploy/docker/README.md` for the full procedure. Short form:
```sh
make docker-agent                     # builds + pushes walljm/jmw-agent:latest
# On the NAS:
docker rm -f jmw-agent
docker pull walljm/jmw-agent:latest
docker run -d --name jmw-agent --restart=always \
  --network=host --pid=host --privileged \
  -v /:/host:ro \
  -v /volume1/jmw-agent/data:/data \
  -v /volume1/jmw-agent/etc:/etc/jmw-agent:ro \
  walljm/jmw-agent:latest
```
`--privileged` is required for `smartctl` to open raw block devices.

### SSH to deploy targets
Use the hostnames from `~/.ssh/config` directly — never override identity or
host-key flags. Rapid-fire connections (keyscan loops, `-o StrictHostKeyChecking=no`
retries) trigger ADM's SSH auto-ban.

```sh
ssh nas10.home   # 192.168.1.70 — NAS (x86)
ssh nas40.home   # 192.168.1.231 — NAS (x86)
```
If host-key verification fails, fix `~/.ssh/known_hosts` manually — do not
pass `-o StrictHostKeyChecking=no` or run `ssh-keyscan` in a loop.

## Decision tables

| Situation | Use |
|---|---|
| Reading host `/proc` or `/sys` | `hostfs.Path("/proc/...")` — never a bare path |
| New metric available on all platforms | `collect.go` + `inventory.go` |
| New metric Linux-only | `*_linux.go`; stub in `*_other.go` |
| New metric Linux + Darwin | `*_linux.go` + `*_darwin.go`; stub in `*_other.go` |
| SMART data | `extras_linux.go:enrichDiskSMART`; needs `--privileged` in Docker |
| Temperature data | `inventory_linux.go:collectTemperatures` reads both `thermal_zone*` (ARM) and `hwmon` (x86) |

## Gotchas

**Don't** use bare `/proc` or `/sys` paths in collector code.  
**Do** wrap every host-filesystem read with `hostfs.Path(p)` — the container deploy sets `JMW_HOST_ROOT=/host`.

**Don't** add CGO or non-stdlib imports without discussion.  
**Do** keep the binary statically linked (`CGO_ENABLED=0`); the Docker base image is distroless.

**Don't** run `ssh-keyscan` or pass `-o StrictHostKeyChecking=no` to probe NAS hosts.  
**Do** use the named hosts from `~/.ssh/config` (`nas10.home`, `nas40.home`); clear auto-bans in ADM → Settings → Security → Auto Block.

## Deploy targets

See `deploy-targets.md` for the full host list. Managed by Watchtower on the NAS boxes — push a new image tag and the container is updated automatically within the poll interval.

## References

- `deploy/docker/README.md` — Docker deploy procedure for Asustor NAS
- `internal/agent/hostfs/hostfs.go` — container-aware filesystem prefix
- `internal/shared/proto/inventory.go` — canonical data model for all collected metrics
- `planning/architecture/overview.md` — architectural decisions and constraints
