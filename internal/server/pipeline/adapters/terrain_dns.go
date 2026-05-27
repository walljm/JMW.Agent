package adapters

import (
	"context"
	"fmt"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/terrain"
)

// TerrainDNS adapts terrain.DNSStats into pipeline observations.
// DNS stats produce per-client observations (top clients → MAC lookup).
// Since DNS stats don't carry MAC addresses directly, this adapter
// produces observations keyed by IP for clients that appear in the top list.
// These will only resolve if the IP was previously seen via DHCP/discovery.
type TerrainDNS struct{}

func (a *TerrainDNS) Kind() string { return "terrain-dns" }

func (a *TerrainDNS) Adapt(_ context.Context, sourceID string, payload any) ([]pipeline.ObservationInput, error) {
	_, ok := payload.(*terrain.DNSStats)
	if !ok {
		return nil, fmt.Errorf("terrain-dns adapter: expected *terrain.DNSStats, got %T", payload)
	}

	// DNS stats don't carry MAC addresses — they report client IPs and query counts.
	// Until IP-based resolution is implemented, this adapter produces no observations.
	// It exists as a placeholder so the Ingestor accepts "terrain-dns" kind.
	return nil, nil
}
