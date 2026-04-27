package store

import (
	"context"
	"database/sql"
	"errors"
	"net"
	"sort"
	"time"
)

// Device is a network-discovered device.
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
		`INSERT INTO devices(id, mac, ip, hostname, hostname_source, vendor, first_seen_at, last_seen_at, seen_by_agent, kind, notes, agent_id, services_json)
		 VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
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
		   services_json = COALESCE(NULLIF(excluded.services_json,''), devices.services_json)`,
		d.ID, d.MAC, d.IP, d.Hostname, d.HostnameSource, d.Vendor,
		d.FirstSeenAt.Format(time.RFC3339),
		d.LastSeenAt.Format(time.RFC3339),
		d.SeenByAgent, d.Kind, d.Notes, d.AgentID, d.ServicesJSON,
		incomingPri, incomingPri)
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
		        COALESCE(agent_id,''), COALESCE(services_json,'')
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
			&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON); err != nil {
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
		        COALESCE(agent_id,''), COALESCE(services_json,'')
		 FROM devices WHERE id = ?`, id)
	var d Device
	var fs, ls string
	if err := row.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
		&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
	d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
	return &d, nil
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
