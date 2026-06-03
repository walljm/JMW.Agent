package pipeline

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/walljm/jmwagent/internal/server/store"
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

func TestResolveInterface_NewMAC(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	iface, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:01", nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if iface.ID == "" {
		t.Fatal("expected non-empty interface ID")
	}
	if iface.MAC != "aa:bb:cc:dd:ee:01" {
		t.Fatalf("expected MAC aa:bb:cc:dd:ee:01, got %s", iface.MAC)
	}
	if iface.HardwareID == "" {
		t.Fatal("expected non-empty hardware ID")
	}
}

func TestResolveInterface_ExistingMAC(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	iface1, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:02", nil, nil)
	if err != nil {
		t.Fatal(err)
	}

	r2 := NewResolver(st)
	iface2, err := r2.ResolveInterface(ctx, "AA:BB:CC:DD:EE:02", nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if iface2.ID != iface1.ID {
		t.Fatalf("expected same ID %s, got %s", iface1.ID, iface2.ID)
	}
}

func TestResolveInterface_BatchCache(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	iface1, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:03", nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	iface2, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:03", nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if iface1 != iface2 {
		t.Fatal("expected same pointer from cache")
	}
}

func TestResolveInterface_WithHints(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	hints := &InterfaceHints{
		SystemVendor:  "Dell",
		SystemModel:   "PowerEdge R740",
		InterfaceName: "eth0",
	}
	iface, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:04", hints, nil)
	if err != nil {
		t.Fatal(err)
	}

	hw, err := st.GetHardware(ctx, iface.HardwareID)
	if err != nil {
		t.Fatal(err)
	}
	if hw.SystemVendor != "Dell" {
		t.Fatalf("expected vendor Dell, got %s", hw.SystemVendor)
	}
	if hw.SystemModel != "PowerEdge R740" {
		t.Fatalf("expected model PowerEdge R740, got %s", hw.SystemModel)
	}
}

func TestResolveInterface_EmptyMAC(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	_, err := r.ResolveInterface(ctx, "", nil, nil)
	if err == nil {
		t.Fatal("expected error for empty MAC")
	}
}

func TestResolveInterface_MACNormalization(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	iface1, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:05", nil, nil)
	if err != nil {
		t.Fatal(err)
	}

	r2 := NewResolver(st)
	iface2, err := r2.ResolveInterface(ctx, "aa-bb-cc-dd-ee-05", nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if iface1.ID != iface2.ID {
		t.Fatalf("expected same interface, got %s vs %s", iface1.ID, iface2.ID)
	}

	r3 := NewResolver(st)
	iface3, err := r3.ResolveInterface(ctx, "aabb.ccdd.ee05", nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if iface1.ID != iface3.ID {
		t.Fatalf("expected same interface, got %s vs %s", iface1.ID, iface3.ID)
	}
}

func TestNormalizeMAC(t *testing.T) {
	cases := []struct {
		input    string
		expected string
	}{
		{"AA:BB:CC:DD:EE:FF", "aa:bb:cc:dd:ee:ff"},
		{"aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:ff"},
		{"AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff"},
		{"aabb.ccdd.eeff", "aa:bb:cc:dd:ee:ff"},
		{"AABBCCDDEEFF", "aa:bb:cc:dd:ee:ff"},
		{"short", "short"},
	}
	for _, tc := range cases {
		got := NormalizeMAC(tc.input)
		if got != tc.expected {
			t.Errorf("NormalizeMAC(%q) = %q, want %q", tc.input, got, tc.expected)
		}
	}
}

func TestResolveByAgent_NotFound(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	sys, err := r.ResolveByAgent(ctx, "nonexistent-agent")
	if err != nil {
		t.Fatal(err)
	}
	if sys != nil {
		t.Fatal("expected nil system for unknown agent")
	}
}

func TestResolveByAgent_Found(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	_, err := st.DB.ExecContext(ctx,
		`INSERT INTO agents (id, hostname, os, arch, status, registered_at)
		 VALUES (?, ?, ?, ?, ?, ?)`,
		"agent-123", "myhost", "linux", "amd64", "approved", "2025-01-01T00:00:00Z")
	if err != nil {
		t.Fatal(err)
	}

	hw := &store.Hardware{SystemVendor: "Test"}
	hwID, err := st.UpsertHardware(ctx, hw)
	if err != nil {
		t.Fatal(err)
	}
	sys := &store.System{
		HardwareID: hwID,
		AgentID:    "agent-123",
		Hostname:   "myhost",
		OSFamily:   "linux",
	}
	sysID, err := st.UpsertSystem(ctx, sys)
	if err != nil {
		t.Fatal(err)
	}

	r := NewResolver(st)
	found, err := r.ResolveByAgent(ctx, "agent-123")
	if err != nil {
		t.Fatal(err)
	}
	if found == nil {
		t.Fatal("expected system, got nil")
	}
	if found.ID != sysID {
		t.Fatalf("expected system ID %s, got %s", sysID, found.ID)
	}
}

// --- Fingerprint-based resolution tests ---

func TestResolveInterface_SerialMatchesSameDevice(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// First: discover a device via MAC only.
	r1 := NewResolver(st)
	iface1, err := r1.ResolveInterface(ctx, "AA:BB:CC:DD:EE:A0", nil, nil)
	if err != nil {
		t.Fatal(err)
	}

	// Second: agent reports a different MAC on the same machine, but with a
	// serial+vendor fingerprint. First, register the serial fingerprint for
	// the original hardware (simulating the agent having sent inventory for
	// the first MAC earlier).
	serial := Fingerprint{Kind: "serial:system", Value: "Dell\x00SVC1234", Source: "agent"}
	err = st.RegisterFingerprints(ctx, iface1.HardwareID, []store.FingerprintInput{
		{Kind: serial.Kind, Value: serial.Value, Source: serial.Source},
	})
	if err != nil {
		t.Fatal(err)
	}

	// Now resolve a new MAC with the same serial fingerprint.
	r2 := NewResolver(st)
	iface2, err := r2.ResolveInterface(ctx, "AA:BB:CC:DD:EE:A1", nil, []Fingerprint{serial})
	if err != nil {
		t.Fatal(err)
	}

	// Both interfaces should belong to the same hardware.
	if iface2.HardwareID != iface1.HardwareID {
		t.Fatalf("expected same hardware %s, got %s", iface1.HardwareID, iface2.HardwareID)
	}
	// But they should be different interfaces.
	if iface2.ID == iface1.ID {
		t.Fatal("expected different interface IDs for different MACs")
	}
}

func TestResolveInterface_NewFingerprintsRegistered(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Resolve with MAC only first.
	r := NewResolver(st)
	iface, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:B0", nil, nil)
	if err != nil {
		t.Fatal(err)
	}

	// Now resolve the same MAC again but with an additional serial fingerprint.
	r2 := NewResolver(st)
	serial := Fingerprint{Kind: "serial:system", Value: "HP\x00ABC789", Source: "agent"}
	_, err = r2.ResolveInterface(ctx, "AA:BB:CC:DD:EE:B0", nil, []Fingerprint{serial})
	if err != nil {
		t.Fatal(err)
	}

	// The serial fingerprint should now be registered for this hardware.
	hits, err := st.LookupFingerprints(ctx, []store.FingerprintInput{
		{Kind: "serial:system", Value: "HP\x00ABC789"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(hits) != 1 {
		t.Fatalf("expected 1 fingerprint hit, got %d", len(hits))
	}
	for hwID := range hits {
		if hwID != iface.HardwareID {
			t.Fatalf("expected hardware %s, got %s", iface.HardwareID, hwID)
		}
	}
}

func TestResolveInterface_MergeOnConflict(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Create two separate devices via different MACs.
	r1 := NewResolver(st)
	iface1, err := r1.ResolveInterface(ctx, "AA:BB:CC:DD:EE:C0", nil, nil)
	if err != nil {
		t.Fatal(err)
	}

	r2 := NewResolver(st)
	iface2, err := r2.ResolveInterface(ctx, "AA:BB:CC:DD:EE:C1", nil, nil)
	if err != nil {
		t.Fatal(err)
	}

	if iface1.HardwareID == iface2.HardwareID {
		t.Fatal("expected different hardware IDs before merge")
	}

	// Register a serial fingerprint on the first device.
	err = st.RegisterFingerprints(ctx, iface1.HardwareID, []store.FingerprintInput{
		{Kind: "serial:system", Value: "Lenovo\x00MERGE1", Source: "agent"},
	})
	if err != nil {
		t.Fatal(err)
	}

	// Now resolve a third MAC that carries the same serial fingerprint AND the
	// second device's MAC fingerprint — this forces a merge.
	r3 := NewResolver(st)
	iface3, err := r3.ResolveInterface(ctx, "AA:BB:CC:DD:EE:C1", nil, []Fingerprint{
		{Kind: "serial:system", Value: "Lenovo\x00MERGE1", Source: "agent"},
	})
	if err != nil {
		t.Fatal(err)
	}

	// After merge, all interfaces should belong to the same hardware.
	// Re-fetch iface1 since the hardware may have changed via merge.
	iface1After, err := st.GetInterfaceByMAC(ctx, "aa:bb:cc:dd:ee:c0")
	if err != nil {
		t.Fatal(err)
	}

	if iface1After.HardwareID != iface3.HardwareID {
		t.Fatalf("expected same hardware after merge, got %s vs %s",
			iface1After.HardwareID, iface3.HardwareID)
	}
}

func TestResolveInterface_DockerEngineFingerprint(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Resolve with MAC + docker engine ID.
	r1 := NewResolver(st)
	fps := []Fingerprint{
		{Kind: "docker_engine_id", Value: "ABCD-1234-EFGH-5678", Source: "agent"},
	}
	iface1, err := r1.ResolveInterface(ctx, "AA:BB:CC:DD:EE:D0", nil, fps)
	if err != nil {
		t.Fatal(err)
	}

	// Resolve a different MAC with the same docker engine ID.
	r2 := NewResolver(st)
	iface2, err := r2.ResolveInterface(ctx, "AA:BB:CC:DD:EE:D1", nil, fps)
	if err != nil {
		t.Fatal(err)
	}

	if iface2.HardwareID != iface1.HardwareID {
		t.Fatalf("expected same hardware via docker engine ID, got %s vs %s",
			iface1.HardwareID, iface2.HardwareID)
	}
}

func TestResolveInterface_MergePreservesMetadata(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Create device 1 via discovery (MAC only, no hardware metadata).
	r1 := NewResolver(st)
	iface1, err := r1.ResolveInterface(ctx, "AA:BB:CC:DD:EE:F0", nil, nil)
	if err != nil {
		t.Fatal(err)
	}

	// Create device 2 via agent inventory (different MAC, has hardware metadata).
	r2 := NewResolver(st)
	hints := &InterfaceHints{
		SystemVendor: "Dell",
		SystemModel:  "PowerEdge R740",
		SystemSerial: "SVC9999",
	}
	iface2, err := r2.ResolveInterface(ctx, "AA:BB:CC:DD:EE:F1", hints, nil)
	if err != nil {
		t.Fatal(err)
	}

	if iface1.HardwareID == iface2.HardwareID {
		t.Fatal("expected different hardware IDs before merge")
	}

	// Force the legacy whole-second timestamp tie that used to make survivor
	// selection depend on map iteration order.
	_, err = st.DB.ExecContext(ctx,
		`UPDATE hardware SET first_seen_at = ? WHERE id IN (?, ?)`,
		"2026-06-03T00:00:00Z", iface1.HardwareID, iface2.HardwareID)
	if err != nil {
		t.Fatal(err)
	}

	// Register a serial fingerprint on device 2.
	err = st.RegisterFingerprints(ctx, iface2.HardwareID, []store.FingerprintInput{
		{Kind: "serial:system", Value: "Dell\x00SVC9999", Source: "agent"},
	})
	if err != nil {
		t.Fatal(err)
	}

	// Now resolve device 1's MAC with the same serial — triggers merge.
	r3 := NewResolver(st)
	_, err = r3.ResolveInterface(ctx, "AA:BB:CC:DD:EE:F0", nil, []Fingerprint{
		{Kind: "serial:system", Value: "Dell\x00SVC9999", Source: "agent"},
	})
	if err != nil {
		t.Fatal(err)
	}

	// After merge, the surviving hardware should have the metadata from both.
	// Device 1 was inserted first, so it is the survivor when timestamps tie.
	hw, err := st.GetHardware(ctx, iface1.HardwareID)
	if err != nil {
		t.Fatal(err)
	}
	if hw.SystemVendor != "Dell" {
		t.Fatalf("expected vendor Dell after merge, got %q", hw.SystemVendor)
	}
	if hw.SystemModel != "PowerEdge R740" {
		t.Fatalf("expected model PowerEdge R740 after merge, got %q", hw.SystemModel)
	}
	if hw.SystemSerial != "SVC9999" {
		t.Fatalf("expected serial SVC9999 after merge, got %q", hw.SystemSerial)
	}

	// The loser hardware should be deleted.
	_, err = st.GetHardware(ctx, iface2.HardwareID)
	if err == nil {
		t.Fatal("expected loser hardware to be deleted")
	}
}

func TestResolveInterface_EmptyFingerprintValueIgnored(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	r := NewResolver(st)
	fps := []Fingerprint{
		{Kind: "serial:system", Value: "", Source: "agent"},
	}
	iface, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:E0", nil, fps)
	if err != nil {
		t.Fatal(err)
	}
	if iface.HardwareID == "" {
		t.Fatal("expected non-empty hardware ID")
	}
}
