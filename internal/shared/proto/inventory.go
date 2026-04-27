// Inventory wire types — extended device facts collected on a slow cadence
// (default every 24h). Additive only.
package proto

import "time"

// Inventory is the full device fact payload.
type Inventory struct {
	CollectedAt time.Time `json:"collected_at"`

	Hardware  HardwareInfo     `json:"hardware"`
	OS        OSInfo           `json:"os"`
	Disks     []DiskDevice     `json:"disks,omitempty"`
	Network   NetworkInfo      `json:"network"`
	Routes    []RouteEntry     `json:"routes,omitempty"`
	Users     []UserSession    `json:"users,omitempty"`
	Listening []ListeningPort  `json:"listening_ports,omitempty"`
	Processes []ProcessSummary `json:"processes,omitempty"` // top-N by CPU/mem
	Docker    *DockerInfo      `json:"docker,omitempty"`
	Reboots   []BootRecord     `json:"reboot_history,omitempty"`
	Packages  *PackageInventory `json:"packages,omitempty"`
}

// HardwareInfo is static-ish hardware facts.
type HardwareInfo struct {
	CPUModel        string `json:"cpu_model,omitempty"`
	CPUVendor       string `json:"cpu_vendor,omitempty"`
	CPUCores        int    `json:"cpu_cores,omitempty"`        // physical cores
	CPULogicalCores int    `json:"cpu_logical_cores,omitempty"` // hyperthreads
	CPUMHz          float64 `json:"cpu_mhz,omitempty"`
	TotalMemBytes   uint64 `json:"total_mem_bytes,omitempty"`
	BoardVendor     string `json:"board_vendor,omitempty"`
	BoardModel      string `json:"board_model,omitempty"`
	SystemVendor    string `json:"system_vendor,omitempty"`
	SystemModel     string `json:"system_model,omitempty"`
	SystemSerial    string `json:"system_serial,omitempty"`
	BIOSVendor      string `json:"bios_vendor,omitempty"`
	BIOSVersion     string `json:"bios_version,omitempty"`
	BIOSDate        string `json:"bios_date,omitempty"`
	Virtualization  string `json:"virtualization,omitempty"` // none|kvm|vmware|docker|wsl|...
}

// OSInfo describes the operating system.
type OSInfo struct {
	Family       string    `json:"family"`        // linux | darwin | windows | ...
	Distro       string    `json:"distro,omitempty"`
	Version      string    `json:"version,omitempty"`
	Build        string    `json:"build,omitempty"`
	Kernel       string    `json:"kernel,omitempty"`
	KernelArch   string    `json:"kernel_arch,omitempty"`
	Hostname     string    `json:"hostname,omitempty"`
	Timezone     string    `json:"timezone,omitempty"`
	BootTime     time.Time `json:"boot_time,omitempty"`
	InstallDate  time.Time `json:"install_date,omitempty"`
}

// DiskDevice describes a physical disk (not a mountpoint).
type DiskDevice struct {
	Name       string         `json:"name"`             // sda, nvme0n1, disk0
	Model      string         `json:"model,omitempty"`
	Serial     string         `json:"serial,omitempty"`
	SizeBytes  uint64         `json:"size_bytes,omitempty"`
	Type       string         `json:"type,omitempty"`   // hdd|ssd|nvme|virtual|unknown
	Removable  bool           `json:"removable,omitempty"`
	Partitions []DiskPartition `json:"partitions,omitempty"`
}

// DiskPartition is one partition on a DiskDevice.
type DiskPartition struct {
	Name       string `json:"name"`
	Mountpoint string `json:"mountpoint,omitempty"`
	FSType     string `json:"fs_type,omitempty"`
	SizeBytes  uint64 `json:"size_bytes,omitempty"`
	Label      string `json:"label,omitempty"`
	UUID       string `json:"uuid,omitempty"`
}

