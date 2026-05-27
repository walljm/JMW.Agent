package adapters

import (
	"context"
	"testing"
	"time"

	"github.com/walljm/jmwagent/internal/server/terrain"
)

func TestTerrainDHCP_Kind(t *testing.T) {
	a := &TerrainDHCP{}
	if a.Kind() != "terrain-dhcp" {
		t.Fatalf("expected kind terrain-dhcp, got %s", a.Kind())
	}
}

func TestTerrainDHCP_Adapt(t *testing.T) {
	a := &TerrainDHCP{}
	ctx := context.Background()

	status := &terrain.DHCPStatus{
		Enabled: true,
		Leases: []terrain.DHCPLease{
			{
				IP:       "192.168.1.100",
				MAC:      "AA:11:22:33:44:55",
				Hostname: "workstation-1",
				Expires:  time.Now().Add(time.Hour),
				Static:   false,
			},
			{
				IP:       "192.168.1.101",
				MAC:      "", // No MAC — skipped.
				Hostname: "orphan",
			},
			{
				IP:       "192.168.1.102",
				MAC:      "BB:11:22:33:44:55",
				Hostname: "", // No hostname — still valid.
				Expires:  time.Now().Add(2 * time.Hour),
			},
		},
	}

	inputs, err := a.Adapt(ctx, "src-dhcp", status)
	if err != nil {
		t.Fatal(err)
	}
	if len(inputs) != 2 {
		t.Fatalf("expected 2 observations (skipping empty MAC), got %d", len(inputs))
	}

	obs := inputs[0]
	if obs.MAC != "AA:11:22:33:44:55" {
		t.Fatalf("expected MAC AA:11:22:33:44:55, got %s", obs.MAC)
	}
	if obs.Hostname != "workstation-1" {
		t.Fatalf("expected hostname workstation-1, got %s", obs.Hostname)
	}
	if obs.SourceKind != "terrain-dhcp" {
		t.Fatalf("expected source kind terrain-dhcp, got %s", obs.SourceKind)
	}
	if obs.ObsType != "dhcp-lease" {
		t.Fatalf("expected obs type dhcp-lease, got %s", obs.ObsType)
	}
	if len(obs.Addresses) != 1 || obs.Addresses[0].Address != "192.168.1.100" {
		t.Fatalf("expected address 192.168.1.100, got %v", obs.Addresses)
	}

	// Second valid lease (no hostname).
	obs2 := inputs[1]
	if obs2.Hostname != "" {
		t.Fatalf("expected empty hostname, got %s", obs2.Hostname)
	}
}

func TestTerrainDHCP_WrongType(t *testing.T) {
	a := &TerrainDHCP{}
	_, err := a.Adapt(context.Background(), "src-1", "wrong")
	if err == nil {
		t.Fatal("expected error for wrong payload type")
	}
}
