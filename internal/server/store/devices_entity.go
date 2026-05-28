package store

import (
	"context"
	"database/sql"
	"net"
	"sort"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/shared/oui"
)

// Device is a network-discovered device. Each row maps to an Interface in the
// entity model. Interfaces that share a HardwareID are the same physical
// machine (equivalent to the old GroupID concept).
type Device struct {
	ID             string
	MAC            string
	IP             string
	Hostname       string
	HostnameSource string // mdns | nbns | rdns | agent | ""
	Vendor         string
	FirstSeenAt    time.Time
	LastSeenAt     time.Time
	SeenByAgent    string
	Kind           string
	Notes          string
	AgentID        string    // if this device is one of our agents, the agent.id
	ServicesJSON   string    // JSON from latest mDNS observation
	GroupID        string    // hardware_id — groups NICs of same machine
	DHCPSeenAt     time.Time // most recent DHCP observation; zero if never
}

// Sighting is one bucketed observation: a unique tuple of
// (agent, ip, mac, method) for a device, with first/last timestamps and a
// repeat count.
type Sighting struct {
	SeenByAgent string
	IP          string
	MAC         string
	Method      string
	FirstSeenAt time.Time
	LastSeenAt  time.Time
	Count       int64
}

// HostnameAlias is one observed (name, source) bucket for a device.
type HostnameAlias struct {
	Name        string
	Source      string
	FirstSeenAt time.Time
	LastSeenAt  time.Time
	Count       int64
}

// DeviceStats summarizes entity counts for dashboard KPIs.
type DeviceStats struct {
	Total            int // distinct interface rows
	Groups           int // distinct hardware entities (logical machines)
	NewLast24h       int // interfaces first seen in last 24h
	UngroupedNoAgent int // always 0 in entity model (every interface has hardware)
}

// ListDevices returns all discovered interfaces as Device structs, ordered by IP.
func (s *Store) ListDevices(ctx context.Context) ([]*Device, error) {
	return s.listDevicesQuery(ctx, `
		SELECT i.id, i.mac, i.hardware_id, i.first_seen_at, i.last_seen_at, COALESCE(i.notes,'')
		FROM interfaces i
		ORDER BY i.first_seen_at DESC`)
}

// ListDevicesInCIDR returns devices that have an IPv4 address within the given
// CIDR range. Since SQLite has no native CIDR operator, we fetch interfaces
// that have at least one IPv4 address and filter in Go.
func (s *Store) ListDevicesInCIDR(ctx context.Context, cidr string) ([]*Device, error) {
	return s.ListDevicesInCIDRs(ctx, []string{cidr})
}

// ListDevicesInCIDRs returns devices whose IPv4 address falls within any of
// the given CIDR ranges.
func (s *Store) ListDevicesInCIDRs(ctx context.Context, cidrs []string) ([]*Device, error) {
	// Parse all CIDRs upfront.
	var nets []*net.IPNet
	for _, c := range cidrs {
		_, ipNet, err := net.ParseCIDR(c)
		if err != nil {
			continue // skip unparseable
		}
		nets = append(nets, ipNet)
	}
	if len(nets) == 0 {
		return s.ListDevices(ctx)
	}

	// Get all devices (hydrated with IPs).
	all, err := s.ListDevices(ctx)
	if err != nil {
		return nil, err
	}

	// Filter to those whose IP is in one of the CIDRs.
	var out []*Device
	for _, d := range all {
		if d.IP == "" {
			continue
		}
		ip := net.ParseIP(d.IP)
		if ip == nil {
			continue
		}
		for _, n := range nets {
			if n.Contains(ip) {
				out = append(out, d)
				break
			}
		}
	}
	return out, nil
}