// NetworkInfo is static-ish network facts.
type NetworkInfo struct {
	Interfaces []NetInterface `json:"interfaces,omitempty"`
	DNSServers []string       `json:"dns_servers,omitempty"`
	DNSSearch  []string       `json:"dns_search,omitempty"`
	Gateway4   string         `json:"gateway_v4,omitempty"`
	Gateway6   string         `json:"gateway_v6,omitempty"`
}

// NetInterface is per-interface static config.
type NetInterface struct {
	Name        string   `json:"name"`
	MAC         string   `json:"mac,omitempty"`
	MTU         int      `json:"mtu,omitempty"`
	IsUp        bool     `json:"is_up"`
	IsLoopback  bool     `json:"is_loopback,omitempty"`
	LinkSpeedMbps int    `json:"link_speed_mbps,omitempty"`
	Duplex      string   `json:"duplex,omitempty"`
	IPv4        []string `json:"ipv4,omitempty"` // CIDR strings
	IPv6        []string `json:"ipv6,omitempty"` // CIDR strings
	Type        string   `json:"type,omitempty"` // ethernet|wifi|virtual|loopback
}

// RouteEntry is one entry in the routing table.
type RouteEntry struct {
	Destination string `json:"destination"` // CIDR or "default"
	Gateway     string `json:"gateway,omitempty"`
	Iface       string `json:"iface,omitempty"`
	Family      string `json:"family"` // ipv4|ipv6
	Metric      int    `json:"metric,omitempty"`
}

// UserSession is one logged-in user / session.
type UserSession struct {
	User    string    `json:"user"`
	TTY     string    `json:"tty,omitempty"`
	Host    string    `json:"host,omitempty"`
	LoginAt time.Time `json:"login_at,omitempty"`
}

// ListeningPort is one bound TCP/UDP socket.
type ListeningPort struct {
	Proto       string `json:"proto"`        // tcp|udp|tcp6|udp6
	Address     string `json:"address"`      // bind address (v4 or v6)
	Port        int    `json:"port"`
	ProcessName string `json:"process,omitempty"`
	PID         int    `json:"pid,omitempty"`
}

// ProcessSummary is a snapshot of one running process (top-N).
type ProcessSummary struct {
	PID        int     `json:"pid"`
	Name       string  `json:"name"`
	User       string  `json:"user,omitempty"`
	CPUPercent float64 `json:"cpu_pct,omitempty"`
	MemPercent float64 `json:"mem_pct,omitempty"`
	MemBytes   uint64  `json:"mem_bytes,omitempty"`
	Cmd        string  `json:"cmd,omitempty"`
}

// DockerInfo summarizes the local Docker engine if reachable.
//
// When reachable, Engine carries engine-wide facts and Containers carries
// the (rich) per-container record. Images and Networks are best-effort
// supplemental views useful for context.
type DockerInfo struct {
	Reachable  bool              `json:"reachable"`
	Version    string            `json:"version,omitempty"` // server version (kept for back-compat)
	Engine     *DockerEngine     `json:"engine,omitempty"`
	Containers []DockerContainer `json:"containers,omitempty"`
	Images     []DockerImage     `json:"images,omitempty"`
	Networks   []DockerNetwork   `json:"networks,omitempty"`
	Volumes    []DockerVolume    `json:"volumes,omitempty"`
}

// DockerEngine carries engine-wide facts (one per host).
type DockerEngine struct {
	ID                string `json:"id,omitempty"`                // Docker daemon ID
	Version           string `json:"version,omitempty"`           // server version
	APIVersion        string `json:"api_version,omitempty"`
	OS                string `json:"os,omitempty"`                // operating-system label from daemon
	OSType            string `json:"os_type,omitempty"`           // linux | windows
	Architecture      string `json:"architecture,omitempty"`
	KernelVersion     string `json:"kernel_version,omitempty"`
	StorageDriver     string `json:"storage_driver,omitempty"`
	CgroupDriver      string `json:"cgroup_driver,omitempty"`
	CgroupVersion     string `json:"cgroup_version,omitempty"`
	LoggingDriver     string `json:"logging_driver,omitempty"`
	DockerRootDir     string `json:"docker_root_dir,omitempty"`
	SwarmLocalNodeID  string `json:"swarm_local_node_id,omitempty"` // empty if not in swarm
	SwarmState        string `json:"swarm_state,omitempty"`         // "active" | "inactive" | ""
	NCPU              int    `json:"ncpu,omitempty"`
	MemTotalBytes     uint64 `json:"mem_total_bytes,omitempty"`
	Containers        int    `json:"containers,omitempty"`
	ContainersRunning int    `json:"containers_running,omitempty"`
	ContainersPaused  int    `json:"containers_paused,omitempty"`
	ContainersStopped int    `json:"containers_stopped,omitempty"`
	Images            int    `json:"images,omitempty"`
}

