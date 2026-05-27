package store

import (
	"context"
	"testing"
	"time"
)

func TestListHardwareEmpty(t *testing.T) {
	st := testStore(t)
	items, err := st.ListHardware(context.Background())
	if err != nil {
		t.Fatalf("ListHardware: %v", err)
	}
	if len(items) != 0 {
		t.Fatalf("expected 0, got %d", len(items))
	}
}

func TestListHardwareWithEntity(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	hw := &Hardware{
		ID:           "hw-1",
		SystemVendor: "Dell",
		SystemModel:  "OptiPlex",
		CPUModel:     "i7-12700",
	}
	if _, err := st.UpsertHardware(ctx, hw); err != nil {
		t.Fatalf("UpsertHardware: %v", err)
	}

	ifc := &Interface{
		ID:         "ifc-1",
		HardwareID: "hw-1",
		MAC:        "aa:bb:cc:dd:ee:ff",
	}
	if _, err := st.UpsertInterface(ctx, ifc); err != nil {
		t.Fatalf("UpsertInterface: %v", err)
	}

	items, err := st.ListHardware(ctx)
	if err != nil {
		t.Fatalf("ListHardware: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1, got %d", len(items))
	}
	if items[0].SystemVendor != "Dell" {
		t.Fatalf("unexpected vendor: %q", items[0].SystemVendor)
	}
	if items[0].InterfaceCount != 1 {
		t.Fatalf("expected 1 interface, got %d", items[0].InterfaceCount)
	}
}

func TestGetHardwareDetail(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	hw := &Hardware{
		ID:           "hw-2",
		SystemVendor: "Lenovo",
		SystemModel:  "ThinkPad",
	}
	if _, err := st.UpsertHardware(ctx, hw); err != nil {
		t.Fatalf("UpsertHardware: %v", err)
	}

	ifc := &Interface{
		ID:         "ifc-2",
		HardwareID: "hw-2",
		MAC:        "11:22:33:44:55:66",
	}
	if _, err := st.UpsertInterface(ctx, ifc); err != nil {
		t.Fatalf("UpsertInterface: %v", err)
	}

	addr := &InterfaceAddress{
		InterfaceID: "ifc-2",
		Address:     "192.168.1.100/24",
		Family:      "ipv4",
		Scope:       "global",
	}
	if err := st.UpsertInterfaceAddress(ctx, addr); err != nil {
		t.Fatalf("UpsertInterfaceAddress: %v", err)
	}

	// Create a source for the observation FK.
	now := time.Now().UTC()
	_, _ = st.DB.ExecContext(ctx,
		`INSERT INTO sources (id, name, kind, enabled, created_at, updated_at) VALUES (?, ?, ?, 1, ?, ?)`,
		"src-1", "test", "agent", now.Format(time.RFC3339), now.Format(time.RFC3339))
	if _, err := st.InsertObservation(ctx, &Observation{
		InterfaceID: "ifc-2",
		SourceID:    "src-1",
		ObsType:     "arp",
		ObservedAt:  now,
	}); err != nil {
		t.Fatalf("InsertObservation: %v", err)
	}

	detail, err := st.GetHardwareDetail(ctx, "hw-2")
	if err != nil {
		t.Fatalf("GetHardwareDetail: %v", err)
	}
	if detail == nil {
		t.Fatal("expected non-nil detail")
	}
	if len(detail.Interfaces) != 1 {
		t.Fatalf("expected 1 interface, got %d", len(detail.Interfaces))
	}
	if len(detail.Interfaces[0].Addresses) != 1 {
		t.Fatalf("expected 1 address, got %d", len(detail.Interfaces[0].Addresses))
	}
	if detail.Interfaces[0].LastObserved == nil {
		t.Fatal("expected LastObserved to be set")
	}
}

func TestGetHardwareDetailNotFound(t *testing.T) {
	st := testStore(t)
	detail, err := st.GetHardwareDetail(context.Background(), "nonexistent")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if detail != nil {
		t.Fatal("expected nil for missing hardware")
	}
}
