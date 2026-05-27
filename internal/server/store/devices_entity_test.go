package store

import (
	"context"
	"testing"
)

func TestListDevices_EntityModel(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Setup: hardware → interface → address + hostname.
	cores := 4
	hw := &Hardware{CPUCores: &cores, ChassisType: "server"}
	hwID, err := st.UpsertHardware(ctx, hw)
	if err != nil {
		t.Fatal(err)
	}

	iface := &Interface{HardwareID: hwID, MAC: "aa:bb:cc:dd:ee:01", IsUp: true}
	ifaceID, err := st.UpsertInterface(ctx, iface)
	if err != nil {
		t.Fatal(err)
	}

	addr := &InterfaceAddress{
		InterfaceID: ifaceID,
		Address:     "192.168.1.100/24",
		Family:      "ipv4",
		Scope:       "global",
	}
	if err := st.UpsertInterfaceAddress(ctx, addr); err != nil {
		t.Fatal(err)
	}

	if err := st.UpsertHostnameAlias(ctx, &EntityHostnameAlias{
		InterfaceID: ifaceID, Hostname: "my-server", SourceKind: "agent", Priority: 10,
	}); err != nil {
		t.Fatal(err)
	}

	// ListDevices should return this device with hydrated fields.
	devs, err := st.ListDevices(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(devs) != 1 {
		t.Fatalf("len = %d, want 1", len(devs))
	}
	d := devs[0]
	if d.ID != ifaceID {
		t.Fatalf("ID = %q, want %q", d.ID, ifaceID)
	}
	if d.MAC != "aa:bb:cc:dd:ee:01" {
		t.Fatalf("MAC = %q", d.MAC)
	}
	if d.IP != "192.168.1.100" {
		t.Fatalf("IP = %q, want 192.168.1.100 (CIDR stripped)", d.IP)
	}
	if d.Hostname != "my-server" {
		t.Fatalf("Hostname = %q, want my-server", d.Hostname)
	}
	if d.HostnameSource != "agent" {
		t.Fatalf("HostnameSource = %q, want agent", d.HostnameSource)
	}
	if d.GroupID != hwID {
		t.Fatalf("GroupID = %q, want %q", d.GroupID, hwID)
	}
}

func TestHydrateDevice_HostnamePriority(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "vm"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	iface := &Interface{HardwareID: hwID, MAC: "ff:ee:dd:cc:bb:aa", IsUp: true}
	ifaceID, _ := st.UpsertInterface(ctx, iface)

	// Insert hostnames with varying priorities.
	// Lower priority number = more authoritative; agent (10) should win over dhcp (50).
	aliases := []struct {
		name     string
		source   string
		priority int
	}{
		{"dhcp-name", "dhcp", 50},
		{"rdns-name", "rdns", 70},
		{"agent-name", "agent", 10},
		{"mdns-name", "mdns", 40},
	}
	for _, a := range aliases {
		if err := st.UpsertHostnameAlias(ctx, &EntityHostnameAlias{
			InterfaceID: ifaceID, Hostname: a.name, SourceKind: a.source, Priority: a.priority,
		}); err != nil {
			t.Fatal(err)
		}
	}

	// GetDevice hydrates and picks canonical hostname by priority ASC.
	d, err := st.GetDevice(ctx, ifaceID)
	if err != nil {
		t.Fatal(err)
	}
	if d == nil {
		t.Fatal("device not found")
	}
	if d.Hostname != "agent-name" {
		t.Fatalf("Hostname = %q, want agent-name (priority 10 should win)", d.Hostname)
	}
	if d.HostnameSource != "agent" {
		t.Fatalf("HostnameSource = %q, want agent", d.HostnameSource)
	}
}

func TestHostnameSourcePriority_Ordering(t *testing.T) {
	// Verify that the priority function keeps the expected ordering:
	// agent > docker > dhcp > mdns > nbns > ssdp > tls > rdns > http
	// Higher number = more authoritative.
	cases := []struct {
		higher, lower string
	}{
		{"agent", "docker"},
		{"docker", "dhcp"},
		{"dhcp", "mdns"},
		{"mdns", "nbns"},
		{"nbns", "ssdp"},
		{"ssdp", "tls"},
		{"tls", "rdns"},
		{"rdns", "http"},
	}
	for _, tc := range cases {
		h := HostnameSourcePriority(tc.higher)
		l := HostnameSourcePriority(tc.lower)
		if h <= l {
			t.Errorf("HostnameSourcePriority(%q)=%d should be > HostnameSourcePriority(%q)=%d",
				tc.higher, h, tc.lower, l)
		}
	}
	// Unknown source should return 0.
	if p := HostnameSourcePriority("unknown"); p != 0 {
		t.Errorf("unknown source priority = %d, want 0", p)
	}
}

func TestGetDevice_ByMAC(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "laptop"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	iface := &Interface{HardwareID: hwID, MAC: "11:22:33:44:55:66", IsUp: true}
	ifaceID, _ := st.UpsertInterface(ctx, iface)

	// Look up by MAC (case-insensitive).
	d, err := st.GetDevice(ctx, "11:22:33:44:55:66")
	if err != nil {
		t.Fatal(err)
	}
	if d == nil {
		t.Fatal("expected device by MAC lookup")
	}
	if d.ID != ifaceID {
		t.Fatalf("ID = %q, want %q", d.ID, ifaceID)
	}

	// Upper case should also work.
	d2, err := st.GetDevice(ctx, "11:22:33:44:55:66")
	if err != nil {
		t.Fatal(err)
	}
	if d2 == nil || d2.ID != ifaceID {
		t.Fatal("MAC lookup case-insensitive failed")
	}
}

func TestDeviceStats(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Empty DB.
	stats, err := st.DeviceStats(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if stats.Total != 0 || stats.Groups != 0 {
		t.Fatalf("empty stats: total=%d groups=%d", stats.Total, stats.Groups)
	}

	// Add two interfaces under one hardware.
	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "server"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	iface1 := &Interface{HardwareID: hwID, MAC: "a1:b2:c3:d4:e5:01", IsUp: true}
	st.UpsertInterface(ctx, iface1)
	iface2 := &Interface{HardwareID: hwID, MAC: "a1:b2:c3:d4:e5:02", IsUp: true}
	st.UpsertInterface(ctx, iface2)

	stats, err = st.DeviceStats(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if stats.Total != 2 {
		t.Fatalf("total = %d, want 2", stats.Total)
	}
	if stats.Groups != 1 {
		t.Fatalf("groups = %d, want 1", stats.Groups)
	}
}

func TestListGroupMembers(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "server"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	iface1 := &Interface{HardwareID: hwID, MAC: "01:02:03:04:05:01", IsUp: true}
	id1, _ := st.UpsertInterface(ctx, iface1)
	iface2 := &Interface{HardwareID: hwID, MAC: "01:02:03:04:05:02", IsUp: true}
	id2, _ := st.UpsertInterface(ctx, iface2)

	// Add addresses so they can be IP-sorted.
	st.UpsertInterfaceAddress(ctx, &InterfaceAddress{InterfaceID: id1, Address: "192.168.1.200/24", Family: "ipv4"})
	st.UpsertInterfaceAddress(ctx, &InterfaceAddress{InterfaceID: id2, Address: "192.168.1.100/24", Family: "ipv4"})

	members, err := st.ListGroupMembers(ctx, hwID, "")
	if err != nil {
		t.Fatal(err)
	}
	if len(members) != 2 {
		t.Fatalf("len = %d, want 2", len(members))
	}
	// Should be sorted by IP — .100 before .200.
	if members[0].IP != "192.168.1.100" {
		t.Fatalf("first member IP = %q, want 192.168.1.100", members[0].IP)
	}
	if members[1].IP != "192.168.1.200" {
		t.Fatalf("second member IP = %q, want 192.168.1.200", members[1].IP)
	}
}
