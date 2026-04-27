package store

import (
	"context"
	"database/sql"
	"errors"
	"net"
	"sort"
	"strings"
	"time"
)

// Device is a network-discovered device. A single physical machine may have
// multiple Device rows (one per NIC); rows that share GroupID represent the
// same machine. GroupID == "" means "ungrouped, treat as its own group".
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
	AgentID        string // if this device is one of our agents, the agent.id
	ServicesJSON   string // JSON: {hostname, services:[], txt:{}} from latest mDNS
	GroupID        string // logical-device group; "" means ungrouped
}

// HostnameSourcePriority ranks a hostname source. Higher = more authoritative.
func HostnameSourcePriority(src string) int {
	switch src {
	case "agent":
		return 5 // self-reported by a running agent (inventory)
	case "mdns":
		return 4
	case "nbns":
		return 3
	case "rdns":
		return 2
	}
	return 0
}

// UpsertDevice inserts or updates a device by ID. Hostname overwrite is
// priority-aware: incoming (Hostname, HostnameSource) only replaces the stored
// value when its source priority is >= the stored source's priority.
func (s *Store) UpsertDevice(ctx context.Context, d *Device) error {
	now := time.Now().UTC().Format(time.RFC3339)
	if d.FirstSeenAt.IsZero() {
		d.FirstSeenAt, _ = time.Parse(time.RFC3339, now)
	}
	if d.LastSeenAt.IsZero() {
		d.LastSeenAt, _ = time.Parse(time.RFC3339, now)
	}
	incomingPri := HostnameSourcePriority(d.HostnameSource)
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO devices(id, mac, ip, hostname, hostname_source, vendor, first_seen_at, last_seen_at, seen_by_agent, kind, notes, agent_id, services_json, device_group_id)
		 VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)
		 ON CONFLICT(id) DO UPDATE SET
		   mac = excluded.mac,
		   ip = excluded.ip,
		   hostname = CASE
		     WHEN excluded.hostname = '' THEN devices.hostname
		     WHEN ? >= CASE devices.hostname_source
		       WHEN 'agent' THEN 5 WHEN 'mdns' THEN 4 WHEN 'nbns' THEN 3 WHEN 'rdns' THEN 2 ELSE 0 END
		       THEN excluded.hostname
		     ELSE devices.hostname END,
		   hostname_source = CASE
		     WHEN excluded.hostname = '' THEN devices.hostname_source
		     WHEN ? >= CASE devices.hostname_source
		       WHEN 'agent' THEN 5 WHEN 'mdns' THEN 4 WHEN 'nbns' THEN 3 WHEN 'rdns' THEN 2 ELSE 0 END
		       THEN excluded.hostname_source
		     ELSE devices.hostname_source END,
		   vendor = COALESCE(NULLIF(excluded.vendor,''), devices.vendor),
		   kind = COALESCE(NULLIF(excluded.kind,''), devices.kind),
		   last_seen_at = excluded.last_seen_at,
		   seen_by_agent = excluded.seen_by_agent,
		   agent_id = COALESCE(NULLIF(excluded.agent_id,''), devices.agent_id),
		   services_json = COALESCE(NULLIF(excluded.services_json,''), devices.services_json),
		   device_group_id = COALESCE(NULLIF(excluded.device_group_id,''), devices.device_group_id)`,
		d.ID, d.MAC, d.IP, d.Hostname, d.HostnameSource, d.Vendor,
		d.FirstSeenAt.Format(time.RFC3339),
		d.LastSeenAt.Format(time.RFC3339),
		d.SeenByAgent, d.Kind, d.Notes, d.AgentID, d.ServicesJSON, d.GroupID,
		incomingPri, incomingPri)
	return err
}

// GroupSourcePriority ranks a group ID by its source prefix. Higher = more
// authoritative. Mirrors HostnameSourcePriority so an mDNS group always wins
// over an NBNS-derived group, etc. Unknown / legacy prefixes return 0 so any
// sourced group can replace them.
func GroupSourcePriority(groupID string) int {
	switch {
	case strings.HasPrefix(groupID, "agent:"):
		return 5
	case strings.HasPrefix(groupID, "mdns:"):
		return 4
	case strings.HasPrefix(groupID, "nbns:"):
		return 3
	case strings.HasPrefix(groupID, "rdns:"):
		return 2
	}
	return 0
}

// SetDeviceGroup assigns a device to a group. Setting groupID to "" clears
// the assignment. Group assignment is priority-aware by source prefix
// (agent > mdns > nbns > rdns): an incoming groupID only replaces the stored
// one when its priority is >= the stored value's. This keeps a NAS that
// announces via mDNS from being un-grouped by a later rDNS-only sighting,
// and prevents an mDNS sighting from stealing a NIC away from its agent.
func (s *Store) SetDeviceGroup(ctx context.Context, deviceID, groupID string) error {
	if groupID == "" {
		_, err := s.DB.ExecContext(ctx,
			`UPDATE devices SET device_group_id = NULL WHERE id = ?`, deviceID)
		return err
	}
	incoming := GroupSourcePriority(groupID)
	_, err := s.DB.ExecContext(ctx,
		`UPDATE devices SET device_group_id = ?
		 WHERE id = ?
		   AND (device_group_id IS NULL
		        OR device_group_id = ?
		        OR ? >= CASE
		             WHEN device_group_id LIKE 'agent:%' THEN 5
		             WHEN device_group_id LIKE 'mdns:%'  THEN 4
		             WHEN device_group_id LIKE 'nbns:%'  THEN 3
		             WHEN device_group_id LIKE 'rdns:%'  THEN 2
		             ELSE 0 END)`,
		groupID, deviceID, groupID, incoming)
	return err
}

// AddHostname records (device, name, source) as an alias bucket. Bumps
// last_seen_at and count on collision.
func (s *Store) AddHostname(ctx context.Context, deviceID, name, source string, t time.Time) error {
	if name == "" || source == "" {
		return nil
	}
	if t.IsZero() {
		t = time.Now().UTC()
	}
	ts := t.Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO device_hostnames(device_id, name, source, first_seen_at, last_seen_at, count)
		 VALUES(?,?,?,?,?,1)
		 ON CONFLICT(device_id, name, source) DO UPDATE SET
		   last_seen_at = excluded.last_seen_at,
		   count        = device_hostnames.count + 1`,
		deviceID, name, source, ts, ts)
	return err
}

