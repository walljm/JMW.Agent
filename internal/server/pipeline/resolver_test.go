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

	iface, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:01", nil)
	if err != nil {
		t.Fatal(err)
	}
	if iface.ID == "" {
		t.Fatal("expected non-empty interface ID")
	}
	// MAC should be normalized to lowercase.
	if iface.MAC != "aa:bb:cc:dd:ee:01" {
		t.Fatalf("expected MAC aa:bb:cc:dd:ee:01, got %s", iface.MAC)
	}
	// Hardware should have been created.
	if iface.HardwareID == "" {
		t.Fatal("expected non-empty hardware ID")
	}
}

func TestResolveInterface_ExistingMAC(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	// First resolution creates it.
	iface1, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:02", nil)
	if err != nil {
		t.Fatal(err)
	}

	// Second resolution with a fresh resolver should find it.
	r2 := NewResolver(st)
	iface2, err := r2.ResolveInterface(ctx, "AA:BB:CC:DD:EE:02", nil)
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

	// Resolve same MAC twice within one batch — should use cache.
	iface1, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:03", nil)
	if err != nil {
		t.Fatal(err)
	}
	iface2, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:03", nil)
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
	iface, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:04", hints)
	if err != nil {
		t.Fatal(err)
	}

	// Verify the hardware got the hint fields.
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

	_, err := r.ResolveInterface(ctx, "", nil)
	if err == nil {
		t.Fatal("expected error for empty MAC")
	}
}

func TestResolveInterface_MACNormalization(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()
	r := NewResolver(st)

	// Different formats of the same MAC should resolve to the same interface.
	iface1, err := r.ResolveInterface(ctx, "AA:BB:CC:DD:EE:05", nil)
	if err != nil {
		t.Fatal(err)
	}

	// Dash-separated.
	r2 := NewResolver(st)
	iface2, err := r2.ResolveInterface(ctx, "aa-bb-cc-dd-ee-05", nil)
	if err != nil {
		t.Fatal(err)
	}
	if iface1.ID != iface2.ID {
		t.Fatalf("expected same interface, got %s vs %s", iface1.ID, iface2.ID)
	}

	// Cisco dot notation.
	r3 := NewResolver(st)
	iface3, err := r3.ResolveInterface(ctx, "aabb.ccdd.ee05", nil)
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
		{"short", "short"}, // invalid length, returned as-is lowercased
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

	// Insert a valid agent (FK target for systems.agent_id).
	_, err := st.DB.ExecContext(ctx,
		`INSERT INTO agents (id, hostname, os, arch, status, registered_at)
		 VALUES (?, ?, ?, ?, ?, ?)`,
		"agent-123", "myhost", "linux", "amd64", "approved", "2025-01-01T00:00:00Z")
	if err != nil {
		t.Fatal(err)
	}

	// Create hardware + system with an agent_id.
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
