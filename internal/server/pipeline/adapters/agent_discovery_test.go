package adapters

import (
	"context"
	"testing"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

func TestAgentDiscovery_Kind(t *testing.T) {
	a := &AgentDiscovery{}
	if a.Kind() != "agent-discovery" {
		t.Fatalf("expected kind agent-discovery, got %s", a.Kind())
	}
}

func TestAgentDiscovery_Adapt(t *testing.T) {
	a := &AgentDiscovery{}
	ctx := context.Background()

	req := &proto.DiscoveryRequest{
		AgentID: "agent-1",
		Sightings: []proto.Sighting{
			{
				MAC:      "AA:BB:CC:DD:EE:FF",
				IP:       "192.168.1.50",
				Hostname: "my-device",
				Method:   "arp",
				SeenAt:   time.Date(2025, 6, 1, 12, 0, 0, 0, time.UTC),
			},
			{
				MAC:    "", // No MAC — should be skipped.
				IP:     "192.168.1.51",
				Method: "arp",
			},
			{
				MAC:    "11:22:33:44:55:66",
				IP:     "2001:db8::1",
				Method: "mdns",
				SeenAt: time.Date(2025, 6, 1, 12, 0, 0, 0, time.UTC),
			},
		},
	}

	inputs, err := a.Adapt(ctx, "src-1", req)
	if err != nil {
		t.Fatal(err)
	}
	if len(inputs) != 2 {
		t.Fatalf("expected 2 observations (skipping empty MAC), got %d", len(inputs))
	}

	// First observation.
	obs := inputs[0]
	if obs.MAC != "AA:BB:CC:DD:EE:FF" {
		t.Fatalf("expected MAC AA:BB:CC:DD:EE:FF, got %s", obs.MAC)
	}
	if obs.Hostname != "my-device" {
		t.Fatalf("expected hostname my-device, got %s", obs.Hostname)
	}
	if obs.SourceKind != "agent" {
		t.Fatalf("expected source kind agent, got %s", obs.SourceKind)
	}
	if obs.ObsType != "discovery" {
		t.Fatalf("expected obs type discovery, got %s", obs.ObsType)
	}
	if len(obs.Addresses) != 1 || obs.Addresses[0].Family != "ipv4" {
		t.Fatalf("expected 1 ipv4 address, got %v", obs.Addresses)
	}

	// IPv6 observation.
	obs2 := inputs[1]
	if len(obs2.Addresses) != 1 || obs2.Addresses[0].Family != "ipv6" {
		t.Fatalf("expected 1 ipv6 address, got %v", obs2.Addresses)
	}
}

func TestAgentDiscovery_WrongType(t *testing.T) {
	a := &AgentDiscovery{}
	_, err := a.Adapt(context.Background(), "src-1", "not a request")
	if err == nil {
		t.Fatal("expected error for wrong payload type")
	}
}
