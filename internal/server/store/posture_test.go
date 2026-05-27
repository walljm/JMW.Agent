package store

import (
	"context"
	"testing"
)

func TestReplaceUpdateStatus(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Create hardware + system prerequisite.
	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "vm"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	sys := &System{HardwareID: hwID, Hostname: "posture-host", OSFamily: "linux"}
	sysID, _ := st.UpsertSystem(ctx, sys)

	status := &SystemUpdateStatus{
		SystemID:       sysID,
		Manager:        "apt",
		Pending:        5,
		Security:       2,
		RebootRequired: true,
		CheckedAt:      "2026-05-26T12:00:00Z",
	}
	if err := st.ReplaceUpdateStatus(ctx, status); err != nil {
		t.Fatal(err)
	}

	// Verify.
	var manager string
	var pending, security, reboot int
	err := st.DB.QueryRowContext(ctx,
		`SELECT manager, pending, security, reboot_required FROM system_update_status WHERE system_id = ?`,
		sysID).Scan(&manager, &pending, &security, &reboot)
	if err != nil {
		t.Fatal(err)
	}
	if manager != "apt" || pending != 5 || security != 2 || reboot != 1 {
		t.Fatalf("got manager=%q pending=%d security=%d reboot=%d", manager, pending, security, reboot)
	}

	// Update (should upsert, not duplicate).
	status.Pending = 3
	status.RebootRequired = false
	if err := st.ReplaceUpdateStatus(ctx, status); err != nil {
		t.Fatal(err)
	}
	err = st.DB.QueryRowContext(ctx,
		`SELECT pending, reboot_required FROM system_update_status WHERE system_id = ?`,
		sysID).Scan(&pending, &reboot)
	if err != nil {
		t.Fatal(err)
	}
	if pending != 3 || reboot != 0 {
		t.Fatalf("after update: pending=%d reboot=%d", pending, reboot)
	}
}

func TestReplacePendingUpdates(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "vm"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	sys := &System{HardwareID: hwID, Hostname: "pkg-host", OSFamily: "linux"}
	sysID, _ := st.UpsertSystem(ctx, sys)

	updates := []SystemPendingUpdate{
		{Name: "openssl", CurrentVersion: "3.0.2", NewVersion: "3.0.14", Security: true},
		{Name: "curl", CurrentVersion: "7.81", NewVersion: "7.88", Security: false},
	}
	if err := st.ReplacePendingUpdates(ctx, sysID, updates); err != nil {
		t.Fatal(err)
	}

	var count int
	st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM system_pending_updates WHERE system_id = ?`, sysID).Scan(&count)
	if count != 2 {
		t.Fatalf("expected 2, got %d", count)
	}

	// Replace with fewer — old ones should be deleted.
	if err := st.ReplacePendingUpdates(ctx, sysID, updates[:1]); err != nil {
		t.Fatal(err)
	}
	st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM system_pending_updates WHERE system_id = ?`, sysID).Scan(&count)
	if count != 1 {
		t.Fatalf("after replace: expected 1, got %d", count)
	}
}

func TestReplaceSecurityPosture(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "vm"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	sys := &System{HardwareID: hwID, Hostname: "sec-host", OSFamily: "linux"}
	sysID, _ := st.UpsertSystem(ctx, sys)

	fwEnabled := true
	tpmPresent := false
	posture := &SystemSecurityPosture{
		SystemID:             sysID,
		FirewallProvider:     "ufw",
		FirewallEnabled:      &fwEnabled,
		FirewallDefaultPolicy: "deny",
		TPMPresent:           &tpmPresent,
		SELinuxMode:          "enforcing",
	}
	if err := st.ReplaceSecurityPosture(ctx, posture); err != nil {
		t.Fatal(err)
	}

	var provider, selinux string
	var fwE, tpm int
	err := st.DB.QueryRowContext(ctx,
		`SELECT firewall_provider, firewall_enabled, tpm_present, selinux_mode
		 FROM system_security_posture WHERE system_id = ?`, sysID).Scan(&provider, &fwE, &tpm, &selinux)
	if err != nil {
		t.Fatal(err)
	}
	if provider != "ufw" || fwE != 1 || tpm != 0 || selinux != "enforcing" {
		t.Fatalf("posture: provider=%q fw=%d tpm=%d selinux=%q", provider, fwE, tpm, selinux)
	}
}

func TestReplaceAVProducts(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "desktop"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	sys := &System{HardwareID: hwID, Hostname: "av-host", OSFamily: "windows"}
	sysID, _ := st.UpsertSystem(ctx, sys)

	products := []SystemAVProduct{
		{Name: "Windows Defender", Enabled: true, RealtimeProtected: true, UpToDate: true},
		{Name: "CrowdStrike", Enabled: true, RealtimeProtected: true, UpToDate: true},
	}
	if err := st.ReplaceAVProducts(ctx, sysID, products); err != nil {
		t.Fatal(err)
	}

	var count int
	st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM system_av_products WHERE system_id = ?`, sysID).Scan(&count)
	if count != 2 {
		t.Fatalf("expected 2 AV products, got %d", count)
	}
}

func TestReplaceSystemServices(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	cores := 2
	hw := &Hardware{CPUCores: &cores, ChassisType: "server"}
	hwID, _ := st.UpsertHardware(ctx, hw)
	sys := &System{HardwareID: hwID, Hostname: "svc-host", OSFamily: "linux"}
	sysID, _ := st.UpsertSystem(ctx, sys)

	services := []SystemServiceStatus{
		{Name: "nginx", State: "running", StartMode: "auto"},
		{Name: "mysql", State: "failed", StartMode: "auto", ExitCode: 1},
	}
	if err := st.ReplaceSystemServices(ctx, sysID, services); err != nil {
		t.Fatal(err)
	}

	var count int
	st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM system_services WHERE system_id = ?`, sysID).Scan(&count)
	if count != 2 {
		t.Fatalf("expected 2 services, got %d", count)
	}

	// Replace with empty — should clear.
	if err := st.ReplaceSystemServices(ctx, sysID, nil); err != nil {
		t.Fatal(err)
	}
	st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM system_services WHERE system_id = ?`, sysID).Scan(&count)
	if count != 0 {
		t.Fatalf("expected 0 after clear, got %d", count)
	}
}
