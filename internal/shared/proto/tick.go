package proto

// TickRequest is the coalesced agent→server payload that replaces the
// separate /metrics, /discoveries, and /inventory endpoints.
type TickRequest struct {
	AgentID string `json:"agent_id"`
	Version string `json:"version,omitempty"`

	// Metrics section (always present when agent has collected snapshots).
	Metrics *MetricsRequest `json:"metrics,omitempty"`

	// Discoveries section (present when new sightings exist since last tick).
	Discoveries *DiscoveryRequest `json:"discoveries,omitempty"`

	// Inventory section (present periodically or when hardware/software changed).
	Inventory *InventoryRequest `json:"inventory,omitempty"`
}

// TickResponse is the server's response to a tick.
type TickResponse struct {
	Accepted bool        `json:"accepted"`
	Update   *UpdateInfo `json:"update,omitempty"`
}
