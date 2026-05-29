// Inventory wire types — extended device facts collected on a slow cadence
// (default every 24h). Additive only.
package proto

import "time"

// Inventory is the full device fact payload.
type Inventory struct {
	CollectedAt time.Time `json:"collected_at"`

	Hardware  HardwareInfo      `json:"hardware"`
	OS        OSInfo            `json:"os"`
	Disks     []DiskDevice      `json:"disks,omitempty"`
	Network   NetworkInfo       `json:"network"`
	Routes    []RouteEntry      `json:"routes,omitempty"`
	Users     []UserSession     `json:"users,omitempty"`
	Listening []ListeningPort   `json:"listening_ports,omitempty"`
	Processes []ProcessSummary  `json:"processes,omitempty"` // top-N by CPU/mem
	Docker    *DockerInfo       `json:"docker,omitempty"`
	Reboots   []BootRecord      `json:"reboot_history,omitempty"`
	Packages  *PackageInventory `json:"packages,omitempty"`
	// Hassio is populated only when the agent runs as a Home Assistant add-on
	// (detected by the SUPERVISOR_TOKEN env var). Lets the device detail view
	// show HA Core / Supervisor / OS versions and the installed add-ons.
	Hassio *HassioInfo `json:"hassio,omitempty"`

	// Filesystems is per-mountpoint usage at inventory time. Lets the server
	// trend fill rate and alert on near-full volumes without sampling the
	// heartbeat-rate disk snapshots.
	Filesystems []FilesystemUsage `json:"filesystems,omitempty"`

	// Updates is the patch posture: pending packages, security count, and
	// "reboot required" flag. One of the top operational questions.
	Updates *UpdateStatus `json:"updates,omitempty"`

	// Services lists units of interest — by default only failed/abnormal ones
	// to keep payload size bounded. Empty slice means "checked, all good".
	Services []ServiceStatus `json:"services,omitempty"`

	// Security collects firewall, AV/EDR, TPM, SecureBoot, and disk-encryption
	// state. Compliance and audit signal.
	Security *SecurityPosture `json:"security,omitempty"`

	// GPUs is a list of installed graphics adapters.
	GPUs []GPU `json:"gpus,omitempty"`

	// Chassis describes the physical form factor (laptop / desktop / server /
	// VM / SBC) plus battery facts when present.
	Chassis *ChassisInfo `json:"chassis,omitempty"`

	// LocalUsers is the list of local accounts (distinct from currently
	// logged-in `Users`). IAM/audit signal.
	LocalUsers []LocalUser `json:"local_users,omitempty"`
}

// FilesystemUsage is one mounted filesystem with size accounting.
type FilesystemUsage struct {
	Mountpoint string `json:"mountpoint"`
	Device     string `json:"device,omitempty"`
	FSType     string `json:"fs_type,omitempty"`
	TotalBytes uint64 `json:"total_bytes,omitempty"`
	UsedBytes  uint64 `json:"used_bytes,omitempty"`
	FreeBytes  uint64 `json:"free_bytes,omitempty"`
	InodesUsed uint64 `json:"inodes_used,omitempty"`
	InodesFree uint64 `json:"inodes_free,omitempty"`
}

// UpdateStatus describes pending OS / package updates.
type UpdateStatus struct {
	Manager        string          `json:"manager,omitempty"` // apt|dnf|yum|brew|softwareupdate|windowsupdate
	Pending        int             `json:"pending"`
	Security       int             `json:"security,omitempty"` // subset of Pending that are security
	RebootRequired bool            `json:"reboot_required,omitempty"`
	CheckedAt      time.Time       `json:"checked_at,omitempty"`
	Updates        []PendingUpdate `json:"updates,omitempty"` // capped list (top N)
}

// PendingUpdate is one package or KB awaiting install.
type PendingUpdate struct {
	Name           string `json:"name"`
	CurrentVersion string `json:"current_version,omitempty"`
	NewVersion     string `json:"new_version,omitempty"`
	Source         string `json:"source,omitempty"` // repo / channel
	Security       bool   `json:"security,omitempty"`
}

// ServiceStatus is one OS service / unit. Only abnormal entries are reported
// by default (failed, auto-but-stopped) to bound payload size.
type ServiceStatus struct {
	Name        string `json:"name"`
	DisplayName string `json:"display_name,omitempty"`
	State       string `json:"state,omitempty"`      // running|stopped|failed|...
	StartMode   string `json:"start_mode,omitempty"` // auto|manual|disabled
	SubState    string `json:"sub_state,omitempty"`  // systemd: running|dead|exited|...
	ExitCode    int    `json:"exit_code,omitempty"`
}