// DockerContainer is one container with rich, ops-relevant facts.
//
// Field set chosen to match what operators actually look at first:
// identity, lifecycle, health, composition (compose/swarm/labels), network
// publication, mounts, resource limits, and current resource usage.
type DockerContainer struct {
	// Identity
	ID         string `json:"id"`                    // full 64-char ID
	Name       string `json:"name,omitempty"`        // primary name without leading slash
	Names      []string `json:"names,omitempty"`     // all names (containers can have aliases)
	Image      string `json:"image,omitempty"`       // image reference as run (may be a tag or digest)
	ImageID    string `json:"image_id,omitempty"`    // sha256:...
	Command    string `json:"command,omitempty"`     // entrypoint+cmd flattened
	CreatedAt  time.Time `json:"created_at,omitempty"`
	StartedAt  time.Time `json:"started_at,omitempty"`
	FinishedAt time.Time `json:"finished_at,omitempty"`

	// Lifecycle
	State        string `json:"state,omitempty"`         // running|exited|paused|restarting|created|dead|removing
	Status       string `json:"status,omitempty"`        // human "Up 3 hours" / "Exited (0) 5 minutes ago"
	Health       string `json:"health,omitempty"`        // healthy|unhealthy|starting|none
	ExitCode     int    `json:"exit_code,omitempty"`
	OOMKilled    bool   `json:"oom_killed,omitempty"`
	RestartCount int    `json:"restart_count,omitempty"`
	Pid          int    `json:"pid,omitempty"`

	// Composition
	Labels         map[string]string `json:"labels,omitempty"`
	ComposeProject string            `json:"compose_project,omitempty"` // com.docker.compose.project
	ComposeService string            `json:"compose_service,omitempty"` // com.docker.compose.service
	SwarmService   string            `json:"swarm_service,omitempty"`   // com.docker.swarm.service.name

	// Config (curated)
	RestartPolicy   string `json:"restart_policy,omitempty"`   // no|always|on-failure|unless-stopped
	RestartMaxRetry int    `json:"restart_max_retry,omitempty"`
	LogDriver       string `json:"log_driver,omitempty"`
	User            string `json:"user,omitempty"`
	WorkingDir      string `json:"working_dir,omitempty"`
	Privileged      bool   `json:"privileged,omitempty"`
	ReadOnlyRootFS  bool   `json:"read_only_rootfs,omitempty"`
	Platform        string `json:"platform,omitempty"` // linux/amd64

	// Network
	Networks []ContainerNetwork `json:"networks,omitempty"`
	Ports    []PortBinding      `json:"ports,omitempty"`

	// Storage
	Mounts []ContainerMount `json:"mounts,omitempty"`

	// Resource limits (0 = unlimited / unset)
	NanoCPUs       int64  `json:"nano_cpus,omitempty"`        // 1e9 = 1 CPU
	CPUShares      int64  `json:"cpu_shares,omitempty"`
	MemoryLimit    uint64 `json:"memory_limit_bytes,omitempty"`
	MemorySwap     int64  `json:"memory_swap_bytes,omitempty"` // -1 = unlimited
	PidsLimit      int64  `json:"pids_limit,omitempty"`

	// Current usage (best-effort; populated only when stats are sampled)
	CPUPercent     float64 `json:"cpu_pct,omitempty"`
	MemUsageBytes  uint64  `json:"mem_usage_bytes,omitempty"`
	MemPercent     float64 `json:"mem_pct,omitempty"`
	NetRxBytes     uint64  `json:"net_rx_bytes,omitempty"`
	NetTxBytes     uint64  `json:"net_tx_bytes,omitempty"`
	BlockReadBytes uint64  `json:"block_read_bytes,omitempty"`
	BlockWriteBytes uint64 `json:"block_write_bytes,omitempty"`
}

