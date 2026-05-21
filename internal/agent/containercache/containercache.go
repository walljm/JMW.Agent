// Package containercache holds the most recent local-container MAC/IP table
// for cross-package lookup.
//
// The collect package populates it after each Docker (or other runtime)
// inventory cycle; the discover package consults it during ARP scans so a
// container's friendly name and runtime appear on every sighting instead of
// only on the periodic inventory snapshot.
//
// Decoupling collect from discover this way avoids an import cycle and lets
// either side be exercised in isolation. Lookups are read-mostly and cheap
// (one map hit under an RLock), and a stale entry is harmless — it just
// means a sighting is labelled with a container that has since stopped,
// which the next inventory cycle corrects.
package containercache

import (
	"strings"
	"sync"
	"time"
)

// Entry describes one local container as a discovery target.
type Entry struct {
	Name    string // primary container name (no leading slash)
	Image   string // image reference as run
	Network string // first network the container is attached to
	Runtime string // "docker", "podman", ...
	Updated time.Time
}

var (
	mu        sync.RWMutex
	byMAC     = map[string]Entry{}
	updatedAt time.Time
)

// Replace atomically swaps the cache with a fresh snapshot.
//
// MACs are normalised to lowercase. Empty MACs are dropped — a container on
// host networking has no L2 identity and can't be matched against ARP.
func Replace(entries map[string]Entry) {
	now := time.Now().UTC()
	next := make(map[string]Entry, len(entries))
	for mac, e := range entries {
		mac = strings.ToLower(strings.TrimSpace(mac))
		if mac == "" {
			continue
		}
		if e.Updated.IsZero() {
			e.Updated = now
		}
		next[mac] = e
	}
	mu.Lock()
	byMAC = next
	updatedAt = now
	mu.Unlock()
}

// Lookup returns the cache entry for a MAC, if any. The bool is false when
// the MAC is unknown.
func Lookup(mac string) (Entry, bool) {
	mac = strings.ToLower(strings.TrimSpace(mac))
	if mac == "" {
		return Entry{}, false
	}
	mu.RLock()
	defer mu.RUnlock()
	e, ok := byMAC[mac]
	return e, ok
}

// LastUpdated returns when Replace was last called. Zero means never.
func LastUpdated() time.Time {
	mu.RLock()
	defer mu.RUnlock()
	return updatedAt
}