// ListRecentDevices returns the most recently first-seen interfaces, newest first.
func (s *Store) ListRecentDevices(ctx context.Context, limit int) ([]*Device, error) {
	if limit <= 0 {
		limit = 5
	}
	rows, err := s.DB.QueryContext(ctx, `
		SELECT i.id, i.mac, i.hardware_id, i.first_seen_at, i.last_seen_at, COALESCE(i.notes,'')
		FROM interfaces i
		ORDER BY i.first_seen_at DESC
		LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	return s.scanDevicesFromInterfaces(ctx, rows)
}

// GetDevice returns a single device by interface ID or MAC.
func (s *Store) GetDevice(ctx context.Context, id string) (*Device, error) {
	row := s.DB.QueryRowContext(ctx, `
		SELECT i.id, i.mac, i.hardware_id, i.first_seen_at, i.last_seen_at, COALESCE(i.notes,'')
		FROM interfaces i
		WHERE i.id = ? OR LOWER(i.mac) = ?`, id, strings.ToLower(id))
	var d Device
	var fs, ls string
	if err := row.Scan(&d.ID, &d.MAC, &d.GroupID, &fs, &ls, &d.Notes); err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
	d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
	s.hydrateDevice(ctx, &d)
	return &d, nil
}

// PrimaryDeviceIDForAgent returns the interface ID (MAC) that best represents
// the agent on the merged entity detail page. It picks the most-recently-seen
// interface row belonging to the agent's hardware. Returns ("", nil) if the
// agent has no associated system or interfaces yet.
func (s *Store) PrimaryDeviceIDForAgent(ctx context.Context, agentID string) (string, error) {
	var hardwareID string
	err := s.DB.QueryRowContext(ctx,
		`SELECT hardware_id FROM systems WHERE agent_id = ? ORDER BY last_seen_at DESC LIMIT 1`,
		agentID).Scan(&hardwareID)
	if err == sql.ErrNoRows || hardwareID == "" {
		return "", nil
	}
	if err != nil {
		return "", err
	}
	var devID string
	err = s.DB.QueryRowContext(ctx,
		`SELECT id FROM interfaces WHERE hardware_id = ? ORDER BY last_seen_at DESC LIMIT 1`,
		hardwareID).Scan(&devID)
	if err == sql.ErrNoRows {
		return "", nil
	}
	if err != nil {
		return "", err
	}
	return devID, nil
}

// AgentPrimaryDeviceIDs returns a map of agentID → primary interface ID for
// every agent that has at least one system and interface row. Agents with no
// associated system or interface are omitted.
func (s *Store) AgentPrimaryDeviceIDs(ctx context.Context) (map[string]string, error) {
	rows, err := s.DB.QueryContext(ctx, `
		SELECT s.agent_id, i.id
		FROM systems s
		JOIN interfaces i ON i.hardware_id = s.hardware_id
		WHERE s.last_seen_at = (
			SELECT MAX(s2.last_seen_at) FROM systems s2 WHERE s2.agent_id = s.agent_id
		)
		AND i.last_seen_at = (
			SELECT MAX(i2.last_seen_at) FROM interfaces i2 WHERE i2.hardware_id = i.hardware_id
		)`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	m := make(map[string]string)
	for rows.Next() {
		var agentID, deviceID string
		if err := rows.Scan(&agentID, &deviceID); err != nil {
			return nil, err
		}
		m[agentID] = deviceID
	}
	return m, rows.Err()
}

// ListGroupMembers returns all interface rows that share the same hardware_id.
func (s *Store) ListGroupMembers(ctx context.Context, groupID, fallbackID string) ([]*Device, error) {
	if groupID == "" {
		d, err := s.GetDevice(ctx, fallbackID)
		if err != nil || d == nil {
			return nil, err
		}
		return []*Device{d}, nil
	}
	rows, err := s.DB.QueryContext(ctx, `
		SELECT i.id, i.mac, i.hardware_id, i.first_seen_at, i.last_seen_at, COALESCE(i.notes,'')
		FROM interfaces i
		WHERE i.hardware_id = ?
		ORDER BY i.first_seen_at ASC`, groupID)
	if err != nil {
		return nil, err
	}
	devs, err := s.scanDevicesFromInterfaces(ctx, rows)
	if err != nil {
		return nil, err
	}
	sort.SliceStable(devs, func(i, j int) bool {
		return compareIP(devs[i].IP, devs[j].IP) < 0
	})
	return devs, nil
}

// ListSightingsForDevices returns observations for the given interface IDs,
// aggregated as sightings.
func (s *Store) ListSightingsForDevices(ctx context.Context, deviceIDs []string, limit int) ([]*Sighting, error) {
	if len(deviceIDs) == 0 {
		return nil, nil
	}
	if limit <= 0 {
		limit = 200
	}
	placeholders := make([]string, len(deviceIDs))
	args := make([]any, 0, len(deviceIDs)+1)
	for i, id := range deviceIDs {
		placeholders[i] = "?"
		args = append(args, id)
	}
	args = append(args, limit)
	// Group by interface ID (not ia.address) to avoid fan-out when the same
	// interface has multiple address rows (e.g. "192.168.1.60" from discovery
	// and "192.168.1.60/24" from inventory — different strings, same device).
	q := `SELECT COALESCE(src.agent_id,''),
	             COALESCE((SELECT address FROM interface_addresses
	                       WHERE interface_id = i.id AND family = 'ipv4'
	                       ORDER BY last_seen_at DESC LIMIT 1), ''),
	             i.mac,
	             o.obs_type, MIN(o.observed_at), MAX(o.observed_at), COUNT(*)
	      FROM observations o
	      JOIN interfaces i ON i.id = o.interface_id
	      JOIN sources src ON src.id = o.source_id
	      WHERE o.interface_id IN (` + strings.Join(placeholders, ",") + `)
	      GROUP BY src.agent_id, i.id, i.mac, o.obs_type
	      ORDER BY MAX(o.observed_at) DESC
	      LIMIT ?`
	rows, err := s.DB.QueryContext(ctx, q, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Sighting
	for rows.Next() {
		var sg Sighting
		var fs, ls string
		if err := rows.Scan(&sg.SeenByAgent, &sg.IP, &sg.MAC, &sg.Method, &fs, &ls, &sg.Count); err != nil {
			return nil, err
		}
		// Strip CIDR suffix from address if present.
		if idx := strings.IndexByte(sg.IP, '/'); idx > 0 {
			sg.IP = sg.IP[:idx]
		}
		sg.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		sg.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		out = append(out, &sg)
	}
	return out, rows.Err()
}

// ListHostnamesForDevices returns hostname aliases for the given interface IDs.
func (s *Store) ListHostnamesForDevices(ctx context.Context, deviceIDs []string) ([]*HostnameAlias, error) {
	if len(deviceIDs) == 0 {
		return nil, nil
	}
	placeholders := make([]string, len(deviceIDs))
	args := make([]any, 0, len(deviceIDs))
	for i, id := range deviceIDs {
		placeholders[i] = "?"
		args = append(args, id)
	}
	q := `SELECT hostname, source_kind, first_seen_at, last_seen_at, 1
	      FROM hostname_aliases
	      WHERE interface_id IN (` + strings.Join(placeholders, ",") + `)
	      ORDER BY last_seen_at DESC`
	rows, err := s.DB.QueryContext(ctx, q, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*HostnameAlias
	for rows.Next() {
		var a HostnameAlias
		var fs, ls string
		if err := rows.Scan(&a.Name, &a.Source, &fs, &ls, &a.Count); err != nil {
			return nil, err
		}
		a.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		a.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		out = append(out, &a)
	}
	return out, rows.Err()
}

// DeviceStats returns aggregate counts from entity tables.
func (s *Store) DeviceStats(ctx context.Context) (DeviceStats, error) {
	var st DeviceStats
	cutoff := time.Now().UTC().Add(-24 * time.Hour).Format(time.RFC3339)
	err := s.DB.QueryRowContext(ctx,
		`SELECT
		   COUNT(*),
		   (SELECT COUNT(*) FROM hardware),
		   COALESCE(SUM(CASE WHEN first_seen_at >= ? THEN 1 ELSE 0 END), 0),
		   0
		 FROM interfaces`, cutoff).
		Scan(&st.Total, &st.Groups, &st.NewLast24h, &st.UngroupedNoAgent)
	return st, err
}

// UpdateDeviceNotes sets the notes field on an interface.
func (s *Store) UpdateDeviceNotes(ctx context.Context, id, notes string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE interfaces SET notes = ? WHERE id = ?`, notes, id)
	return err
}

