package collect

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/agent/containercache"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// docker.go talks to the local Docker daemon over its unix socket using the
// Docker Engine HTTP API. We avoid the docker CLI (and the moby Go SDK) on
// purpose: shelling out is slow and field-poor, and the SDK pulls in a huge
// dependency tree. The Engine API is stable and small.
//
// Reference: https://docs.docker.com/engine/api/v1.43/

// dockerSocketCandidates returns paths to try, in priority order. The first
// readable one wins. $DOCKER_HOST overrides only when it is a unix:// URL.
func dockerSocketCandidates() []string {
	var out []string
	if dh := os.Getenv("DOCKER_HOST"); strings.HasPrefix(dh, "unix://") {
		out = append(out, strings.TrimPrefix(dh, "unix://"))
	}
	out = append(out, "/var/run/docker.sock")
	if home, err := os.UserHomeDir(); err == nil {
		// Docker Desktop on macOS / colima / rootless on Linux.
		out = append(out,
			filepath.Join(home, ".docker", "run", "docker.sock"),
			filepath.Join(home, ".colima", "default", "docker.sock"),
			filepath.Join(home, ".docker", "desktop", "docker.sock"),
		)
	}
	return out
}

// dockerClient wraps an http.Client bound to a unix socket.
type dockerClient struct {
	hc   *http.Client
	addr string // socket path, for diagnostics
}

func newDockerClient() *dockerClient {
	for _, sock := range dockerSocketCandidates() {
		fi, err := os.Stat(sock)
		if err != nil || (fi.Mode()&os.ModeSocket) == 0 {
			continue
		}
		hc := &http.Client{
			Timeout: 5 * time.Second,
			Transport: &http.Transport{
				DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
					var d net.Dialer
					return d.DialContext(ctx, "unix", sock)
				},
				DisableKeepAlives:     false,
				MaxIdleConns:          4,
				IdleConnTimeout:       30 * time.Second,
				ResponseHeaderTimeout: 5 * time.Second,
			},
		}
		return &dockerClient{hc: hc, addr: sock}
	}
	return nil
}

func (c *dockerClient) get(ctx context.Context, path string, into any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://docker"+path, nil)
	if err != nil {
		return err
	}
	resp, err := c.hc.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		return fmt.Errorf("docker api %s: HTTP %d", path, resp.StatusCode)
	}
	if into == nil {
		return nil
	}
	return json.NewDecoder(resp.Body).Decode(into)
}

// --- Engine API response shapes (only fields we read) ---

type apiVersion struct {
	Version    string `json:"Version"`
	APIVersion string `json:"ApiVersion"`
	Os         string `json:"Os"`
	Arch       string `json:"Arch"`
	KernelVer  string `json:"KernelVersion"`
}

type apiInfo struct {
	ID                string `json:"ID"`
	OperatingSystem   string `json:"OperatingSystem"`
	OSType            string `json:"OSType"`
	Architecture      string `json:"Architecture"`
	KernelVersion     string `json:"KernelVersion"`
	Driver            string `json:"Driver"`
	CgroupDriver      string `json:"CgroupDriver"`
	CgroupVersion     string `json:"CgroupVersion"`
	LoggingDriver     string `json:"LoggingDriver"`
	DockerRootDir     string `json:"DockerRootDir"`
	NCPU              int    `json:"NCPU"`
	MemTotal          uint64 `json:"MemTotal"`
	Containers        int    `json:"Containers"`
	ContainersRunning int    `json:"ContainersRunning"`
	ContainersPaused  int    `json:"ContainersPaused"`
	ContainersStopped int    `json:"ContainersStopped"`
	Images            int    `json:"Images"`
	Swarm             struct {
		LocalNodeState string `json:"LocalNodeState"`
		NodeID         string `json:"NodeID"`
	} `json:"Swarm"`
}

