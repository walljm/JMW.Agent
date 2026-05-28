package pipeline

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	"github.com/walljm/jmwagent/internal/server/dek"
	"github.com/walljm/jmwagent/internal/server/store"
)

func TestPipeline_ProcessEmpty(t *testing.T) {
	st := testStore(t)
	p := New(st)

	result, err := p.Process(context.Background(), nil)
	if err != nil {
		t.Fatal(err)
	}
	if result.InterfacesResolved != 0 {
		t.Fatalf("expected 0 resolved, got %d", result.InterfacesResolved)
	}
}

func TestPipeline_SingleObservation(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Need a source for the observation.
	sourceID := createTestSource(t, st)

	p := New(st)
	inputs := []ObservationInput{
		{
			MAC:        "AA:BB:CC:DD:EE:10",
			SourceID:   sourceID,
			SourceKind: "terrain-dhcp",
			ObsType:    "dhcp-lease",
			ObservedAt: time.Now().UTC(),
			RawJSON:    `{"ip":"192.168.1.50"}`,
			Hostname:   "desktop-abc",
			Addresses: []AddressInput{
				{Address: "192.168.1.50/24", Family: "ipv4"},
			},
		},
	}

	result, err := p.Process(ctx, inputs)
	if err != nil {
		t.Fatal(err)
	}
	if result.InterfacesResolved != 1 {
		t.Fatalf("expected 1 resolved, got %d", result.InterfacesResolved)
	}
	if result.ObservationsStored != 1 {
		t.Fatalf("expected 1 observation, got %d", result.ObservationsStored)
	}
	if result.HostnamesUpdated != 1 {
		t.Fatalf("expected 1 hostname, got %d", result.HostnamesUpdated)
	}
	if result.AddressesUpdated != 1 {
		t.Fatalf("expected 1 address, got %d", result.AddressesUpdated)
	}

	// Verify the interface was created.
	iface, err := st.GetInterfaceByMAC(ctx, "aa:bb:cc:dd:ee:10")
	if err != nil {
		t.Fatal(err)
	}
	if iface.MAC != "aa:bb:cc:dd:ee:10" {
		t.Fatalf("unexpected MAC: %s", iface.MAC)
	}

	// Verify canonical hostname.
	hostname, err := st.GetCanonicalHostname(ctx, iface.ID)
	if err != nil {
		t.Fatal(err)
	}
	if hostname != "desktop-abc" {
		t.Fatalf("expected hostname desktop-abc, got %s", hostname)
	}
}

func TestPipeline_MultipleSameMAC(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	sourceID := createTestSource(t, st)

	p := New(st)
	now := time.Now().UTC()
	inputs := []ObservationInput{
		{
			MAC:        "AA:BB:CC:DD:EE:20",
			SourceID:   sourceID,
			SourceKind: "terrain-dhcp",
			ObsType:    "dhcp-lease",
			ObservedAt: now,
			Hostname:   "host-from-dhcp",
		},
		{
			MAC:        "AA:BB:CC:DD:EE:20",
			SourceID:   sourceID,
			SourceKind: "agent",
			ObsType:    "inventory",
			ObservedAt: now,
			Hostname:   "host-from-agent",
		},
	}

	result, err := p.Process(ctx, inputs)
	if err != nil {
		t.Fatal(err)
	}
	if result.InterfacesResolved != 2 {
		t.Fatalf("expected 2 resolved (same interface, 2 observations), got %d", result.InterfacesResolved)
	}
	if result.ObservationsStored != 2 {
		t.Fatalf("expected 2 observations stored, got %d", result.ObservationsStored)
	}

	// terrain-dhcp (priority 8) beats agent (priority 25).
	iface, err := st.GetInterfaceByMAC(ctx, "aa:bb:cc:dd:ee:20")
	if err != nil {
		t.Fatal(err)
	}
	hostname, err := st.GetCanonicalHostname(ctx, iface.ID)
	if err != nil {
		t.Fatal(err)
	}
	if hostname != "host-from-dhcp" {
		t.Fatalf("expected terrain-dhcp hostname to win, got %s", hostname)
	}
}

func TestPipeline_InvalidMAC_Skipped(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	sourceID := createTestSource(t, st)

	p := New(st)
	inputs := []ObservationInput{
		{
			MAC:        "", // Invalid — should be skipped.
			SourceID:   sourceID,
			SourceKind: "terrain-dhcp",
			ObsType:    "dhcp-lease",
			ObservedAt: time.Now().UTC(),
		},
		{
			MAC:        "AA:BB:CC:DD:EE:30",
			SourceID:   sourceID,
			SourceKind: "terrain-dhcp",
			ObsType:    "dhcp-lease",
			ObservedAt: time.Now().UTC(),
		},
	}

	result, err := p.Process(ctx, inputs)
	if err != nil {
		t.Fatal(err)
	}
	// First one failed (empty MAC), second succeeded.
	if result.InterfacesResolved != 1 {
		t.Fatalf("expected 1 resolved, got %d", result.InterfacesResolved)
	}
	if result.ObservationsStored != 1 {
		t.Fatalf("expected 1 observation, got %d", result.ObservationsStored)
	}
	if result.Failures != 1 {
		t.Fatalf("expected 1 failure, got %d", result.Failures)
	}
}

// createTestSource inserts a minimal source for FK requirements.
func createTestSource(t *testing.T, st *store.Store) string {
	t.Helper()
	ctx := context.Background()

	// Create a DEK key for source encryption.
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