// SecurityPosture aggregates firewall, AV, TPM, SecureBoot, encryption.
type SecurityPosture struct {
	Firewall       *FirewallStatus   `json:"firewall,omitempty"`
	AntiVirus      []AVProduct       `json:"antivirus,omitempty"`
	TPMPresent     *bool             `json:"tpm_present,omitempty"`
	TPMVersion     string            `json:"tpm_version,omitempty"`
	SecureBoot     *bool             `json:"secure_boot,omitempty"`
	DiskEncryption []EncryptedVolume `json:"disk_encryption,omitempty"`
	SELinuxMode    string            `json:"selinux_mode,omitempty"`  // enforcing|permissive|disabled|""
	AppArmorMode   string            `json:"apparmor_mode,omitempty"` // enforce|complain|""
}

// FirewallStatus describes the host firewall.
type FirewallStatus struct {
	Provider string   `json:"provider,omitempty"` // ufw|firewalld|nftables|iptables|pf|windows
	Enabled  bool     `json:"enabled"`
	Default  string   `json:"default,omitempty"`  // default policy: allow|deny|reject
	Profiles []string `json:"profiles,omitempty"` // windows: domain/private/public state
}

// AVProduct is one antivirus / EDR registration.
type AVProduct struct {
	Name              string    `json:"name"`
	Enabled           bool      `json:"enabled"`
	RealtimeProtected bool      `json:"realtime,omitempty"`
	UpToDate          bool      `json:"up_to_date,omitempty"`
	SignatureVersion  string    `json:"signature_version,omitempty"`
	SignatureAge      string    `json:"signature_age,omitempty"` // human ("3d")
	LastScan          time.Time `json:"last_scan,omitempty"`
}

// EncryptedVolume is one volume protected by full-disk encryption.
type EncryptedVolume struct {
	Mountpoint string `json:"mountpoint,omitempty"`
	Device     string `json:"device,omitempty"`
	Type       string `json:"type,omitempty"`   // bitlocker|luks|filevault|apfs-encrypted
	Status     string `json:"status,omitempty"` // on|off|partial|suspended
}

// GPU is one graphics adapter.
type GPU struct {
	Vendor        string `json:"vendor,omitempty"`
	Model         string `json:"model,omitempty"`
	DriverVersion string `json:"driver_version,omitempty"`
	VRAMBytes     uint64 `json:"vram_bytes,omitempty"`
}

// ChassisInfo describes the physical form factor and (if a laptop) battery.
type ChassisInfo struct {
	Type    string   `json:"type,omitempty"` // laptop|desktop|server|vm|sbc|tablet|other
	Battery *Battery `json:"battery,omitempty"`
}

// Battery describes the primary battery on portable hardware.
type Battery struct {
	DesignCapacityWh  float64 `json:"design_capacity_wh,omitempty"`
	CurrentCapacityWh float64 `json:"current_capacity_wh,omitempty"`
	HealthPercent     float64 `json:"health_pct,omitempty"` // current / design * 100
	CycleCount        int     `json:"cycle_count,omitempty"`
	State             string  `json:"state,omitempty"` // charging|discharging|full|...
	ChargePercent     float64 `json:"charge_pct,omitempty"`
}

// LocalUser is one local account on the host.
type LocalUser struct {
	Name        string    `json:"name"`
	UID         string    `json:"uid,omitempty"` // string for cross-platform (Windows uses SIDs)
	GID         string    `json:"gid,omitempty"`
	HomeDir     string    `json:"home_dir,omitempty"`
	Shell       string    `json:"shell,omitempty"`
	IsAdmin     bool      `json:"is_admin,omitempty"` // sudo / wheel / Administrators
	Disabled    bool      `json:"disabled,omitempty"`
	LastLogin   time.Time `json:"last_login,omitempty"`
	PasswordAge int       `json:"password_age_days,omitempty"`
}

// HardwareInfo is static-ish hardware facts.
type HardwareInfo struct {
	CPUModel        string  `json:"cpu_model,omitempty"`
	CPUVendor       string  `json:"cpu_vendor,omitempty"`
	CPUCores        int     `json:"cpu_cores,omitempty"`         // physical cores
	CPULogicalCores int     `json:"cpu_logical_cores,omitempty"` // hyperthreads
	CPUMHz          float64 `json:"cpu_mhz,omitempty"`
	TotalMemBytes   uint64  `json:"total_mem_bytes,omitempty"`
	BoardVendor     string  `json:"board_vendor,omitempty"`
	BoardModel      string  `json:"board_model,omitempty"`
	SystemVendor    string  `json:"system_vendor,omitempty"`
	SystemModel     string  `json:"system_model,omitempty"`
	SystemSerial    string  `json:"system_serial,omitempty"`
	BIOSVendor      string  `json:"bios_vendor,omitempty"`
	BIOSVersion     string  `json:"bios_version,omitempty"`
	BIOSDate        string  `json:"bios_date,omitempty"`
	Virtualization  string  `json:"virtualization,omitempty"` // none|kvm|vmware|docker|wsl|...
	// Temperatures is a point-in-time thermal reading taken at inventory time.
	// Important on ARM SBCs (RPi, HA Green, Jetson) where the SoC throttles
	// when these climb. Empty on hardware without thermal sensors.
	Temperatures []TempReading `json:"temperatures,omitempty"`
}

