package store

import (
	"context"
	"path/filepath"
	"testing"
	"time"
)

func testStore(t *testing.T) *Store {
	t.Helper()
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "test.db")
	st, err := Open(context.Background(), dbPath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { st.Close() })
	return st
}

func TestEntityChain_Hardware_System_Interface_Address(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Create Hardware.
	cores := 4
	mem := int64(8_000_000_000)
	hw := &Hardware{
		SystemVendor:   "Dell",
		SystemModel:    "OptiPlex 7080",
		CPUModel:       "Intel i7-10700",
		CPUCores:       &cores,
		TotalMemBytes:  &mem,
		Virtualization: "none",
		ChassisType:    "desktop",
	}
	hwID, err := st.UpsertHardware(ctx, hw)
	if err != nil {
		t.Fatal(err)
	}
	if hwID == "" {
		t.Fatal("expected hardware ID")
	}

	// Read back.
	gotHW, err := st.GetHardware(ctx, hwID)
	if err != nil {
		t.Fatal(err)
	}
	if gotHW.SystemVendor != "Dell" {
		t.Fatalf("vendor = %q", gotHW.SystemVendor)
	}
	if *gotHW.CPUCores != 4 {
		t.Fatalf("cores = %d", *gotHW.CPUCores)
	}

	// Create System.
	sys := &System{
		HardwareID: hwID,
		Hostname:   "workstation-01",
		OSFamily:   "linux",
		OSDistro:   "Ubuntu",
		OSVersion:  "22.04",
		Kernel:     "5.15.0-76-generic",
	}
	sysID, err := st.UpsertSystem(ctx, sys)
	if err != nil {
		t.Fatal(err)
	}
	if sysID == "" {
		t.Fatal("expected system ID")
	}

	// Read back.
	gotSys, err := st.GetSystem(ctx, sysID)
	if err != nil {
		t.Fatal(err)
	}
	if gotSys.Hostname != "workstation-01" {
		t.Fatalf("hostname = %q", gotSys.Hostname)
	}
	if gotSys.HardwareID != hwID {
		t.Fatal("hardware_id mismatch")
	}

	// Create Interface.
	mtu := 1500
	iface := &Interface{
		SystemID:   sysID,
		HardwareID: hwID,
		MAC:        "aa:bb:cc:dd:ee:ff",
		Name:       "eth0",
		IfaceType:  "ethernet",
		MTU:        &mtu,
		IsUp:       true,
	}
	ifaceID, err := st.UpsertInterface(ctx, iface)
	if err != nil {
		t.Fatal(err)
	}
	if ifaceID == "" {
		t.Fatal("expected interface ID")
	}

	// Read back by MAC.
	gotIface, err := st.GetInterfaceByMAC(ctx, "aa:bb:cc:dd:ee:ff")
	if err != nil {
		t.Fatal(err)
	}
	if gotIface.Name != "eth0" {
		t.Fatalf("name = %q", gotIface.Name)
	}
	if gotIface.HardwareID != hwID {
		t.Fatal("hardware_id mismatch on interface")
	}

	// Add address.
	addr := &InterfaceAddress{
		InterfaceID: ifaceID,
		Address:     "192.168.1.50/24",
		Family:      "ipv4",
		Scope:       "global",
	}
	if err := st.UpsertInterfaceAddress(ctx, addr); err != nil {
		t.Fatal(err)
	}

	// Upsert again (should update last_seen_at, not create duplicate).
	if err := st.UpsertInterfaceAddress(ctx, addr); err != nil {
		t.Fatal(err)
	}

	// Verify only one address row exists.
	var count int
	err = st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM interface_addresses WHERE interface_id = ?`, ifaceID).Scan(&count)
	if err != nil {
		t.Fatal(err)
	}
	if count != 1 {
		t.Fatalf("expected 1 address, got %d", count)
	}
}

func TestUpsertInterface_Conflict(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, Virtualization: "kvm", ChassisType: "vm"}
	hwID, err := st.UpsertHardware(ctx, hw)
	if err != nil {
		t.Fatal(err)
	}

	// First insert.
	iface1 := &Interface{
		HardwareID: hwID,
		MAC:        "11:22:33:44:55:66",
		Name:       "ens3",
		IsUp:       true,
	}
	id1, err := st.UpsertInterface(ctx, iface1)
	if err != nil {
		t.Fatal(err)
	}

	// Second upsert with same MAC — should return same ID.
	iface2 := &Interface{
		HardwareID: hwID,
		MAC:        "11:22:33:44:55:66",
		Name:       "ens3-renamed",
		IsUp:       false,
	}
	id2, err := st.UpsertInterface(ctx, iface2)
	if err != nil {
		t.Fatal(err)
	}
	if id1 != id2 {
		t.Fatalf("expected same ID on conflict, got %q vs %q", id1, id2)
	}
}

func TestEnsureAgentSystemAdoptsReplacementIntoExistingAgent(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	oldAgentID := "local-jmw-agent"
	newAgentID := "0641dbe7-jmw-agent"
	registeredAt := time.Date(2026, 6, 3, 12, 0, 0, 0, time.UTC)
	for _, agent := range []*Agent{
		{ID: oldAgentID, Hostname: "local-jmw-agent", OS: "linux", Arch: "arm64", Version: "v1.1.2", Status: AgentStatusApproved, RegisteredAt: registeredAt},
		{ID: newAgentID, Hostname: "0641dbe7-jmw-agent", OS: "linux", Arch: "arm64", Version: "v2.2.5", Status: AgentStatusApproved, RegisteredAt: registeredAt.Add(time.Hour)},
	} {
		if err := st.CreateAgent(ctx, agent); err != nil {
			t.Fatal(err)
		}
	}
	if err := st.UpdateAgentNotes(ctx, oldAgentID, "Home Assistant (192.168.2.0/24 agent)"); err != nil {
		t.Fatal(err)
	}
	if err := st.SetTagsForTarget(ctx, TagTargetAgent, oldAgentID, []string{"iot", "server"}); err != nil {
		t.Fatal(err)
	}
	newSourceID, err := st.EnsureAgentSource(ctx, newAgentID, "0641dbe7-jmw-agent")
	if err != nil {
		t.Fatal(err)
	}
	collected := time.Now().UTC()
	if err := st.SetAgentInventory(ctx, newAgentID, `{"os":{"hostname":"homeassistant"}}`, "192.168.2.206", collected); err != nil {
		t.Fatal(err)
	}

	hwID, err := st.UpsertHardware(ctx, &Hardware{SystemVendor: "Nabu Casa", SystemModel: "Home Assistant Green"})
	if err != nil {
		t.Fatal(err)
	}
	oldSystemID, err := st.UpsertSystem(ctx, &System{
		HardwareID: hwID,
		AgentID:    oldAgentID,
		Hostname:   "homeassistant",
		OSFamily:   "linux",
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := st.UpsertInterface(ctx, &Interface{
		SystemID:   oldSystemID,
		HardwareID: hwID,
		MAC:        "aa:bb:cc:dd:ee:ff",
		Name:       "end0",
		IsUp:       true,
	}); err != nil {
		t.Fatal(err)
	}

	result, err := st.EnsureAgentSystem(ctx, newAgentID, "aa:bb:cc:dd:ee:ff", "homeassistant", "linux")
	if err != nil {
		t.Fatal(err)
	}
	if result.CanonicalAgentID != oldAgentID {
		t.Fatalf("canonical = %q, want %q", result.CanonicalAgentID, oldAgentID)
	}
	if len(result.SupersededAgentIDs) != 1 || result.SupersededAgentIDs[0] != newAgentID {
		t.Fatalf("superseded = %#v, want [%q]", result.SupersededAgentIDs, newAgentID)
	}

	oldAgent, err := st.GetAgent(ctx, oldAgentID)
	if err != nil {
		t.Fatal(err)
	}
	if oldAgent.Status != AgentStatusApproved {
		t.Fatalf("old status = %q, want %q", oldAgent.Status, AgentStatusApproved)
	}
	if oldAgent.Notes != "Home Assistant (192.168.2.0/24 agent)" {
		t.Fatalf("old notes = %q", oldAgent.Notes)
	}
	tags, err := st.ListTagsForTarget(ctx, TagTargetAgent, oldAgentID)
	if err != nil {
		t.Fatal(err)
	}
	if len(tags) != 2 || tags[0] != "iot" || tags[1] != "server" {
		t.Fatalf("old tags = %#v, want [iot server]", tags)
	}

	newAgent, err := st.GetAgent(ctx, newAgentID)
	if err != nil {
		t.Fatal(err)
	}
	if newAgent.Status != AgentStatusDeregistered {
		t.Fatalf("new status = %q, want %q", newAgent.Status, AgentStatusDeregistered)
	}

	oldSystem, err := st.GetSystem(ctx, oldSystemID)
	if err != nil {
		t.Fatal(err)
	}
	if oldSystem.AgentID != oldAgentID {
		t.Fatalf("old system agent_id = %q, want %q", oldSystem.AgentID, oldAgentID)
	}
	newSystem, err := st.GetSystemByAgent(ctx, newAgentID)
	if err == nil && newSystem != nil {
		t.Fatalf("new agent still has linked system: %#v", newSystem)
	}
	canonicalSystem, err := st.GetSystemByAgent(ctx, oldAgentID)
	if err != nil {
		t.Fatal(err)
	}
	if canonicalSystem.HardwareID != hwID {
		t.Fatalf("canonical system hardware_id = %q, want %q", canonicalSystem.HardwareID, hwID)
	}

	var sourceAgentID string
	if err := st.DB.QueryRowContext(ctx, `SELECT agent_id FROM sources WHERE id = ?`, newSourceID).Scan(&sourceAgentID); err != nil {
		t.Fatal(err)
	}
	if sourceAgentID != oldAgentID {
		t.Fatalf("source agent_id = %q, want %q", sourceAgentID, oldAgentID)
	}
	invJSON, invAt, err := st.GetAgentInventory(ctx, oldAgentID)
	if err != nil {
		t.Fatal(err)
	}
	if invJSON != `{"os":{"hostname":"homeassistant"}}` {
		t.Fatalf("canonical inventory = %q", invJSON)
	}
	if invAt.IsZero() {
		t.Fatal("canonical inventory timestamp was not copied")
	}
}

func TestEnsureAgentSystemExistingDuplicateKeepsOldAgentWhenOldReports(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	oldAgentID := "local-jmw-agent"
	newAgentID := "0641dbe7-jmw-agent"
	registeredAt := time.Date(2026, 6, 3, 12, 0, 0, 0, time.UTC)
	for _, agent := range []*Agent{
		{ID: oldAgentID, Hostname: "local-jmw-agent", OS: "linux", Arch: "arm64", Version: "v1.1.2", Status: AgentStatusApproved, RegisteredAt: registeredAt},
		{ID: newAgentID, Hostname: "0641dbe7-jmw-agent", OS: "linux", Arch: "arm64", Version: "v2.2.5", Status: AgentStatusApproved, RegisteredAt: registeredAt.Add(time.Hour)},
	} {
		if err := st.CreateAgent(ctx, agent); err != nil {
			t.Fatal(err)
		}
	}

	hwID, err := st.UpsertHardware(ctx, &Hardware{SystemVendor: "Nabu Casa", SystemModel: "Home Assistant Green"})
	if err != nil {
		t.Fatal(err)
	}
	oldSystemID, err := st.UpsertSystem(ctx, &System{
		HardwareID: hwID,
		AgentID:    oldAgentID,
		Hostname:   "homeassistant",
		OSFamily:   "linux",
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := st.UpsertSystem(ctx, &System{
		HardwareID: hwID,
		AgentID:    newAgentID,
		Hostname:   "homeassistant",
		OSFamily:   "linux",
	}); err != nil {
		t.Fatal(err)
	}
	if _, err := st.UpsertInterface(ctx, &Interface{
		SystemID:   oldSystemID,
		HardwareID: hwID,
		MAC:        "aa:bb:cc:dd:ee:ff",
		Name:       "end0",
		IsUp:       true,
	}); err != nil {
		t.Fatal(err)
	}

	result, err := st.EnsureAgentSystem(ctx, oldAgentID, "aa:bb:cc:dd:ee:ff", "homeassistant", "linux")
	if err != nil {
		t.Fatal(err)
	}
	if result.CanonicalAgentID != oldAgentID {
		t.Fatalf("canonical = %q, want %q", result.CanonicalAgentID, oldAgentID)
	}
	if len(result.SupersededAgentIDs) != 1 || result.SupersededAgentIDs[0] != newAgentID {
		t.Fatalf("superseded = %#v, want [%q]", result.SupersededAgentIDs, newAgentID)
	}

	newAgent, err := st.GetAgent(ctx, newAgentID)
	if err != nil {
		t.Fatal(err)
	}
	if newAgent.Status != AgentStatusDeregistered {
		t.Fatalf("new status = %q, want %q", newAgent.Status, AgentStatusDeregistered)
	}
}

func TestHostnameAlias_Priority(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "vm"}
	hwID, _ := st.UpsertHardware(ctx, hw)

	iface := &Interface{HardwareID: hwID, MAC: "aa:aa:aa:aa:aa:aa", IsUp: true}
	ifaceID, _ := st.UpsertInterface(ctx, iface)

	// Add DHCP hostname (lower priority = 50).
	_ = st.UpsertHostnameAlias(ctx, &EntityHostnameAlias{
		InterfaceID: ifaceID, Hostname: "dhcp-host", SourceKind: "dhcp", Priority: 50,
	})
	// Add agent hostname (higher priority = 10).
	_ = st.UpsertHostnameAlias(ctx, &EntityHostnameAlias{
		InterfaceID: ifaceID, Hostname: "agent-host", SourceKind: "agent", Priority: 10,
	})

	canonical, err := st.GetCanonicalHostname(ctx, ifaceID)
	if err != nil {
		t.Fatal(err)
	}
	if canonical != "agent-host" {
		t.Fatalf("canonical = %q, want agent-host", canonical)
	}
}

func TestDisk_Upsert_And_SMART(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "server"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	sys := &System{HardwareID: hwID, Hostname: "srv-01", OSFamily: "linux"}
	sysID, _ := st.UpsertSystem(ctx, sys)

	size := int64(500_000_000_000)
	disk := &Disk{
		SystemID:  sysID,
		Name:      "sda",
		Model:     "Samsung SSD 870",
		Serial:    "S1234",
		SizeBytes: &size,
		DiskType:  "ssd",
	}
	diskID, err := st.UpsertDisk(ctx, disk)
	if err != nil {
		t.Fatal(err)
	}
	if diskID == 0 {
		t.Fatal("expected non-zero disk ID")
	}

	// Upsert again — should get same ID.
	diskID2, err := st.UpsertDisk(ctx, disk)
	if err != nil {
		t.Fatal(err)
	}
	if diskID != diskID2 {
		t.Fatalf("disk ID changed: %d vs %d", diskID, diskID2)
	}

	// Write SMART attributes.
	temp := 35.0
	hours := int64(1000)
	smart := &DiskSMART{
		DiskID:        diskID,
		OverallHealth: "PASSED",
		TemperatureC:  &temp,
		PowerOnHours:  &hours,
	}
	if err := st.UpsertDiskSMART(ctx, smart); err != nil {
		t.Fatal(err)
	}

	// Verify.
	var health string
	var tempC float64
	err = st.DB.QueryRowContext(ctx,
		`SELECT overall_health, temperature_c FROM disk_smart_attributes WHERE disk_id = ?`, diskID).Scan(&health, &tempC)
	if err != nil {
		t.Fatal(err)
	}
	if health != "PASSED" || tempC != 35.0 {
		t.Fatalf("smart: health=%q temp=%f", health, tempC)
	}
}

func TestObservation_Insert(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Need a source first (from Phase 0 migration).
	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "vm"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	iface := &Interface{HardwareID: hwID, MAC: "bb:cc:dd:ee:ff:00", IsUp: true}
	ifaceID, _ := st.UpsertInterface(ctx, iface)

	// Create a source for the observation FK.
	src := &Source{
		Name:       "Test Source",
		Kind:       "agent",
		Enabled:    true,
		ConfigJSON: `{}`,
	}
	if err := st.CreateSource(ctx, src, nil); err != nil {
		t.Fatal(err)
	}

	obs := &Observation{
		InterfaceID: ifaceID,
		SourceID:    src.ID,
		ObservedAt:  time.Now().UTC(),
		ObsType:     "agent-heartbeat",
		RawJSON:     `{"test": true}`,
	}
	obsID, err := st.InsertObservation(ctx, obs)
	if err != nil {
		t.Fatal(err)
	}
	if obsID == 0 {
		t.Fatal("expected non-zero observation ID")
	}
}

func TestService_Upsert(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "vm"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	iface := &Interface{HardwareID: hwID, MAC: "cc:dd:ee:ff:00:11", IsUp: true}
	ifaceID, _ := st.UpsertInterface(ctx, iface)

	svc := &Service{
		InterfaceID: ifaceID,
		Proto:       "tcp",
		Port:        443,
		ServiceName: "https",
		Product:     "nginx",
		Version:     "1.25.3",
	}
	if err := st.UpsertService(ctx, svc); err != nil {
		t.Fatal(err)
	}

	// Upsert again with updated version.
	svc.Version = "1.25.4"
	if err := st.UpsertService(ctx, svc); err != nil {
		t.Fatal(err)
	}

	// Verify only one row.
	var count int
	err := st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM services WHERE interface_id = ?`, ifaceID).Scan(&count)
	if err != nil {
		t.Fatal(err)
	}
	if count != 1 {
		t.Fatalf("expected 1 service, got %d", count)
	}

	// Verify version updated.
	var ver string
	err = st.DB.QueryRowContext(ctx,
		`SELECT version FROM services WHERE interface_id = ? AND port = 443`, ifaceID).Scan(&ver)
	if err != nil {
		t.Fatal(err)
	}
	if ver != "1.25.4" {
		t.Fatalf("version = %q", ver)
	}
}
