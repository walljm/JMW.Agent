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

	// If this is a Home Assistant add-on, surface the Supervisor's hostname
	// as a separate "hassio" source (priority 10) alongside the OS hostname
	// (source "agent", priority 25). On HA agents the agent-side collector
	// already overwrites inv.OS.Hostname with the Hassio hostname, so both
	// entries will have the same value — but labelling the source correctly
	// means the server can rank it above a generic agent self-report.
	var hassioHostname string
	if inv.Hassio != nil {
		hassioHostname = inv.Hassio.Hostname
	}

	// Build hardware-level fingerprints that apply to every interface on this machine.
	hwFingerprints := make([]pipeline.Fingerprint, 0, 4)
	if inv.Hardware.SystemSerial != "" && inv.Hardware.SystemVendor != "" {
		hwFingerprints = append(hwFingerprints, pipeline.Fingerprint{
			Kind:   "serial:system",
			Value:  inv.Hardware.SystemVendor + "\x00" + inv.Hardware.SystemSerial,
			Source: "agent",
		})
	}
	// BoardSerial is not yet collected by the agent proto. When it is, add a
	// "serial:board" fingerprint here: BoardVendor + "\x00" + BoardSerial.
	if inv.Docker != nil && inv.Docker.Engine != nil && inv.Docker.Engine.ID != "" {
		hwFingerprints = append(hwFingerprints, pipeline.Fingerprint{
			Kind:   "docker_engine_id",
			Value:  inv.Docker.Engine.ID,
			Source: "agent",
		})
	}

	inputs := make([]pipeline.ObservationInput, 0, len(inv.Network.Interfaces))

	for i := range inv.Network.Interfaces {
		iface := &inv.Network.Interfaces[i]
		if iface.MAC == "" || iface.IsLoopback {
			continue
		}

		mac := iface.MAC
		if iface.PermMAC != "" {
			mac = iface.PermMAC
		}

		obs := pipeline.ObservationInput{
			MAC:        mac,
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
			Fingerprints: hwFingerprints,
		}
		if hassioHostname != "" {
			obs.HostnameSources = map[string]string{"hassio": hassioHostname}
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
