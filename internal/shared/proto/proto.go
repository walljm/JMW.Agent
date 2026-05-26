// Package proto contains wire types shared between server and agent.
//
// API-stable: changes here are versioned API changes. Add fields, do not
// remove or rename them. Breaking changes go in a new package version.
package proto

import "time"

// RegisterRequest is sent by an agent on first contact.
type RegisterRequest struct {
	AgentID           string   `json:"agent_id"`
	Hostname          string   `json:"hostname"`
	OS                string   `json:"os"`
	Arch              string   `json:"arch"`
	Version           string   `json:"version"`
	PSK               string   `json:"psk,omitempty"`
	PublicKey         string   `json:"public_key,omitempty"` // base64-encoded ed25519 pubkey (future)
	EnabledSubsystems []string `json:"enabled_subsystems"`
}

// RegisterResponse tells the agent what happened.
type RegisterResponse struct {
	Status            string `json:"status"` // "approved" | "pending"
	Message           string `json:"message"`
	HeartbeatInterval int    `json:"heartbeat_interval"` // seconds
	ServerCertSHA256  string `json:"server_cert_sha256"` // hex; agent pins this
}

// HeartbeatRequest is sent on every interval.
type HeartbeatRequest struct {
	AgentID           string    `json:"agent_id"`
	Version           string    `json:"version"`
	EnabledSubsystems []string  `json:"enabled_subsystems"`
	Uptime            int64     `json:"uptime_seconds"`
	SentAt            time.Time `json:"sent_at"`
}

// HeartbeatResponse may include directives back to the agent.
type HeartbeatResponse struct {
	Approved        bool        `json:"approved"`
	NextHeartbeatIn int         `json:"next_heartbeat_in"` // seconds
	Update          *UpdateInfo `json:"update,omitempty"`
}

// UpdateInfo tells the agent that a newer binary is available for its
// platform. URL is relative to the server base URL; the agent calls it
// over the same PSK + pinned-TLS channel as every other API request.
type UpdateInfo struct {
	Version string `json:"version"`
	URL     string `json:"url"`
	SHA256  string `json:"sha256"`
	Size    int64  `json:"size,omitempty"`
}

// MetricSnapshot is one point-in-time system metrics reading.
type MetricSnapshot struct {
	Timestamp     time.Time           `json:"ts"`
	CPUPercent    float64             `json:"cpu_pct"`
	MemUsedBytes  uint64              `json:"mem_used_bytes"`
	MemTotalBytes uint64              `json:"mem_total_bytes"`
	Load1         float64             `json:"load_1,omitempty"`
	Load5         float64             `json:"load_5,omitempty"`
	Load15        float64             `json:"load_15,omitempty"`
	UptimeSeconds int64               `json:"uptime_seconds"`
	Disks         []DiskSnapshot      `json:"disks,omitempty"`
	Interfaces    []InterfaceSnapshot `json:"interfaces,omitempty"`
}

// DiskSnapshot is per-disk metrics at a point in time.
type DiskSnapshot struct {
	Device     string `json:"device"`
	Mountpoint string `json:"mountpoint,omitempty"`
	UsedBytes  uint64 `json:"used_bytes"`
	TotalBytes uint64 `json:"total_bytes"`
	FSType     string `json:"fs_type,omitempty"`
}

// InterfaceSnapshot is per-network-interface metrics.
type InterfaceSnapshot struct {
	Name      string `json:"name"`
	IP        string `json:"ip,omitempty"`
	MAC       string `json:"mac,omitempty"`
	RxBytes   uint64 `json:"rx_bytes"`
	TxBytes   uint64 `json:"tx_bytes"`
	RxPackets uint64 `json:"rx_packets"`
	TxPackets uint64 `json:"tx_packets"`
	IsUp      bool   `json:"is_up"`
}

// MetricsRequest carries a batch of snapshots.
type MetricsRequest struct {
	AgentID   string           `json:"agent_id"`
	Snapshots []MetricSnapshot `json:"snapshots"`
}

// MetricsResponse is a simple ack.
type MetricsResponse struct {
	Accepted int `json:"accepted"`
}

// Sighting is one ARP/mDNS observation reported by an agent.
type Sighting struct {
	IP              string            `json:"ip"`
	MAC             string            `json:"mac"`
	Hostname        string            `json:"hostname,omitempty"`
	Vendor          string            `json:"vendor,omitempty"`
	Kind            string            `json:"kind,omitempty"` // e.g. "container" when seen on docker0/br-*
	Method          string            `json:"method"`         // arp | mdns | ping
	SeenAt          time.Time         `json:"seen_at"`
	Services        []string          `json:"services,omitempty"`
	TXT             map[string]string `json:"txt,omitempty"`
	HostnameSources map[string]string `json:"hostname_sources,omitempty"`
	// Probes carries optional, per-protocol probe results. Each value is
	// a flat string-keyed map so the server can store and render them
	// without knowing the schema. Known keys: "eureka", "ipp", "roku",
	// "airplay", "ldap", "dhcp", "ssh_fp", "title".
	Probes map[string]map[string]string `json:"probes,omitempty"`
}

// NetworkContext identifies the network segment an agent was on when it
// performed a discovery scan. The server uses this to auto-create and
// associate devices with logical network records.
type NetworkContext struct {
	GatewayMAC string `json:"gateway_mac"`          // primary stable identity of the network
	CIDR       string `json:"cidr,omitempty"`       // agent's interface CIDR (e.g. "192.168.1.0/24")
	Interface  string `json:"interface,omitempty"`   // OS interface name (e.g. "en0", "eth0")
	SSID       string `json:"ssid,omitempty"`       // Wi-Fi SSID, empty for wired
}

// DiscoveryRequest carries a batch of sightings from one agent.
type DiscoveryRequest struct {
	AgentID   string          `json:"agent_id"`
	Sightings []Sighting      `json:"sightings"`
	Network   *NetworkContext `json:"network,omitempty"` // nil for legacy agents that haven't upgraded
}

// DiscoveryResponse acks how many sightings were accepted.
type DiscoveryResponse struct {
	Accepted int `json:"accepted"`
}

// ErrorResponse is the standard error shape.
type ErrorResponse struct {
	Error   string `json:"error"`
	Code    string `json:"code,omitempty"`
	Message string `json:"message,omitempty"`
}
