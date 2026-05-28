package adapters

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/terrain"
)

// TerrainDNS adapts []terrain.DNSRecord (PTR lookup results from the LAN DNS
// server) into pipeline observations with source kind "terrain-dns".
type TerrainDNS struct{}

func (a *TerrainDNS) Kind() string { return "terrain-dns" }

func (a *TerrainDNS) Adapt(_ context.Context, sourceID string, payload any) ([]pipeline.ObservationInput, error) {
	records, ok := payload.([]terrain.DNSRecord)
	if !ok {
		return nil, fmt.Errorf("terrain-dns adapter: expected []terrain.DNSRecord, got %T", payload)
	}

	now := time.Now().UTC()
	inputs := make([]pipeline.ObservationInput, 0, len(records))

	for _, rec := range records {
		if rec.MAC == "" || rec.Hostname == "" {
			continue
		}
		raw, _ := json.Marshal(rec)
		obs := pipeline.ObservationInput{
			MAC:        rec.MAC,
			SourceID:   sourceID,
			SourceKind: "terrain-dns",
			ObsType:    "dns-ptr",
			ObservedAt: now,
			RawJSON:    string(raw),
			Hostname:   rec.Hostname,
		}
		if rec.IP != "" {
			obs.Addresses = []pipeline.AddressInput{
				{Address: rec.IP, Family: "ipv4"},
			}
		}
		inputs = append(inputs, obs)
	}

	return inputs, nil
}