// TempReading is one thermal sensor sample.
type TempReading struct {
	Name    string  `json:"name"`           // e.g. "soc-thermal", "cpu_thermal"
	Type    string  `json:"type,omitempty"` // kernel-reported zone type
	Celsius float64 `json:"celsius"`
}

// OSInfo describes the operating system.
type OSInfo struct {
	Family      string    `json:"family"` // linux | darwin | windows | ...
	Distro      string    `json:"distro,omitempty"`
	Version     string    `json:"version,omitempty"`
	Build       string    `json:"build,omitempty"`
	Kernel      string    `json:"kernel,omitempty"`
	KernelArch  string    `json:"kernel_arch,omitempty"`
	Hostname    string    `json:"hostname,omitempty"`
	Timezone    string    `json:"timezone,omitempty"`
	BootTime    time.Time `json:"boot_time,omitempty"`
	InstallDate time.Time `json:"install_date,omitempty"`
}

// DiskDevice describes a physical disk (not a mountpoint).
type DiskDevice struct {
	Name       string          `json:"name"` // sda, nvme0n1, disk0
	Model      string          `json:"model,omitempty"`
	Serial     string          `json:"serial,omitempty"`
	SizeBytes  uint64          `json:"size_bytes,omitempty"`
	Type       string          `json:"type,omitempty"` // hdd|ssd|nvme|virtual|unknown
	Removable  bool            `json:"removable,omitempty"`
	Partitions []DiskPartition `json:"partitions,omitempty"`
	SMART      *SMARTHealth    `json:"smart,omitempty"`
}

// SMARTHealth is a curated subset of S.M.A.R.T. attributes that actually
// indicate impending failure. Full attribute dumps are huge and noisy; we
// only carry what an operator looks at.
type SMARTHealth struct {
	OverallHealth       string  `json:"overall_health,omitempty"` // PASSED|FAILED|UNKNOWN
	TemperatureCelsius  float64 `json:"temperature_c,omitempty"`
	PowerOnHours        uint64  `json:"power_on_hours,omitempty"`
	PowerCycleCount     uint64  `json:"power_cycle_count,omitempty"`
	ReallocatedSectors  uint64  `json:"reallocated_sectors,omitempty"` // SAS/SATA
	PendingSectors      uint64  `json:"pending_sectors,omitempty"`
	UncorrectableErrors uint64  `json:"uncorrectable_errors,omitempty"`
	MediaWearoutPercent float64 `json:"media_wearout_pct,omitempty"`   // SSD wear (0=new, 100=spent)
	PercentageUsed      float64 `json:"percentage_used,omitempty"`     // NVMe (0=new, 100=spent)
	AvailableSparePct   float64 `json:"available_spare_pct,omitempty"` // NVMe
	DataUnitsReadGB     float64 `json:"data_units_read_gb,omitempty"`  // NVMe
	DataUnitsWrittenGB  float64 `json:"data_units_written_gb,omitempty"`
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
	Name          string   `json:"name"`
	MAC           string   `json:"mac,omitempty"`
	PermMAC       string   `json:"perm_mac,omitempty"` // permanent hardware MAC when active MAC differs (e.g. bond master)
	MTU           int      `json:"mtu,omitempty"`
	IsUp          bool     `json:"is_up"`
	IsLoopback    bool     `json:"is_loopback,omitempty"`
	LinkSpeedMbps int      `json:"link_speed_mbps,omitempty"`
	Duplex        string   `json:"duplex,omitempty"`
	IPv4          []string `json:"ipv4,omitempty"` // CIDR strings
	IPv6          []string `json:"ipv6,omitempty"` // CIDR strings
	Type          string   `json:"type,omitempty"` // ethernet|wifi|virtual|loopback
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
	Proto       string `json:"proto"`   // tcp|udp|tcp6|udp6
	Address     string `json:"address"` // bind address (v4 or v6)
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
	ID                string `json:"id,omitempty"`      // Docker daemon ID
	Version           string `json:"version,omitempty"` // server version
	APIVersion        string `json:"api_version,omitempty"`
	OS                string `json:"os,omitempty"`      // operating-system label from daemon
	OSType            string `json:"os_type,omitempty"` // linux | windows
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
	ID         string    `json:"id"`                 // full 64-char ID
	Name       string    `json:"name,omitempty"`     // primary name without leading slash
	Names      []string  `json:"names,omitempty"`    // all names (containers can have aliases)
	Image      string    `json:"image,omitempty"`    // image reference as run (may be a tag or digest)
	ImageID    string    `json:"image_id,omitempty"` // sha256:...
	Command    string    `json:"command,omitempty"`  // entrypoint+cmd flattened
	CreatedAt  time.Time `json:"created_at,omitempty"`
	StartedAt  time.Time `json:"started_at,omitempty"`
	FinishedAt time.Time `json:"finished_at,omitempty"`

	// Lifecycle
	State        string `json:"state,omitempty"`  // running|exited|paused|restarting|created|dead|removing
	Status       string `json:"status,omitempty"` // human "Up 3 hours" / "Exited (0) 5 minutes ago"
	Health       string `json:"health,omitempty"` // healthy|unhealthy|starting|none
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
	RestartPolicy   string `json:"restart_policy,omitempty"` // no|always|on-failure|unless-stopped
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
	NanoCPUs    int64  `json:"nano_cpus,omitempty"` // 1e9 = 1 CPU
	CPUShares   int64  `json:"cpu_shares,omitempty"`
	MemoryLimit uint64 `json:"memory_limit_bytes,omitempty"`
	MemorySwap  int64  `json:"memory_swap_bytes,omitempty"` // -1 = unlimited
	PidsLimit   int64  `json:"pids_limit,omitempty"`

	// Current usage (best-effort; populated only when stats are sampled)
	CPUPercent      float64 `json:"cpu_pct,omitempty"`
	MemUsageBytes   uint64  `json:"mem_usage_bytes,omitempty"`
	MemPercent      float64 `json:"mem_pct,omitempty"`
	NetRxBytes      uint64  `json:"net_rx_bytes,omitempty"`
	NetTxBytes      uint64  `json:"net_tx_bytes,omitempty"`
	BlockReadBytes  uint64  `json:"block_read_bytes,omitempty"`
	BlockWriteBytes uint64  `json:"block_write_bytes,omitempty"`
}