type apiContainerSummary struct {
	ID         string            `json:"Id"`
	Names      []string          `json:"Names"`
	Image      string            `json:"Image"`
	ImageID    string            `json:"ImageID"`
	Command    string            `json:"Command"`
	Created    int64             `json:"Created"` // unix seconds
	State      string            `json:"State"`
	Status     string            `json:"Status"`
	Labels     map[string]string `json:"Labels"`
	Ports      []struct {
		IP          string `json:"IP"`
		PrivatePort int    `json:"PrivatePort"`
		PublicPort  int    `json:"PublicPort"`
		Type        string `json:"Type"`
	} `json:"Ports"`
}

type apiContainerInspect struct {
	ID      string `json:"Id"`
	Name    string `json:"Name"`
	Created string `json:"Created"`
	Path    string `json:"Path"`
	Args    []string `json:"Args"`
	Image   string `json:"Image"`
	Platform string `json:"Platform"`
	State   struct {
		Status     string `json:"Status"`
		Running    bool   `json:"Running"`
		Paused     bool   `json:"Paused"`
		Restarting bool   `json:"Restarting"`
		OOMKilled  bool   `json:"OOMKilled"`
		Dead       bool   `json:"Dead"`
		Pid        int    `json:"Pid"`
		ExitCode   int    `json:"ExitCode"`
		StartedAt  string `json:"StartedAt"`
		FinishedAt string `json:"FinishedAt"`
		Health     *struct {
			Status string `json:"Status"`
		} `json:"Health"`
	} `json:"State"`
	RestartCount int `json:"RestartCount"`
	Config       struct {
		Hostname   string            `json:"Hostname"`
		User       string            `json:"User"`
		WorkingDir string            `json:"WorkingDir"`
		Image      string            `json:"Image"`
		Entrypoint []string          `json:"Entrypoint"`
		Cmd        []string          `json:"Cmd"`
		Labels     map[string]string `json:"Labels"`
	} `json:"Config"`
	HostConfig struct {
		LogConfig struct {
			Type string `json:"Type"`
		} `json:"LogConfig"`
		RestartPolicy struct {
			Name              string `json:"Name"`
			MaximumRetryCount int    `json:"MaximumRetryCount"`
		} `json:"RestartPolicy"`
		Privileged     bool   `json:"Privileged"`
		ReadonlyRootfs bool   `json:"ReadonlyRootfs"`
		NanoCpus       int64  `json:"NanoCpus"`
		CpuShares      int64  `json:"CpuShares"`
		Memory         uint64 `json:"Memory"`
		MemorySwap     int64  `json:"MemorySwap"`
		PidsLimit      int64  `json:"PidsLimit"`
	} `json:"HostConfig"`
	Mounts []struct {
		Type        string `json:"Type"`
		Name        string `json:"Name"`
		Source      string `json:"Source"`
		Destination string `json:"Destination"`
		Driver      string `json:"Driver"`
		Mode        string `json:"Mode"`
		RW          bool   `json:"RW"`
		Propagation string `json:"Propagation"`
	} `json:"Mounts"`
	NetworkSettings struct {
		Networks map[string]struct {
			NetworkID           string   `json:"NetworkID"`
			IPAddress           string   `json:"IPAddress"`
			IPPrefixLen         int      `json:"IPPrefixLen"`
			GlobalIPv6Address   string   `json:"GlobalIPv6Address"`
			GlobalIPv6PrefixLen int      `json:"GlobalIPv6PrefixLen"`
			Gateway             string   `json:"Gateway"`
			MacAddress          string   `json:"MacAddress"`
			Aliases             []string `json:"Aliases"`
		} `json:"Networks"`
		Ports map[string][]struct {
			HostIp   string `json:"HostIp"`
			HostPort string `json:"HostPort"`
		} `json:"Ports"`
	} `json:"NetworkSettings"`
}

type apiImageSummary struct {
	ID          string   `json:"Id"`
	RepoTags    []string `json:"RepoTags"`
	Size        int64    `json:"Size"`
}

type apiNetwork struct {
	ID         string            `json:"Id"`
	Name       string            `json:"Name"`
	Driver     string            `json:"Driver"`
	Scope      string            `json:"Scope"`
	Internal   bool              `json:"Internal"`
	Attachable bool              `json:"Attachable"`
	Labels     map[string]string `json:"Labels"`
	IPAM       struct {
		Config []struct {
			Subnet string `json:"Subnet"`
		} `json:"Config"`
	} `json:"IPAM"`
}

