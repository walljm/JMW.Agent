package adapters

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/terrain"
)

// TerrainDHCP adapts terrain.DHCPStatus into pipeline observations.
type TerrainDHCP struct{}

func (a *TerrainDHCP) Kind() string { return "terrain-dhcp" }

func (a *TerrainDHCP) Adapt(_ context.Context, sourceID string, payload any) ([]pipeline.ObservationInput, error) {
	status, ok := payload.(*terrain.DHCPStatus)
	if !ok {
		return nil, fmt.Errorf("terrain-dhcp adapter: expected *terrain.DHCPStatus, got %T", payload)
	}

	now := time.Now().UTC()
	inputs := make([]pipeline.ObservationInput, 0, len(status.Leases))

	for i := range status.Leases {
		lease := &status.Leases[i]
		if lease.MAC == "" {
			continue
		}

		raw, _ := json.Marshal(lease)

		obs := pipeline.ObservationInput{
			MAC:        lease.MAC,
			SourceID:   sourceID,
			SourceKind: "terrain-dhcp",
			ObsType:    "dhcp-lease",
			ObservedAt: now,
			RawJSON:    string(raw),
			Hostname:   lease.Hostname,
		}

		if lease.IP != "" {
			obs.Addresses = []pipeline.AddressInput{
				{Address: lease.IP, Family: "ipv4"},
			}
		}

		inputs = append(inputs, obs)
	}

	return inputs, nil
}
