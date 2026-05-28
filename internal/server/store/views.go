package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

// HardwareListItem is a row in the entity-based hardware list view.
type HardwareListItem struct {
	ID             string
	SystemVendor   string
	SystemModel    string
	CPUModel       string
	CPUCores       int
	TotalMemMB     int64
	InterfaceCount int
	FirstSeenAt    time.Time
	LastSeenAt     time.Time
}

// ListHardware returns all hardware entities with basic stats.
func (s *Store) ListHardware(ctx context.Context) ([]*HardwareListItem, error) {
	rows, err := s.DB.QueryContext(ctx, `
		SELECT h.id, COALESCE(h.system_vendor,''), COALESCE(h.system_model,''),
		       COALESCE(h.cpu_model,''), COALESCE(h.cpu_cores,0), COALESCE(h.total_mem_bytes,0),
		       (SELECT COUNT(*) FROM interfaces WHERE hardware_id = h.id),
		       h.created_at,
		       COALESCE((SELECT MAX(o.observed_at) FROM observations o
		                 JOIN interfaces i ON i.id = o.interface_id
		                 WHERE i.hardware_id = h.id), h.created_at)
		FROM hardware h ORDER BY h.created_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*HardwareListItem
	for rows.Next() {
		var item HardwareListItem
		var memBytes int64
		var created, lastSeen string
		if err := rows.Scan(&item.ID, &item.SystemVendor, &item.SystemModel,
			&item.CPUModel, &item.CPUCores, &memBytes, &item.InterfaceCount,
			&created, &lastSeen); err != nil {
			return nil, err
		}
		item.TotalMemMB = memBytes / (1024 * 1024)
		item.FirstSeenAt, _ = time.Parse(time.RFC3339, created)
		item.LastSeenAt, _ = time.Parse(time.RFC3339, lastSeen)
		out = append(out, &item)
	}
	return out, rows.Err()
}

// HardwareDetail is the full view for a single hardware entity.
type HardwareDetail struct {
	Hardware
	System     *System
	Interfaces []InterfaceDetail
}

// InterfaceDetail is an interface with its addresses and recent observations.
type InterfaceDetail struct {
	Interface
	Addresses    []InterfaceAddress
	Hostnames    []EntityHostnameAlias
	LastObserved *time.Time
}

// GetHardwareDetail returns full detail for a hardware entity.
func (s *Store) GetHardwareDetail(ctx context.Context, id string) (*HardwareDetail, error) {
	hw, err := s.GetHardware(ctx, id)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	if hw == nil {
		return nil, nil
	}

	detail := &HardwareDetail{Hardware: *hw}

	// Look up system linked to this hardware.
	var sysID sql.NullString
	_ = s.DB.QueryRowContext(ctx,
		`SELECT id FROM systems WHERE hardware_id = ? LIMIT 1`, id).Scan(&sysID)
	if sysID.Valid {
		sys, err := s.GetSystem(ctx, sysID.String)
		if err == nil {
			detail.System = sys
		}
	}

	// List interfaces for this hardware. Materialize first to avoid nested
	// readers on a single-connection SQLite DB.
	rows, err := s.DB.QueryContext(ctx, `
		SELECT id, hardware_id, mac, first_seen_at, last_seen_at
		FROM interfaces WHERE hardware_id = ? ORDER BY first_seen_at ASC`, id)
	if err != nil {
		return detail, nil
	}
	var ifcs []Interface
	for rows.Next() {
		var ifc Interface
		var firstSeen, lastSeen string
		if err := rows.Scan(&ifc.ID, &ifc.HardwareID, &ifc.MAC,
			&firstSeen, &lastSeen); err != nil {
			continue
		}
		ifc.FirstSeenAt, _ = time.Parse(time.RFC3339, firstSeen)
		ifc.LastSeenAt, _ = time.Parse(time.RFC3339, lastSeen)
		ifcs = append(ifcs, ifc)
	}
	rows.Close()

	for _, ifc := range ifcs {
		id := InterfaceDetail{Interface: ifc}

		// Addresses.
		addrRows, err := s.DB.QueryContext(ctx, `
			SELECT id, interface_id, address, family, scope, first_seen_at, last_seen_at
			FROM interface_addresses WHERE interface_id = ?`, ifc.ID)
		if err == nil {
			for addrRows.Next() {
				var addr InterfaceAddress
				var fs, ls string
				if err := addrRows.Scan(&addr.ID, &addr.InterfaceID, &addr.Address,
					&addr.Family, &addr.Scope, &fs, &ls); err == nil {
					addr.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
					addr.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
					id.Addresses = append(id.Addresses, addr)
				}
			}
			addrRows.Close()
		}

		// Hostnames.
		hnRows, err := s.DB.QueryContext(ctx, `
			SELECT id, interface_id, hostname, source_kind, priority, first_seen_at, last_seen_at
			FROM hostname_aliases WHERE interface_id = ?`, ifc.ID)
		if err == nil {
			for hnRows.Next() {
				var hn EntityHostnameAlias
				var fs, ls string
				if err := hnRows.Scan(&hn.ID, &hn.InterfaceID, &hn.Hostname,
					&hn.SourceKind, &hn.Priority, &fs, &ls); err == nil {
					hn.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
					hn.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
					id.Hostnames = append(id.Hostnames, hn)
				}
			}
			hnRows.Close()
		}

		// Last observation time.
		var lastObs sql.NullString
		_ = s.DB.QueryRowContext(ctx,
			`SELECT MAX(observed_at) FROM observations WHERE interface_id = ?`, ifc.ID).Scan(&lastObs)
		if lastObs.Valid {
			t, _ := time.Parse(time.RFC3339, lastObs.String)
			id.LastObserved = &t
		}

		detail.Interfaces = append(detail.Interfaces, id)
	}

	return detail, nil
}

// SourceListItem is a row in the sources list view.
type SourceListItem struct {
	ID                    string
	Name                  string
	Kind                  string
	Enabled               bool
	AgentID               string
	PollIntervalSeconds   int
	LastPollSuccess       *time.Time
	ConsecutiveErrorCount int
	LastError             string
}

// ListSourcesForUI returns sources formatted for the dashboard.
func (s *Store) ListSourcesForUI(ctx context.Context) ([]*SourceListItem, error) {
	rows, err := s.DB.QueryContext(ctx, `
		SELECT id, name, kind, enabled, COALESCE(agent_id,''),
		       COALESCE(poll_interval_seconds,0), last_poll_success,
		       consecutive_error_count, COALESCE(last_poll_error,'')
		FROM sources ORDER BY kind, name`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*SourceListItem
	for rows.Next() {
		var item SourceListItem
		var enabled int
		var lastSuccess sql.NullString
		if err := rows.Scan(&item.ID, &item.Name, &item.Kind, &enabled, &item.AgentID,
			&item.PollIntervalSeconds, &lastSuccess,
			&item.ConsecutiveErrorCount, &item.LastError); err != nil {
			return nil, err
		}
		item.Enabled = enabled != 0
		if lastSuccess.Valid {
			t, _ := time.Parse(time.RFC3339, lastSuccess.String)
			item.LastPollSuccess = &t
		}
		out = append(out, &item)
	}
	return out, rows.Err()
}
