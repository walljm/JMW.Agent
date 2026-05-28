package store

import (
	"context"
	"time"
)

// --- Posture types ---

// SystemUpdateStatus is the latest patch posture for a system.
type SystemUpdateStatus struct {
	SystemID       string
	Manager        string
	Pending        int
	Security       int
	RebootRequired bool
	CheckedAt      string
}

// SystemPendingUpdate is one pending package update.
type SystemPendingUpdate struct {
	SystemID       string
	Name           string
	CurrentVersion string
	NewVersion     string
	Source         string
	Security       bool
}

// SystemSecurityPosture is the latest security posture for a system.
type SystemSecurityPosture struct {
	SystemID              string
	FirewallProvider      string
	FirewallEnabled       *bool
	FirewallDefaultPolicy string
	TPMPresent            *bool
	TPMVersion            string
	SecureBoot            *bool
	SELinuxMode           string
	AppArmorMode          string
}

// SystemAVProduct is one antivirus/EDR registration.
type SystemAVProduct struct {
	SystemID          string
	Name              string
	Enabled           bool
	RealtimeProtected bool
	UpToDate          bool
	SignatureVersion  string
	SignatureAge      string
	LastScan          string
}

// SystemEncryptedVolume is one encrypted volume.
type SystemEncryptedVolume struct {
	SystemID   string
	Mountpoint string
	Device     string
	EncType    string
	EncStatus  string
}

// SystemServiceStatus is one OS service status.
type SystemServiceStatus struct {
	SystemID    string
	Name        string
	DisplayName string
	State       string
	StartMode   string
	SubState    string
	ExitCode    int
}

// --- Posture writers (bulk-replace per system) ---

// ReplaceUpdateStatus replaces the update status for a system.
func (s *Store) ReplaceUpdateStatus(ctx context.Context, status *SystemUpdateStatus) error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO system_update_status (system_id, manager, pending, security, reboot_required, checked_at, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(system_id) DO UPDATE SET
		    manager=excluded.manager, pending=excluded.pending, security=excluded.security,
		    reboot_required=excluded.reboot_required, checked_at=excluded.checked_at,
		    updated_at=excluded.updated_at`,
		status.SystemID, nullStr(status.Manager), status.Pending, status.Security,
		boolToInt(status.RebootRequired), nullStr(status.CheckedAt), now)
	return err
}

// ReplacePendingUpdates bulk-replaces the pending updates for a system.
func (s *Store) ReplacePendingUpdates(ctx context.Context, systemID string, updates []SystemPendingUpdate) error {
	now := time.Now().UTC().Format(time.RFC3339)
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	if _, err := tx.ExecContext(ctx,
		`DELETE FROM system_pending_updates WHERE system_id = ?`, systemID); err != nil {
		return err
	}
	for _, u := range updates {
		if _, err := tx.ExecContext(ctx,
			`INSERT INTO system_pending_updates (system_id, name, current_version, new_version, source, security, updated_at)
			 VALUES (?, ?, ?, ?, ?, ?, ?)`,
			systemID, u.Name, nullStr(u.CurrentVersion), nullStr(u.NewVersion),
			nullStr(u.Source), boolToInt(u.Security), now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ReplaceSecurityPosture replaces the security posture for a system.
func (s *Store) ReplaceSecurityPosture(ctx context.Context, p *SystemSecurityPosture) error {
	now := time.Now().UTC().Format(time.RFC3339)
	var fwEnabled, tpmPresent, secureBoot *int
	if p.FirewallEnabled != nil {
		v := boolToInt(*p.FirewallEnabled)
		fwEnabled = &v
	}
	if p.TPMPresent != nil {
		v := boolToInt(*p.TPMPresent)
		tpmPresent = &v
	}
	if p.SecureBoot != nil {
		v := boolToInt(*p.SecureBoot)
		secureBoot = &v
	}

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO system_security_posture (system_id, firewall_provider, firewall_enabled,
		    firewall_default_policy, tpm_present, tpm_version, secure_boot, selinux_mode, apparmor_mode, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(system_id) DO UPDATE SET
		    firewall_provider=excluded.firewall_provider, firewall_enabled=excluded.firewall_enabled,
		    firewall_default_policy=excluded.firewall_default_policy,
		    tpm_present=excluded.tpm_present, tpm_version=excluded.tpm_version,
		    secure_boot=excluded.secure_boot, selinux_mode=excluded.selinux_mode,
		    apparmor_mode=excluded.apparmor_mode, updated_at=excluded.updated_at`,
		p.SystemID, nullStr(p.FirewallProvider), fwEnabled,
		nullStr(p.FirewallDefaultPolicy), tpmPresent, nullStr(p.TPMVersion),
		secureBoot, nullStr(p.SELinuxMode), nullStr(p.AppArmorMode), now)
	return err
}

// ReplaceAVProducts bulk-replaces AV products for a system.
func (s *Store) ReplaceAVProducts(ctx context.Context, systemID string, products []SystemAVProduct) error {
	now := time.Now().UTC().Format(time.RFC3339)
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	if _, err := tx.ExecContext(ctx,
		`DELETE FROM system_av_products WHERE system_id = ?`, systemID); err != nil {
		return err
	}
	for _, p := range products {
		if _, err := tx.ExecContext(ctx,
			`INSERT INTO system_av_products (system_id, name, enabled, realtime_protected, up_to_date,
			    signature_version, signature_age, last_scan, updated_at)
			 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
			systemID, p.Name, boolToInt(p.Enabled), boolToInt(p.RealtimeProtected),
			boolToInt(p.UpToDate), nullStr(p.SignatureVersion), nullStr(p.SignatureAge),
			nullStr(p.LastScan), now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ReplaceEncryptedVolumes bulk-replaces encrypted volumes for a system.
func (s *Store) ReplaceEncryptedVolumes(ctx context.Context, systemID string, vols []SystemEncryptedVolume) error {
	now := time.Now().UTC().Format(time.RFC3339)
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	if _, err := tx.ExecContext(ctx,
		`DELETE FROM system_encrypted_volumes WHERE system_id = ?`, systemID); err != nil {
		return err
	}
	for _, v := range vols {
		if _, err := tx.ExecContext(ctx,
			`INSERT INTO system_encrypted_volumes (system_id, mountpoint, device, enc_type, enc_status, updated_at)
			 VALUES (?, ?, ?, ?, ?, ?)`,
			systemID, nullStr(v.Mountpoint), nullStr(v.Device),
			nullStr(v.EncType), nullStr(v.EncStatus), now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ReplaceSystemServices bulk-replaces OS service statuses for a system.
func (s *Store) ReplaceSystemServices(ctx context.Context, systemID string, services []SystemServiceStatus) error {
	now := time.Now().UTC().Format(time.RFC3339)
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	if _, err := tx.ExecContext(ctx,
		`DELETE FROM system_services WHERE system_id = ?`, systemID); err != nil {
		return err
	}
	for _, svc := range services {
		if _, err := tx.ExecContext(ctx,
			`INSERT INTO system_services (system_id, name, display_name, state, start_mode, sub_state, exit_code, updated_at)
			 VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
			systemID, svc.Name, nullStr(svc.DisplayName), nullStr(svc.State),
			nullStr(svc.StartMode), nullStr(svc.SubState), svc.ExitCode, now); err != nil {
			return err
		}
	}
	return tx.Commit()
}