type apiVolume struct {
	Name       string            `json:"Name"`
	Driver     string            `json:"Driver"`
	Mountpoint string            `json:"Mountpoint"`
	Labels     map[string]string `json:"Labels"`
}

type apiVolumeList struct {
	Volumes []apiVolume `json:"Volumes"`
}

// --- Top-level collector ---

func collectDocker(ctx context.Context) *proto.DockerInfo {
	c := newDockerClient()
	if c == nil {
		return nil
	}
	// Cap the entire docker collection so a wedged daemon can't stall inventory.
	cctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()

	var ver apiVersion
	if err := c.get(cctx, "/version", &ver); err != nil {
		// Socket exists but isn't talking to us (perm denied, daemon down).
		return &proto.DockerInfo{Reachable: false}
	}

	di := &proto.DockerInfo{
		Reachable: true,
		Version:   ver.Version,
		Engine: &proto.DockerEngine{
			Version:    ver.Version,
			APIVersion: ver.APIVersion,
		},
	}

	var info apiInfo
	if err := c.get(cctx, "/info", &info); err == nil {
		e := di.Engine
		e.ID = info.ID
		e.OS = info.OperatingSystem
		e.OSType = info.OSType
		e.Architecture = info.Architecture
		e.KernelVersion = info.KernelVersion
		e.StorageDriver = info.Driver
		e.CgroupDriver = info.CgroupDriver
		e.CgroupVersion = info.CgroupVersion
		e.LoggingDriver = info.LoggingDriver
		e.DockerRootDir = info.DockerRootDir
		e.NCPU = info.NCPU
		e.MemTotalBytes = info.MemTotal
		e.Containers = info.Containers
		e.ContainersRunning = info.ContainersRunning
		e.ContainersPaused = info.ContainersPaused
		e.ContainersStopped = info.ContainersStopped
		e.Images = info.Images
		e.SwarmLocalNodeID = info.Swarm.NodeID
		e.SwarmState = info.Swarm.LocalNodeState
	}

	// Containers (summary, then enrich each via inspect).
	var sums []apiContainerSummary
	if err := c.get(cctx, "/containers/json?all=true", &sums); err == nil {
		for _, s := range sums {
			dc := containerFromSummary(s)
			var ins apiContainerInspect
			if err := c.get(cctx, "/containers/"+s.ID+"/json", &ins); err == nil {
				enrichContainer(&dc, &ins)
			}
			di.Containers = append(di.Containers, dc)
		}
	}

	// Images (summary only).
	var imgs []apiImageSummary
	if err := c.get(cctx, "/images/json", &imgs); err == nil {
		for _, im := range imgs {
			repo, tag := splitRepoTag(im.RepoTags)
			di.Images = append(di.Images, proto.DockerImage{
				ID:         im.ID,
				Repository: repo,
				Tag:        tag,
				SizeBytes:  uint64(im.Size),
			})
		}
	}

	// Networks.
	var nets []apiNetwork
	if err := c.get(cctx, "/networks", &nets); err == nil {
		for _, n := range nets {
			dn := proto.DockerNetwork{
				ID:         n.ID,
				Name:       n.Name,
				Driver:     n.Driver,
				Scope:      n.Scope,
				Internal:   n.Internal,
				Attachable: n.Attachable,
				Labels:     n.Labels,
			}
			for _, c := range n.IPAM.Config {
				if c.Subnet != "" {
					dn.Subnets = append(dn.Subnets, c.Subnet)
				}
			}
			di.Networks = append(di.Networks, dn)
		}
	}

	// Volumes.
	var vl apiVolumeList
	if err := c.get(cctx, "/volumes", &vl); err == nil {
		for _, v := range vl.Volumes {
			di.Volumes = append(di.Volumes, proto.DockerVolume{
				Name:       v.Name,
				Driver:     v.Driver,
				Mountpoint: v.Mountpoint,
				Labels:     v.Labels,
			})
		}
	}

	publishContainerCache(di.Containers)
	return di
}

