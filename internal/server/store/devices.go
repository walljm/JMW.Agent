package store

import (
	"context"
	"database/sql"
	"errors"
	"net"
	"sort"
	"strconv"
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
	AgentID        string    // if this device is one of our agents, the agent.id
	ServicesJSON   string    // JSON: {hostname, services:[], txt:{}} from latest mDNS
	GroupID        string    // logical-device group; "" means ungrouped
	StaticLease    bool      // true when the DHCP server has this MAC pinned (Reserved/Static lease)
	DHCPSeenAt     time.Time // most recent time a DHCP lease for this MAC was observed; zero if never
}

// HostnameSourcePriority ranks a hostname source. Higher = more authoritative.
// Keep in sync with the agent's discover.sourcePriority — both sides must
// agree on ordering for priority-aware updates to behave consistently.
func HostnameSourcePriority(src string) int {
	switch src {
	case "agent":
		return 100 // self-reported by a running agent (inventory)
	case "docker":
		return 95 // local container runtime is authoritative for its containers
	case "dhcp":
		return 93 // self-announced by client to its DHCP server
	case "mdns":
		return 90
	case "llmnr":
		return 85
	case "smb":
		return 80
	case "nbns":
		return 70
	case "ldap":
		return 68 // dnsHostName from rootDSE — solid for DCs
	case "snmp":
		return 65
	case "eureka":
		return 62 // Google Cast/Nest device-name
	case "ipp":
		return 60 // printer-name from IPP
	case "roku":
		return 58
	case "airplay":
		return 55
	case "wsd":
		return 52
	case "ssdp":
		return 50
	case "garp":
		return 45 // ARP entries scraped from gateway via SNMP
	case "tls":
		return 40
	case "rdns":
		return 30
	case "http":
		return 20
	case "ssh":
		return 15
	}
	return 0
}

// hostnameSourceSQLCase returns a SQL CASE expression that maps the column
// name (assumed to be a hostname source) to its numeric priority. Callers
// embed this directly in SQL strings so the priority table lives in one
// place (HostnameSourcePriority above) and is mirrored into SQL on the fly
// instead of being hand-maintained in every query.
func hostnameSourceSQLCase(col string) string {
	knownSources := []string{"agent", "docker", "dhcp", "mdns", "llmnr", "smb", "nbns", "ldap", "snmp", "eureka", "ipp", "roku", "airplay", "wsd", "ssdp", "garp", "tls", "rdns", "http", "ssh"}
	var b strings.Builder
	b.WriteString("CASE ")
	b.WriteString(col)
	for _, s := range knownSources {
		b.WriteString(" WHEN '")
		b.WriteString(s)
		b.WriteString("' THEN ")
		b.WriteString(strconv.Itoa(HostnameSourcePriority(s)))
	}
	b.WriteString(" ELSE 0 END")
	return b.String()
}

// groupSourceSQLCase returns a SQL CASE expression that maps a group_id
// column (e.g. 'agent:abc') to the priority of its source prefix.
func groupSourceSQLCase(col string) string {
	knownSources := []string{"agent", "docker", "dhcp", "mdns", "llmnr", "smb", "nbns", "ldap", "snmp", "eureka", "ipp", "roku", "airplay", "wsd", "ssdp", "garp", "tls", "rdns", "http", "ssh"}
	var b strings.Builder
	b.WriteString("CASE")
	for _, s := range knownSources {
		b.WriteString(" WHEN ")
		b.WriteString(col)
		b.WriteString(" LIKE '")
		b.WriteString(s)
		b.WriteString(":%' THEN ")
		b.WriteString(strconv.Itoa(HostnameSourcePriority(s)))
	}
	b.WriteString(" ELSE 0 END")
	return b.String()
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
	storedCase := hostnameSourceSQLCase("devices.hostname_source")
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO devices(id, mac, ip, hostname, hostname_source, vendor, first_seen_at, last_seen_at, seen_by_agent, kind, notes, agent_id, services_json, device_group_id)
		 VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)
		 ON CONFLICT(id) DO UPDATE SET
		   mac = excluded.mac,
		   ip = excluded.ip,
		   hostname = CASE
		     WHEN excluded.hostname = '' THEN devices.hostname
		     WHEN ? >= `+storedCase+` THEN excluded.hostname
		     ELSE devices.hostname END,
		   hostname_source = CASE
		     WHEN excluded.hostname = '' THEN devices.hostname_source
		     WHEN ? >= `+storedCase+` THEN excluded.hostname_source
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
	if i := strings.IndexByte(groupID, ':'); i > 0 {
		return HostnameSourcePriority(groupID[:i])
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
	storedCase := groupSourceSQLCase("device_group_id")
	_, err := s.DB.ExecContext(ctx,
		`UPDATE devices SET device_group_id = ?
		 WHERE id = ?
		   AND (device_group_id IS NULL
		        OR device_group_id = ?
		        OR ? >= `+storedCase+`)`,
		groupID, deviceID, groupID, incoming)
	return err
}