// ContainerNetwork is one network the container is attached to.
type ContainerNetwork struct {
	Name        string `json:"name"`
	NetworkID   string `json:"network_id,omitempty"`
	IPv4        string `json:"ipv4,omitempty"`
	IPv4Prefix  int    `json:"ipv4_prefix,omitempty"`
	IPv6        string `json:"ipv6,omitempty"`
	IPv6Prefix  int    `json:"ipv6_prefix,omitempty"`
	Gateway     string `json:"gateway,omitempty"`
	MAC         string `json:"mac,omitempty"`
	Aliases     []string `json:"aliases,omitempty"`
}

// PortBinding is one published port mapping.
type PortBinding struct {
	HostIP        string `json:"host_ip,omitempty"`
	HostPort      int    `json:"host_port,omitempty"`
	ContainerPort int    `json:"container_port"`
	Protocol      string `json:"protocol,omitempty"` // tcp|udp|sctp
}

// ContainerMount describes one mount (volume, bind, tmpfs).
type ContainerMount struct {
	Type        string `json:"type"`                  // volume|bind|tmpfs
	Name        string `json:"name,omitempty"`        // volume name (volume mounts only)
	Source      string `json:"source,omitempty"`      // host path or volume name
	Destination string `json:"destination"`           // container path
	Driver      string `json:"driver,omitempty"`
	Mode        string `json:"mode,omitempty"`        // rw|ro|... raw mode string
	RW          bool   `json:"rw,omitempty"`
	Propagation string `json:"propagation,omitempty"`
}

// DockerNetwork is one engine-level network.
type DockerNetwork struct {
	ID         string            `json:"id"`
	Name       string            `json:"name,omitempty"`
	Driver     string            `json:"driver,omitempty"` // bridge|overlay|host|...
	Scope      string            `json:"scope,omitempty"`  // local|swarm
	Internal   bool              `json:"internal,omitempty"`
	Attachable bool              `json:"attachable,omitempty"`
	Subnets    []string          `json:"subnets,omitempty"`
	Labels     map[string]string `json:"labels,omitempty"`
}

// DockerVolume is one engine-level volume.
type DockerVolume struct {
	Name       string            `json:"name"`
	Driver     string            `json:"driver,omitempty"`
	Mountpoint string            `json:"mountpoint,omitempty"`
	Labels     map[string]string `json:"labels,omitempty"`
}

// DockerImage is one image known to the engine.
type DockerImage struct {
	ID         string `json:"id"`
	Repository string `json:"repository,omitempty"`
	Tag        string `json:"tag,omitempty"`
	SizeBytes  uint64 `json:"size_bytes,omitempty"`
}

// BootRecord is one entry in the reboot history.
type BootRecord struct {
	BootedAt time.Time `json:"booted_at"`
	Kernel   string    `json:"kernel,omitempty"`
}

// PackageInventory is the installed package list. Large; opt-in.
type PackageInventory struct {
	Manager  string    `json:"manager"` // apt|dpkg|rpm|brew|pkg|...
	Count    int       `json:"count"`
	Packages []Package `json:"packages,omitempty"`
}

// Package is one installed package.
type Package struct {
	Name    string `json:"name"`
	Version string `json:"version,omitempty"`
	Arch    string `json:"arch,omitempty"`
}

// InventoryRequest is what the agent posts.
type InventoryRequest struct {
	AgentID   string    `json:"agent_id"`
	Inventory Inventory `json:"inventory"`
}

// InventoryResponse is the server ack.
type InventoryResponse struct {
	Accepted bool `json:"accepted"`
}
