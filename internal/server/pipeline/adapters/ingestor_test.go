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
				MAC:      "AA:BB:CC:11:22:33",
				IP:       "192.168.1.100",
				Hostname: "desktop-one",
				Method:   "arp",
				SeenAt:   time.Now().UTC(),
			},
			{
				MAC:      "AA:BB:CC:11:22:44",
				IP:       "192.168.1.101",
				Hostname: "laptop-two",
				Method:   "mdns",
				SeenAt:   time.Now().UTC(),
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
		t.Fatalf("expected 2 observations, got %d", result.ObservationsStored)
	}
	if result.HostnamesUpdated != 2 {
		t.Fatalf("expected 2 hostnames, got %d", result.HostnamesUpdated)
	}

	// Verify interface was created with normalized MAC.
	iface, err := st.GetInterfaceByMAC(ctx, "aa:bb:cc:11:22:33")
	if err != nil {
		t.Fatal(err)
	}
	if iface.ID == "" {
		t.Fatal("expected interface to exist")
	}

	// Verify hostname was set.
	hostname, err := st.GetCanonicalHostname(ctx, iface.ID)
	if err != nil {
		t.Fatal(err)
	}
	if hostname != "desktop-one" {
		t.Fatalf("expected hostname desktop-one, got %s", hostname)
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