// HostnameAlias is one observed (name, source) bucket for a device.
type HostnameAlias struct {
	Name        string
	Source      string
	FirstSeenAt time.Time
	LastSeenAt  time.Time
	Count       int64
}

// ListHostnames returns all observed name/source aliases for a device,
// most-recently-seen first.
func (s *Store) ListHostnames(ctx context.Context, deviceID string) ([]*HostnameAlias, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT name, source, first_seen_at, last_seen_at, count
		 FROM device_hostnames WHERE device_id = ? ORDER BY last_seen_at DESC`, deviceID)
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

// AddSighting upserts a bucketed sighting row keyed on
// (device_id, seen_by_agent, ip, mac, method). On collision it bumps
// last_seen_at and increments count, leaving first_seen_at untouched.
func (s *Store) AddSighting(ctx context.Context, deviceID, agentID, ip, mac, method string, t time.Time) error {
	if t.IsZero() {
		t = time.Now().UTC()
	}
	ts := t.Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO device_sightings(device_id, seen_by_agent, ip, mac, method, first_seen_at, last_seen_at, count)
		 VALUES(?,?,?,?,?,?,?,1)
		 ON CONFLICT(device_id, seen_by_agent, ip, mac, method) DO UPDATE SET
		   last_seen_at = excluded.last_seen_at,
		   count        = device_sightings.count + 1`,
		deviceID, agentID, ip, mac, method, ts, ts)
	return err
}