// publishContainerCache pushes a MAC -> container snapshot into
// containercache so the discover package can label ARP sightings with the
// owning container's name + runtime. We extract one entry per (container,
// network) so multi-homed containers are matched on whichever NIC the ARP
// table observed.
func publishContainerCache(containers []proto.DockerContainer) {
	entries := make(map[string]containercache.Entry, len(containers))
	for _, dc := range containers {
		// Only running containers have live MACs in the host ARP table.
		// Stopped containers keep their last config but no neighbour entry,
		// so caching them would risk mislabelling a recycled MAC.
		if dc.State != "" && dc.State != "running" {
			continue
		}
		for _, n := range dc.Networks {
			if n.MAC == "" {
				continue
			}
			entries[n.MAC] = containercache.Entry{
				Name:    dc.Name,
				Image:   dc.Image,
				Network: n.Name,
				Runtime: "docker",
			}
		}
	}
	containercache.Replace(entries)
}

// containerFromSummary fills the fields available from /containers/json.
func containerFromSummary(s apiContainerSummary) proto.DockerContainer {
	dc := proto.DockerContainer{
		ID:        s.ID,
		Names:     trimNames(s.Names),
		Image:     s.Image,
		ImageID:   s.ImageID,
		Command:   s.Command,
		State:     s.State,
		Status:    s.Status,
		Labels:    s.Labels,
		CreatedAt: time.Unix(s.Created, 0).UTC(),
	}
	if len(dc.Names) > 0 {
		dc.Name = dc.Names[0]
	}
	for _, p := range s.Ports {
		dc.Ports = append(dc.Ports, proto.PortBinding{
			HostIP:        p.IP,
			HostPort:      p.PublicPort,
			ContainerPort: p.PrivatePort,
			Protocol:      p.Type,
		})
	}
	dc.ComposeProject = s.Labels["com.docker.compose.project"]
	dc.ComposeService = s.Labels["com.docker.compose.service"]
	dc.SwarmService = s.Labels["com.docker.swarm.service.name"]
	return dc
}