// BackfillAgentGroup ensures every device row associated with the given
// agent is assigned to that agent's canonical group ("agent:<agentID>").
// It catches two classes of stale rows that the per-NIC inventory upsert
// loop would otherwise miss:
//
//  1. Rows already attributed to this agent (devices.agent_id = agentID)
//     whose group was never set — typically transient virtual NICs
//     (utun*, awdl0, llw0, …) that the agent reported once but no longer
//     reports.
//  2. Rows for the agent's own hostname that were originally created by
//     another agent's discovery (kind/source = mdns/nbns/rdns) and so
//     never touched the inventory upsert path. macOS per-SSID MAC
//     randomization makes this common: another agent ARPs the macbook's
//     current Wi-Fi MAC and tags it `mdns:<hostname>`, while the macbook
//     agent itself reports a different (or rotated) MAC in inventory.
//
// Group-overwrite respects source priority (agent > mdns > nbns > rdns):
// existing higher-priority groups are never clobbered.
func (s *Store) BackfillAgentGroup(ctx context.Context, agentID, hostname string) error {
	if agentID == "" {
		return nil
	}
	target := "agent:" + agentID
	// (1) Rows tagged with this agent_id and no group.
	if _, err := s.DB.ExecContext(ctx,
		`UPDATE devices
		    SET device_group_id = ?
		  WHERE agent_id = ?
		    AND (device_group_id IS NULL OR device_group_id = '')`,
		target, agentID); err != nil {
		return err
	}
	// (2) Rows whose hostname matches the agent's hostname and whose
	// current group has lower priority than 'agent:' (5). We compare on
	// normalized hostname (lowercased, trailing '.' and '.local' stripped)
	// so "Walljm-Macbook-2.local" and "walljm-macbook-2" match.
	if hostname == "" {
		return nil
	}
	norm := strings.TrimSuffix(strings.ToLower(strings.TrimSpace(hostname)), ".")
	norm = strings.TrimSuffix(norm, ".local")
	if norm == "" {
		return nil
	}
	_, err := s.DB.ExecContext(ctx,
		`UPDATE devices
		    SET device_group_id = ?,
		        agent_id = COALESCE(NULLIF(agent_id,''), ?)
		  WHERE LOWER(REPLACE(REPLACE(IFNULL(hostname,''), '.local', ''), '.', '')) =
		        REPLACE(?, '.', '')
		    AND (device_group_id IS NULL
		         OR device_group_id = ''
		         OR device_group_id = ?
		         OR (device_group_id NOT LIKE 'agent:%'))`,
		target, agentID, norm, target)
	return err
}

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
		        COALESCE(device_group_id,''),
		        COALESCE(static_lease,0), COALESCE(dhcp_seen_at,'')
		 FROM devices`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Device
	for rows.Next() {
		var d Device
		var fs, ls, ds string
		var staticLease int
		if err := rows.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
			&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON, &d.GroupID,
			&staticLease, &ds); err != nil {
			return nil, err
		}
		d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		d.StaticLease = staticLease != 0
		if ds != "" {
			d.DHCPSeenAt, _ = time.Parse(time.RFC3339, ds)
		}
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

// ListRecentDevices returns the most recently first-seen devices, newest
// first, limited. Useful for "newly discovered" dashboard panels.
func (s *Store) ListRecentDevices(ctx context.Context, limit int) ([]*Device, error) {
	if limit <= 0 {
		limit = 5
	}
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, COALESCE(mac,''), COALESCE(ip,''), COALESCE(hostname,''),
		        COALESCE(hostname_source,''),
		        COALESCE(vendor,''), first_seen_at, last_seen_at,
		        COALESCE(seen_by_agent,''), COALESCE(kind,''), COALESCE(notes,''),
		        COALESCE(agent_id,''), COALESCE(services_json,''),
		        COALESCE(device_group_id,''),
		        COALESCE(static_lease,0), COALESCE(dhcp_seen_at,'')
		 FROM devices
		 ORDER BY first_seen_at DESC, id ASC
		 LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Device
	for rows.Next() {
		var d Device
		var fs, ls, ds string
		var staticLease int
		if err := rows.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
			&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON, &d.GroupID,
			&staticLease, &ds); err != nil {
			return nil, err
		}
		d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		d.StaticLease = staticLease != 0
		if ds != "" {
			d.DHCPSeenAt, _ = time.Parse(time.RFC3339, ds)
		}
		out = append(out, &d)
	}
	return out, rows.Err()
}

// DeviceStats summarizes the devices table for dashboard KPIs.
type DeviceStats struct {
	Total            int // distinct device rows
	Groups           int // distinct logical machines (grouped + each ungrouped row)
	NewLast24h       int // rows whose first_seen_at is within the last 24h
	UngroupedNoAgent int // rows with no group AND not attributed to an agent
}

// DeviceStats returns aggregate counts for the devices table. All counts
// come from a single round trip.
func (s *Store) DeviceStats(ctx context.Context) (DeviceStats, error) {
	var st DeviceStats
	cutoff := time.Now().UTC().Add(-24 * time.Hour).Format(time.RFC3339)
	err := s.DB.QueryRowContext(ctx,
		`SELECT
		   COUNT(*),
		   COUNT(DISTINCT COALESCE(NULLIF(device_group_id,''), '_ungrouped:' || id)),
		   COALESCE(SUM(CASE WHEN first_seen_at >= ? THEN 1 ELSE 0 END), 0),
		   COALESCE(SUM(CASE WHEN (device_group_id IS NULL OR device_group_id = '')
		                      AND (agent_id IS NULL OR agent_id = '') THEN 1 ELSE 0 END), 0)
		 FROM devices`, cutoff).
		Scan(&st.Total, &st.Groups, &st.NewLast24h, &st.UngroupedNoAgent)
	return st, err
}

// GetDevice returns a single device.
func (s *Store) GetDevice(ctx context.Context, id string) (*Device, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, COALESCE(mac,''), COALESCE(ip,''), COALESCE(hostname,''),
		        COALESCE(hostname_source,''),
		        COALESCE(vendor,''), first_seen_at, last_seen_at,
		        COALESCE(seen_by_agent,''), COALESCE(kind,''), COALESCE(notes,''),
		        COALESCE(agent_id,''), COALESCE(services_json,''),
		        COALESCE(device_group_id,''),
		        COALESCE(static_lease,0), COALESCE(dhcp_seen_at,'')
		 FROM devices WHERE id = ?`, id)
	var d Device
	var fs, ls, ds string
	var staticLease int
	if err := row.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
		&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON, &d.GroupID,
		&staticLease, &ds); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
	d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
	d.StaticLease = staticLease != 0
	if ds != "" {
		d.DHCPSeenAt, _ = time.Parse(time.RFC3339, ds)
	}
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
		        COALESCE(device_group_id,''),
		        COALESCE(static_lease,0), COALESCE(dhcp_seen_at,'')
		 FROM devices WHERE device_group_id = ?`, groupID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Device
	for rows.Next() {
		var d Device
		var fs, ls, ds string
		var staticLease int
		if err := rows.Scan(&d.ID, &d.MAC, &d.IP, &d.Hostname, &d.HostnameSource, &d.Vendor,
			&fs, &ls, &d.SeenByAgent, &d.Kind, &d.Notes, &d.AgentID, &d.ServicesJSON, &d.GroupID,
			&staticLease, &ds); err != nil {
			return nil, err
		}
		d.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		d.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		d.StaticLease = staticLease != 0
		if ds != "" {
			d.DHCPSeenAt, _ = time.Parse(time.RFC3339, ds)
		}
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

// DHCPLeaseEntry is one lease observation supplied by the terrain poller
// (or any other DHCP-aware source). MAC is the canonical key; Hostname is
// the name the client announced; Static marks Reserved/Static reservations.
type DHCPLeaseEntry struct {
	MAC      string
	IP       string
	Hostname string
	Static   bool
	Expires  time.Time
}

// UpsertFromDHCPLease overlays a DHCP lease onto the devices table:
//
//   - Inserts a skeletal row if no device exists for the lease's MAC.
//   - Refreshes the row's IP and dhcp_seen_at on every call.
//   - Updates static_lease to reflect the lease's pinned/reservation state.
//   - Performs a priority-aware hostname update via UpsertDevice
//     (hostname source = "dhcp", priority 93), so it never clobbers a
//     higher-priority name (agent/docker self-report).
//   - Records the announced name in device_hostnames so the alias table
//     surfaces it on the detail page.
//   - Records a "dhcp" sighting so the device timeline reflects the
//     observation.
//
// MAC is lowercased before use. Empty MAC is a no-op.
func (s *Store) UpsertFromDHCPLease(ctx context.Context, lease DHCPLeaseEntry, sourceAgent string, observedAt time.Time) error {
	mac := strings.ToLower(strings.TrimSpace(lease.MAC))
	if mac == "" {
		return nil
	}
	if observedAt.IsZero() {
		observedAt = time.Now().UTC()
	}

	// UpsertDevice handles priority-aware hostname merging and first/last seen.
	// Leave Kind/Vendor/ServicesJSON empty so a richer agent sighting always
	// wins those fields.
	dev := &Device{
		ID:             mac,
		MAC:            mac,
		IP:             lease.IP,
		Hostname:       strings.TrimSpace(lease.Hostname),
		HostnameSource: "dhcp",
		LastSeenAt:     observedAt,
		SeenByAgent:    sourceAgent,
	}
	if err := s.UpsertDevice(ctx, dev); err != nil {
		return err
	}

	// Overlay the DHCP-only columns. These are always written: the latest
	// lease observation is the freshest truth for them.
	staticFlag := 0
	if lease.Static {
		staticFlag = 1
	}
	if _, err := s.DB.ExecContext(ctx,
		`UPDATE devices
		    SET static_lease = ?,
		        dhcp_seen_at = ?
		  WHERE id = ?`,
		staticFlag, observedAt.UTC().Format(time.RFC3339), mac); err != nil {
		return err
	}

	if dev.Hostname != "" {
		_ = s.AddHostname(ctx, mac, dev.Hostname, "dhcp", observedAt)
	}
	_ = s.AddSighting(ctx, mac, sourceAgent, lease.IP, mac, "dhcp", observedAt)
	return nil
}