// ListDevices returns all devices ordered by IP address (numeric ascending).
func (s *Store) ListDevices(ctx context.Context) ([]*Device, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, COALESCE(mac,''), COALESCE(ip,''), COALESCE(hostname,''),
		        COALESCE(hostname_source,''),
		        COALESCE(vendor,''), first_seen_at, last_seen_at,
		        COALESCE(seen_by_agent,''), COALESCE(kind,''), COALESCE(notes,''),
		        COALESCE(agent_id,''), COALESCE(services_json,''),
		        COALESCE(device_group_id,'')
		 FROM devices`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Device
	for rows.Next() {
		var d Device
		var fs, ls string
		if err := rows.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
			&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON, &d.GroupID); err != nil {
			return nil, err
		}
		d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		out = append(out, &d)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	sort.SliceStable(out, func(i, j int) bool {
		return compareIP(out[i].IP, out[j].IP) < 0
	})
	return out, nil
}

// compareIP orders IP address strings numerically. Empty/invalid IPs sort last.
// IPv4 sorts before IPv6.
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

// GetDevice returns a single device.
func (s *Store) GetDevice(ctx context.Context, id string) (*Device, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, COALESCE(mac,''), COALESCE(ip,''), COALESCE(hostname,''),
		        COALESCE(hostname_source,''),
		        COALESCE(vendor,''), first_seen_at, last_seen_at,
		        COALESCE(seen_by_agent,''), COALESCE(kind,''), COALESCE(notes,''),
		        COALESCE(agent_id,''), COALESCE(services_json,''),
		        COALESCE(device_group_id,'')
		 FROM devices WHERE id = ?`, id)
	var d Device
	var fs, ls string
	if err := row.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
		&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON, &d.GroupID); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
	d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
	return &d, nil
}

// ListGroupMembers returns all device rows that share the given group ID,
// ordered by IP. If groupID is empty, returns just the row whose id matches
// fallbackID (i.e., the device is its own group). Group members are returned
// in IP order so callers can pick a deterministic primary.
func (s *Store) ListGroupMembers(ctx context.Context, groupID, fallbackID string) ([]*Device, error) {
	if groupID == "" {
		d, err := s.GetDevice(ctx, fallbackID)
		if err != nil || d == nil {
			return nil, err
		}
		return []*Device{d}, nil
	}
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, COALESCE(mac,''), COALESCE(ip,''), COALESCE(hostname,''),
		        COALESCE(hostname_source,''),
		        COALESCE(vendor,''), first_seen_at, last_seen_at,
		        COALESCE(seen_by_agent,''), COALESCE(kind,''), COALESCE(notes,''),
		        COALESCE(agent_id,''), COALESCE(services_json,''),
		        COALESCE(device_group_id,'')
		 FROM devices WHERE device_group_id = ?`, groupID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Device
	for rows.Next() {
		var d Device
		var fs, ls string
		if err := rows.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
			&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON, &d.GroupID); err != nil {
			return nil, err
		}
		d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		out = append(out, &d)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	sort.SliceStable(out, func(i, j int) bool {
		return compareIP(out[i].IP, out[j].IP) < 0
	})
	return out, nil
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

// ListSightings returns up to `limit` bucketed sightings for a device,
// most-recently-seen first.
func (s *Store) ListSightings(ctx context.Context, deviceID string, limit int) ([]*Sighting, error) {
	if limit <= 0 {
		limit = 100
	}
	rows, err := s.DB.QueryContext(ctx,
		`SELECT seen_by_agent, ip, mac, method, first_seen_at, last_seen_at, count
		 FROM device_sightings WHERE device_id = ? ORDER BY last_seen_at DESC LIMIT ?`,
		deviceID, limit)
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
		sg.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		sg.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		out = append(out, &sg)
	}
	return out, rows.Err()
}

// ListSightingsForDevices returns bucketed sightings for any of the given
// device IDs, most-recently-seen first, capped at `limit` total rows. Used to
// merge sightings across all members of a device group.
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
	q := `SELECT seen_by_agent, ip, mac, method, first_seen_at, last_seen_at, count
	      FROM device_sightings WHERE device_id IN (` + strings.Join(placeholders, ",") + `)
	      ORDER BY last_seen_at DESC LIMIT ?`
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
		sg.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		sg.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		out = append(out, &sg)
	}
	return out, rows.Err()
}

// ListHostnamesForDevices returns the union of hostname aliases across the
// given device IDs, most-recently-seen first.
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
	q := `SELECT name, source, MIN(first_seen_at), MAX(last_seen_at), SUM(count)
	      FROM device_hostnames WHERE device_id IN (` + strings.Join(placeholders, ",") + `)
	      GROUP BY name, source ORDER BY MAX(last_seen_at) DESC`
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