// enrichContainer overlays the rich per-container inspect data on top of
// the summary fields. Inspect always wins where both are present.
func enrichContainer(dc *proto.DockerContainer, ins *apiContainerInspect) {
	if ins.Name != "" {
		dc.Name = strings.TrimPrefix(ins.Name, "/")
	}
	dc.ImageID = ins.Image
	dc.Platform = ins.Platform
	dc.Pid = ins.State.Pid
	dc.ExitCode = ins.State.ExitCode
	dc.OOMKilled = ins.State.OOMKilled
	dc.RestartCount = ins.RestartCount
	if ins.State.Status != "" {
		dc.State = ins.State.Status
	}
	if ins.State.Health != nil {
		dc.Health = strings.ToLower(ins.State.Health.Status)
	}
	dc.StartedAt, _ = parseDockerTime(ins.State.StartedAt)
	dc.FinishedAt, _ = parseDockerTime(ins.State.FinishedAt)

	// Config
	dc.User = ins.Config.User
	dc.WorkingDir = ins.Config.WorkingDir
	if len(ins.Config.Entrypoint)+len(ins.Config.Cmd) > 0 {
		parts := append([]string{}, ins.Config.Entrypoint...)
		parts = append(parts, ins.Config.Cmd...)
		dc.Command = strings.Join(parts, " ")
	}
	if ins.Config.Labels != nil {
		dc.Labels = ins.Config.Labels
		dc.ComposeProject = ins.Config.Labels["com.docker.compose.project"]
		dc.ComposeService = ins.Config.Labels["com.docker.compose.service"]
		dc.SwarmService = ins.Config.Labels["com.docker.swarm.service.name"]
	}

	// HostConfig
	dc.LogDriver = ins.HostConfig.LogConfig.Type
	dc.RestartPolicy = ins.HostConfig.RestartPolicy.Name
	dc.RestartMaxRetry = ins.HostConfig.RestartPolicy.MaximumRetryCount
	dc.Privileged = ins.HostConfig.Privileged
	dc.ReadOnlyRootFS = ins.HostConfig.ReadonlyRootfs
	dc.NanoCPUs = ins.HostConfig.NanoCpus
	dc.CPUShares = ins.HostConfig.CpuShares
	dc.MemoryLimit = ins.HostConfig.Memory
	dc.MemorySwap = ins.HostConfig.MemorySwap
	dc.PidsLimit = ins.HostConfig.PidsLimit

	// Mounts
	dc.Mounts = nil
	for _, m := range ins.Mounts {
		dc.Mounts = append(dc.Mounts, proto.ContainerMount{
			Type: m.Type, Name: m.Name, Source: m.Source,
			Destination: m.Destination, Driver: m.Driver,
			Mode: m.Mode, RW: m.RW, Propagation: m.Propagation,
		})
	}

	// Networks
	dc.Networks = nil
	for name, n := range ins.NetworkSettings.Networks {
		dc.Networks = append(dc.Networks, proto.ContainerNetwork{
			Name:       name,
			NetworkID:  n.NetworkID,
			IPv4:       n.IPAddress,
			IPv4Prefix: n.IPPrefixLen,
			IPv6:       n.GlobalIPv6Address,
			IPv6Prefix: n.GlobalIPv6PrefixLen,
			Gateway:    n.Gateway,
			MAC:        n.MacAddress,
			Aliases:    n.Aliases,
		})
	}

	// Published ports from inspect (richer than summary; it can include
	// container ports with no host binding).
	dc.Ports = dc.Ports[:0]
	for spec, bindings := range ins.NetworkSettings.Ports {
		// spec is "<port>/<proto>" e.g. "80/tcp"
		port, ps := splitPortSpec(spec)
		if len(bindings) == 0 {
			dc.Ports = append(dc.Ports, ps.toPortBinding(port, "", 0))
			continue
		}
		for _, b := range bindings {
			hp := atoiSafe(b.HostPort)
			dc.Ports = append(dc.Ports, ps.toPortBinding(port, b.HostIp, hp))
		}
	}
}

// --- Small helpers ---

func trimNames(in []string) []string {
	out := make([]string, 0, len(in))
	for _, n := range in {
		out = append(out, strings.TrimPrefix(n, "/"))
	}
	return out
}

// splitRepoTag picks the first repo:tag from RepoTags. Containers without
// any tags (dangling images) get empty strings.
func splitRepoTag(tags []string) (repo, tag string) {
	for _, t := range tags {
		if t == "<none>:<none>" || t == "" {
			continue
		}
		if i := strings.LastIndexByte(t, ':'); i > 0 {
			return t[:i], t[i+1:]
		}
		return t, ""
	}
	return "", ""
}

// parseDockerTime accepts the RFC3339Nano strings the daemon emits, plus the
// zero-value sentinel "0001-01-01T00:00:00Z" which we treat as no time.
func parseDockerTime(s string) (time.Time, error) {
	if s == "" || strings.HasPrefix(s, "0001-01-01") {
		return time.Time{}, nil
	}
	return time.Parse(time.RFC3339Nano, s)
}

// portSpec is a tiny helper type so we can write splitPortSpec / toPortBinding
// without polluting the package namespace with more free functions.
type portSpec struct{ proto string }

func (p portSpec) toPortBinding(containerPort int, hostIP string, hostPort int) proto.PortBinding {
	return proto.PortBinding{
		HostIP: hostIP, HostPort: hostPort,
		ContainerPort: containerPort, Protocol: p.proto,
	}
}

func splitPortSpec(spec string) (port int, ps portSpec) {
	// "<num>/<proto>"
	if i := strings.IndexByte(spec, '/'); i > 0 {
		return atoiSafe(spec[:i]), portSpec{proto: spec[i+1:]}
	}
	return atoiSafe(spec), portSpec{}
}

func atoiSafe(s string) int {
	if s == "" {
		return 0
	}
	n := 0
	for _, c := range s {
		if c < '0' || c > '9' {
			return 0
		}
		n = n*10 + int(c-'0')
	}
	return n
}
