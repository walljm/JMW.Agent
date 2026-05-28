package adapters

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	"github.com/walljm/jmwagent/internal/server/dek"
	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

func testStore(t *testing.T) *store.Store {
	t.Helper()
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "test.db")
	st, err := store.Open(context.Background(), dbPath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { st.Close() })
	return st
}

func createTestSource(t *testing.T, st *store.Store) string {
	t.Helper()
	ctx := context.Background()
	keyPath := filepath.Join(t.TempDir(), "test.key")
	key, err := dek.LoadOrCreate(keyPath)
	if err != nil {
		t.Fatal(err)
	}
	src := &store.Source{
		Name:    "test-source",
		Kind:    "terrain-dhcp",
		Enabled: true,
	}
	err = st.CreateSource(ctx, src, key)
	if err != nil {
		t.Fatal(err)
	}
	return src.ID
}

func TestIngestor_AgentDiscovery_EndToEnd(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	sourceID := createTestSource(t, st)

	ing := pipeline.NewIngestor(st, &AgentDiscovery{})

	req := &proto.DiscoveryRequest{
		AgentID: "agent-1",
		Sightings: []proto.Sighting{
			{
				MAC:    "AA:BB:CC:11:22:33",
				IP:     "192.168.1.100",
				Method: "arp",
				SeenAt: time.Now().UTC(),
				// smb (priority 15) beats agent (priority 25) — smb-name should win.
				HostnameSources: map[string]string{
					"agent": "agent-name",
					"smb":   "smb-name",
					"mdns":  "mdns-name.local", // will be normalized to mdns-name
				},
			},
			{
				MAC:    "AA:BB:CC:11:22:44",
				IP:     "192.168.1.101",
				Method: "mdns",
				SeenAt: time.Now().UTC(),
				HostnameSources: map[string]string{
					"mdns": "laptop-two",
				},
			},
		},
	}

	result, err := ing.Ingest(ctx, "agent-discovery", sourceID, req)
	if err != nil {
		t.Fatal(err)
	}
	if result.InterfacesResolved != 2 {
		t.Fatalf("expected 2 resolved, got %d", result.InterfacesResolved)
	}
	if result.ObservationsStored != 2 {
		t.Fatalf("expected 2 observations (one per sighting), got %d", result.ObservationsStored)
	}
	// First sighting has 3 hostnames (agent + smb + mdns after normalization).
	// Second sighting has 1 hostname.
	if result.HostnamesUpdated != 4 {
		t.Fatalf("expected 4 hostnames, got %d", result.HostnamesUpdated)
	}

	// Verify smb-name wins for the first sighting (smb priority 15 < agent 25 < mdns 40).
	iface, err := st.GetInterfaceByMAC(ctx, "aa:bb:cc:11:22:33")
	if err != nil {
		t.Fatal(err)
	}
	if iface.ID == "" {
		t.Fatal("expected interface to exist")
	}
	best, err := st.GetCanonicalHostname(ctx, iface.ID)
	if err != nil {
		t.Fatal(err)
	}
	if best != "smb-name" {
		t.Fatalf("expected smb-name (priority 15), got %s", best)
	}
}

func TestIngestor_AgentDiscovery_NormalizesHostnames(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	sourceID := createTestSource(t, st)

	ing := pipeline.NewIngestor(st, &AgentDiscovery{})

	req := &proto.DiscoveryRequest{
		AgentID: "agent-1",
		Sightings: []proto.Sighting{
			{
				MAC:    "BB:CC:DD:11:22:33",
				IP:     "192.168.1.200",
				Method: "arp",
				SeenAt: time.Now().UTC(),
				HostnameSources: map[string]string{
					"agent": "host.docker.internal", // rejected — docker-internal
					"mdns":  "mypc.local",            // normalized to "mypc"
				},
			},
		},
	}

	result, err := ing.Ingest(ctx, "agent-discovery", sourceID, req)
	if err != nil {
		t.Fatal(err)
	}
	// Only mdns survives (normalized to "mypc"); docker-internal is rejected.
	if result.HostnamesUpdated != 1 {
		t.Fatalf("expected 1 hostname after normalization, got %d", result.HostnamesUpdated)
	}

	iface, err := st.GetInterfaceByMAC(ctx, "bb:cc:dd:11:22:33")
	if err != nil {
		t.Fatal(err)
	}
	best, err := st.GetCanonicalHostname(ctx, iface.ID)
	if err != nil {
		t.Fatal(err)
	}
	if best != "mypc" {
		t.Fatalf("expected normalized hostname mypc, got %s", best)
	}
}

func TestIngestor_AgentInventory_EndToEnd(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	sourceID := createTestSource(t, st)

	ing := pipeline.NewIngestor(st, &AgentInventory{})

	inv := &proto.Inventory{
		CollectedAt: time.Now().UTC(),
		Hardware: proto.HardwareInfo{
			SystemVendor: "Dell",
			SystemModel:  "OptiPlex 7080",
			CPUModel:     "i7-10700",
			CPUCores:     8,
		},
		OS: proto.OSInfo{
			Family:   "linux",
			Distro:   "Ubuntu",
			Version:  "22.04",
			Hostname: "devbox",
		},
		Network: proto.NetworkInfo{
			Interfaces: []proto.NetInterface{
				{
					Name: "eth0",
					MAC:  "DE:AD:BE:EF:00:01",
					IsUp: true,
					IPv4: []string{"10.0.0.5/24"},
				},
				{
					Name:       "lo",
					IsLoopback: true, // Should be skipped.
				},
			},
		},
	}

	result, err := ing.Ingest(ctx, "agent-inventory", sourceID, inv)
	if err != nil {
		t.Fatal(err)
	}
	if result.InterfacesResolved != 1 {
		t.Fatalf("expected 1 resolved, got %d", result.InterfacesResolved)
	}
	if result.AddressesUpdated != 1 {
		t.Fatalf("expected 1 address, got %d", result.AddressesUpdated)
	}

	// Verify hostname.
	iface, err := st.GetInterfaceByMAC(ctx, "de:ad:be:ef:00:01")
	if err != nil {
		t.Fatal(err)
	}
	hostname, err := st.GetCanonicalHostname(ctx, iface.ID)
	if err != nil {
		t.Fatal(err)
	}
	if hostname != "devbox" {
		t.Fatalf("expected hostname devbox, got %s", hostname)
	}
}

func TestIngestor_UnknownKind(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	ing := pipeline.NewIngestor(st)

	_, err := ing.Ingest(ctx, "unknown-kind", "src-1", nil)
	if err == nil {
		t.Fatal("expected error for unknown kind")
	}
}

func TestIngestor_HasAdapter(t *testing.T) {
	st := testStore(t)
	ing := pipeline.NewIngestor(st, &AgentDiscovery{}, &TerrainDHCP{})

	if !ing.HasAdapter("agent-discovery") {
		t.Fatal("expected agent-discovery adapter")
	}
	if !ing.HasAdapter("terrain-dhcp") {
		t.Fatal("expected terrain-dhcp adapter")
	}
	if ing.HasAdapter("nope") {
		t.Fatal("unexpected adapter for 'nope'")
	}
}
