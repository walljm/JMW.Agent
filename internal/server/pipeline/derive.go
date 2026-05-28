package pipeline

import (
	"time"

	"github.com/walljm/jmwagent/internal/shared/hostname"
)

// HostnamePriority returns the priority for a given source kind.
// Lower number = higher priority. Delegates to the shared hostname package so
// all layers (pipeline, store, agent) use the same ordering.
func HostnamePriority(sourceKind string) int {
	return hostname.Priority(sourceKind)
}

// StaleThreshold is how long after last_seen_at an entity is considered stale.
const StaleThreshold = 7 * 24 * time.Hour

// IsStale returns true if the entity hasn't been seen within the threshold.
func IsStale(lastSeen time.Time) bool {
	return time.Since(lastSeen) > StaleThreshold
}