// --- Internal helpers ---

func (s *Store) listDevicesQuery(ctx context.Context, query string, args ...any) ([]*Device, error) {
	rows, err := s.DB.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	devs, err := s.scanDevicesFromInterfaces(ctx, rows)
	if err != nil {
		return nil, err
	}
	sort.SliceStable(devs, func(i, j int) bool {
		return compareIP(devs[i].IP, devs[j].IP) < 0
	})
	return devs, nil
}

func (s *Store) scanDevicesFromInterfaces(ctx context.Context, rows *sql.Rows) ([]*Device, error) {
	defer rows.Close()
	var out []*Device
	for rows.Next() {
		var d Device
		var fs, ls string
		if err := rows.Scan(&d.ID, &d.MAC, &d.GroupID, &fs, &ls, &d.Notes); err != nil {
			return nil, err
		}
		d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		d.Vendor = oui.Lookup(d.MAC)
		out = append(out, &d)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	if len(out) == 0 {
		return out, nil
	}
	// Batch-hydrate after closing the rows cursor to avoid deadlocking the
	// single SQLite connection (SetMaxOpenConns=1).
	s.hydrateDevices(ctx, out)
	return out, nil
}

// hydrateDevices batch-fills IP, Hostname, HostnameSource, AgentID, SeenByAgent,
// ServicesJSON, and DHCPSeenAt for a slice of devices using one query per field
// instead of N queries per device.
func (s *Store) hydrateDevices(ctx context.Context, devs []*Device) {
	ids := make([]string, len(devs))
	byID := make(map[string]*Device, len(devs))
	for i, d := range devs {
		ids[i] = d.ID
		byID[d.ID] = d
	}
	ph := makePlaceholders(len(ids))
	args := toAnySlice(ids)

	// 1. Best IPv4 address per interface (most recently seen).
	addrRows, err := s.DB.QueryContext(ctx,
		`SELECT interface_id, address FROM interface_addresses
		 WHERE interface_id IN (`+ph+`) AND family = 'ipv4'
		 ORDER BY last_seen_at DESC`, args...)
	if err == nil {
		defer addrRows.Close()
		seen := make(map[string]bool, len(ids))
		for addrRows.Next() {
			var ifID, addr string
			if addrRows.Scan(&ifID, &addr) == nil && !seen[ifID] {
				seen[ifID] = true
				if idx := strings.IndexByte(addr, '/'); idx > 0 {
					addr = addr[:idx]
				}
				if d := byID[ifID]; d != nil {
					d.IP = addr
				}
			}
		}
		addrRows.Close()
	}

	// 2. Best hostname per interface (lowest priority = most authoritative).
	hnRows, err := s.DB.QueryContext(ctx,
		`SELECT interface_id, hostname, source_kind FROM hostname_aliases
		 WHERE interface_id IN (`+ph+`)
		 ORDER BY priority ASC, last_seen_at DESC`, args...)
	if err == nil {
		defer hnRows.Close()
		seen := make(map[string]bool, len(ids))
		for hnRows.Next() {
			var ifID, hostname, source string
			if hnRows.Scan(&ifID, &hostname, &source) == nil && !seen[ifID] {
				seen[ifID] = true
				if d := byID[ifID]; d != nil {
					d.Hostname = hostname
					d.HostnameSource = source
				}
			}
		}
		hnRows.Close()
	}

	// 3. Agent ID from systems table.
	agentRows, err := s.DB.QueryContext(ctx,
		`SELECT i.id, s.agent_id FROM systems s
		 JOIN interfaces i ON i.hardware_id = s.hardware_id
		 WHERE i.id IN (`+ph+`) AND s.agent_id IS NOT NULL AND s.agent_id != ''`, args...)
	if err == nil {
		defer agentRows.Close()
		for agentRows.Next() {
			var ifID, agentID string
			if agentRows.Scan(&ifID, &agentID) == nil {
				if d := byID[ifID]; d != nil {
					d.AgentID = agentID
				}
			}
		}
		agentRows.Close()
	}

	// 4. Most recent observing agent per interface.
	seenRows, err := s.DB.QueryContext(ctx,
		`SELECT o.interface_id, src.agent_id FROM observations o
		 JOIN sources src ON src.id = o.source_id
		 WHERE o.interface_id IN (`+ph+`) AND src.agent_id IS NOT NULL AND src.agent_id != ''
		 ORDER BY o.observed_at DESC`, args...)
	if err == nil {
		defer seenRows.Close()
		seen := make(map[string]bool, len(ids))
		for seenRows.Next() {
			var ifID, agentID string
			if seenRows.Scan(&ifID, &agentID) == nil && !seen[ifID] {
				seen[ifID] = true
				if d := byID[ifID]; d != nil {
					d.SeenByAgent = agentID
				}
			}
		}
		seenRows.Close()
	}

	// 5. Services JSON from most recent discovery observation.
	svcRows, err := s.DB.QueryContext(ctx,
		`SELECT interface_id, raw_json FROM observations
		 WHERE interface_id IN (`+ph+`) AND obs_type = 'discovery'
		 AND raw_json LIKE '%"services"%'
		 ORDER BY observed_at DESC`, args...)
	if err == nil {
		defer svcRows.Close()
		seen := make(map[string]bool, len(ids))
		for svcRows.Next() {
			var ifID, rawJSON string
			if svcRows.Scan(&ifID, &rawJSON) == nil && !seen[ifID] {
				seen[ifID] = true
				if d := byID[ifID]; d != nil {
					d.ServicesJSON = rawJSON
				}
			}
		}
		svcRows.Close()
	}

	// 6. DHCP last-seen time.
	dhcpRows, err := s.DB.QueryContext(ctx,
		`SELECT interface_id, MAX(observed_at) FROM observations
		 WHERE interface_id IN (`+ph+`) AND obs_type IN ('dhcp-lease','dhcp')
		 GROUP BY interface_id`, args...)
	if err == nil {
		defer dhcpRows.Close()
		for dhcpRows.Next() {
			var ifID, at string
			if dhcpRows.Scan(&ifID, &at) == nil {
				if d := byID[ifID]; d != nil {
					d.DHCPSeenAt, _ = time.Parse(time.RFC3339, at)
				}
			}
		}
		dhcpRows.Close()
	}

	// 7. Derived device kind from hardware.
	kindRows, err := s.DB.QueryContext(ctx,
		`SELECT i.id, COALESCE(h.device_kind, '') FROM interfaces i
		 JOIN hardware h ON h.id = i.hardware_id
		 WHERE i.id IN (`+ph+`)`, args...)
	if err == nil {
		defer kindRows.Close()
		for kindRows.Next() {
			var ifID, kind string
			if kindRows.Scan(&ifID, &kind) == nil {
				if d := byID[ifID]; d != nil && kind != "" {
					d.Kind = kind
				}
			}
		}
		kindRows.Close()
	}
}

// makePlaceholders returns "?,?,?" for n items.
func makePlaceholders(n int) string {
	if n == 0 {
		return ""
	}
	b := make([]byte, 0, n*2-1)
	for i := 0; i < n; i++ {
		if i > 0 {
			b = append(b, ',')
		}
		b = append(b, '?')
	}
	return string(b)
}

// toAnySlice converts []string to []any for query args.
func toAnySlice(ss []string) []any {
	out := make([]any, len(ss))
	for i, s := range ss {
		out[i] = s
	}
	return out
}

// hydrateDevice fills IP, Hostname, HostnameSource, Vendor, AgentID, and
// SeenByAgent from entity sub-tables. Used for single-device lookups (GetDevice).
func (s *Store) hydrateDevice(ctx context.Context, d *Device) {
	d.Vendor = oui.Lookup(d.MAC)
	s.hydrateDevices(ctx, []*Device{d})
}

// compareIP orders IP address strings numerically. Empty/invalid IPs sort last.
func compareIP(a, b string) int {
	ipa := net.ParseIP(a)
	ipb := net.ParseIP(b)
	switch {
	case ipa == nil && ipb == nil:
		return 0
	case ipa == nil:
		return 1
	case ipb == nil:
		return -1
	}
	a4, b4 := ipa.To4(), ipb.To4()
	switch {
	case a4 != nil && b4 == nil:
		return -1
	case a4 == nil && b4 != nil:
		return 1
	case a4 != nil && b4 != nil:
		for i := 0; i < 4; i++ {
			if a4[i] != b4[i] {
				if a4[i] < b4[i] {
					return -1
				}
				return 1
			}
		}
		return 0
	}
	a16, b16 := ipa.To16(), ipb.To16()
	for i := 0; i < 16; i++ {
		if a16[i] != b16[i] {
			if a16[i] < b16[i] {
				return -1
			}
			return 1
		}
	}
	return 0
}
