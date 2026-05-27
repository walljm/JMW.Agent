package adapters

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// AgentInventory adapts a proto.Inventory into pipeline observations.
// It produces one observation per network interface (the canonical identity)
// plus enrichment data for hardware, system, and disks.
type AgentInventory struct{}

func (a *AgentInventory) Kind() string { return "agent-inventory" }

func (a *AgentInventory) Adapt(_ context.Context, sourceID string, payload any) ([]pipeline.ObservationInput, error) {
	inv, ok := payload.(*proto.Inventory)
	if !ok {
		return nil, fmt.Errorf("agent-inventory adapter: expected *proto.Inventory, got %T", payload)
	}

	observedAt := inv.CollectedAt
	if observedAt.IsZero() {
		observedAt = time.Now().UTC()
	}

	raw, _ := json.Marshal(inv)
	rawStr := string(raw)

	inputs := make([]pipeline.ObservationInput, 0, len(inv.Network.Interfaces))

	for i := range inv.Network.Interfaces {
		iface := &inv.Network.Interfaces[i]
		if iface.MAC == "" || iface.IsLoopback {
			continue
		}

		obs := pipeline.ObservationInput{
			MAC:        iface.MAC,
			SourceID:   sourceID,
			SourceKind: "agent",
			ObsType:    "inventory",
			ObservedAt: observedAt,
			RawJSON:    rawStr,
			Hostname:   inv.OS.Hostname,
			Hints: &pipeline.InterfaceHints{
				SystemVendor:  inv.Hardware.SystemVendor,
				SystemModel:   inv.Hardware.SystemModel,
				SystemSerial:  inv.Hardware.SystemSerial,
				InterfaceName: iface.Name,
			},
		}

		// Collect addresses from this interface.
		for _, ip := range iface.IPv4 {
			obs.Addresses = append(obs.Addresses, pipeline.AddressInput{
				Address: ip,
				Family:  "ipv4",
			})
		}
		for _, ip := range iface.IPv6 {
			obs.Addresses = append(obs.Addresses, pipeline.AddressInput{
				Address: ip,
				Family:  "ipv6",
			})
		}

		inputs = append(inputs, obs)
	}

	return inputs, nil
}
