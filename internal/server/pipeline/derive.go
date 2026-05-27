package pipeline

import "time"

// HostnamePriority returns the priority for a given source kind.
// Lower number = higher priority. Agent-reported hostnames win over
// network-derived names.
func HostnamePriority(sourceKind string) int {
	switch sourceKind {
	case "agent":
		return 10
	case "user-input":
		return 20
	case "terrain-dns":
		return 30
	case "terrain-dhcp":
		return 40
	case "snmp-poller":
		return 50
	case "nmap-scanner":
		return 60
	default:
		return 100
	}
}

// StaleThreshold is how long after last_seen_at an entity is considered stale.
const StaleThreshold = 7 * 24 * time.Hour

// IsStale returns true if the entity hasn't been seen within the threshold.
func IsStale(lastSeen time.Time) bool {
	return time.Since(lastSeen) > StaleThreshold
}