// ContainerNetwork is one network the container is attached to.
type ContainerNetwork struct {
	Name       string   `json:"name"`
	NetworkID  string   `json:"network_id,omitempty"`
	IPv4       string   `json:"ipv4,omitempty"`
	IPv4Prefix int      `json:"ipv4_prefix,omitempty"`
	IPv6       string   `json:"ipv6,omitempty"`
	IPv6Prefix int      `json:"ipv6_prefix,omitempty"`
	Gateway    string   `json:"gateway,omitempty"`
	MAC        string   `json:"mac,omitempty"`
	Aliases    []string `json:"aliases,omitempty"`
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
	Type        string `json:"type"`             // volume|bind|tmpfs
	Name        string `json:"name,omitempty"`   // volume name (volume mounts only)
	Source      string `json:"source,omitempty"` // host path or volume name
	Destination string `json:"destination"`      // container path
	Driver      string `json:"driver,omitempty"`
	Mode        string `json:"mode,omitempty"` // rw|ro|... raw mode string
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

// HassioInfo holds Home Assistant Supervisor facts collected via the
// http://supervisor/* API. Populated only when running as an HA add-on
// (SUPERVISOR_TOKEN env var is set by Supervisor on container start).
type HassioInfo struct {
	SupervisorVersion string        `json:"supervisor_version,omitempty"`
	CoreVersion       string        `json:"core_version,omitempty"`
	OSVersion         string        `json:"os_version,omitempty"` // HAOS / HA Green firmware
	Channel           string        `json:"channel,omitempty"`    // stable | beta | dev
	Arch              string        `json:"arch,omitempty"`       // aarch64 | amd64 | armv7
	Machine           string        `json:"machine,omitempty"`    // e.g. "green", "yellow", "raspberrypi4"
	Hostname          string        `json:"hostname,omitempty"`   // host's real hostname (Supervisor's view)
	HostOS            string        `json:"host_os,omitempty"`    // e.g. "Home Assistant OS 14.2"
	HostKernel        string        `json:"host_kernel,omitempty"`
	Chassis           string        `json:"chassis,omitempty"` // embedded | desktop | server | vm
	BootTime          time.Time     `json:"boot_time,omitempty"`
	Addons            []HassioAddon `json:"addons,omitempty"`
}

// HassioAddon is one installed add-on.
type HassioAddon struct {
	Slug    string `json:"slug"`
	Name    string `json:"name,omitempty"`
	Version string `json:"version,omitempty"`
	State   string `json:"state,omitempty"`  // started | stopped | error
	Update  bool   `json:"update,omitempty"` // update_available
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
