package store

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"strings"
	"time"
)

// NetworkStatus constants.
const (
	NetworkStatusDiscovered = "discovered"
	NetworkStatusMonitored  = "monitored"
	NetworkStatusIgnored    = "ignored"
)

// Network is a logical network segment identified by its gateway MAC.
type Network struct {
	ID         string
	Name       string
	GatewayMAC string
	CIDR       string
	SSID       string
	Status     string
	CreatedAt  time.Time
	LastSeenAt time.Time
	// Populated by list queries for display purposes.
	DeviceCount int
}

// generateNetworkID creates a random 16-byte hex ID.
func generateNetworkID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// UpsertNetwork creates or updates a network record by gateway MAC. Returns
// the network ID (existing or newly created). Atomic via ON CONFLICT to
// avoid a TOCTOU race when two agents report the same gateway simultaneously.
func (s *Store) UpsertNetwork(ctx context.Context, gatewayMAC, cidr, ssid string, seenAt time.Time) (string, error) {
	gatewayMAC = strings.ToLower(strings.TrimSpace(gatewayMAC))
	if gatewayMAC == "" {
		return "", errors.New("gateway_mac required")
	}

	now := seenAt.UTC().Format(time.RFC3339)
	newID := generateNetworkID()

	// INSERT a candidate row; on conflict (gateway_mac already exists),
	// update last_seen_at and optionally CIDR/SSID without changing the id.
	// RETURNING gives us back whichever id won — the existing one on conflict,
	// or our newly generated one on insert.
	var id string
	err := s.DB.QueryRowContext(ctx,
		`INSERT INTO networks (id, name, gateway_mac, cidr, ssid, status, created_at, last_seen_at)
		 VALUES (?, '', ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(gateway_mac) DO UPDATE SET
		   last_seen_at = excluded.last_seen_at,
		   cidr = COALESCE(NULLIF(excluded.cidr, ''), networks.cidr),
		   ssid = COALESCE(NULLIF(excluded.ssid, ''), networks.ssid)
		 RETURNING id`,
		newID, gatewayMAC, cidr, ssid, NetworkStatusDiscovered, now, now).Scan(&id)
	if err != nil {
		return "", err
	}
	return id, nil
}

// AssociateDeviceNetwork links a device to a network, creating or updating
// the junction row.
func (s *Store) AssociateDeviceNetwork(ctx context.Context, deviceID, networkID string, seenAt time.Time) error {
	now := seenAt.UTC().Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO device_networks (device_id, network_id, first_seen_at, last_seen_at)
		 VALUES (?, ?, ?, ?)
		 ON CONFLICT(device_id, network_id) DO UPDATE SET
		   last_seen_at = excluded.last_seen_at`,
		deviceID, networkID, now, now)
	return err
}

// GetNetwork returns a single network by ID.
func (s *Store) GetNetwork(ctx context.Context, id string) (*Network, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, name, gateway_mac, COALESCE(cidr,''), COALESCE(ssid,''),
		        status, created_at, last_seen_at
		 FROM networks WHERE id = ?`, id)
	return scanNetwork(row)
}

// GetNetworkByGatewayMAC returns a network by its gateway MAC.
func (s *Store) GetNetworkByGatewayMAC(ctx context.Context, gwMAC string) (*Network, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, name, gateway_mac, COALESCE(cidr,''), COALESCE(ssid,''),
		        status, created_at, last_seen_at
		 FROM networks WHERE gateway_mac = ?`, strings.ToLower(strings.TrimSpace(gwMAC)))
	return scanNetwork(row)
}

func scanNetwork(row *sql.Row) (*Network, error) {
	var n Network
	var ca, la string
	if err := row.Scan(&n.ID, &n.Name, &n.GatewayMAC, &n.CIDR, &n.SSID,
		&n.Status, &ca, &la); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	n.CreatedAt, _ = time.Parse(time.RFC3339, ca)
	n.LastSeenAt, _ = time.Parse(time.RFC3339, la)
	return &n, nil
}

// ListNetworks returns all networks, optionally filtered by status.
// Pass empty status to list all.
func (s *Store) ListNetworks(ctx context.Context, status string) ([]*Network, error) {
	var rows *sql.Rows
	var err error
	if status == "" {
		rows, err = s.DB.QueryContext(ctx,
			`SELECT n.id, n.name, n.gateway_mac, COALESCE(n.cidr,''), COALESCE(n.ssid,''),
			        n.status, n.created_at, n.last_seen_at,
			        COUNT(dn.device_id)
			 FROM networks n
			 LEFT JOIN device_networks dn ON dn.network_id = n.id
			 GROUP BY n.id
			 ORDER BY CASE n.status
			   WHEN 'monitored' THEN 0
			   WHEN 'discovered' THEN 1
			   WHEN 'ignored' THEN 2
			   ELSE 3 END,
			 n.last_seen_at DESC`)
	} else {
		rows, err = s.DB.QueryContext(ctx,
			`SELECT n.id, n.name, n.gateway_mac, COALESCE(n.cidr,''), COALESCE(n.ssid,''),
			        n.status, n.created_at, n.last_seen_at,
			        COUNT(dn.device_id)
			 FROM networks n
			 LEFT JOIN device_networks dn ON dn.network_id = n.id
			 WHERE n.status = ?
			 GROUP BY n.id
			 ORDER BY n.last_seen_at DESC`, status)
	}
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var out []*Network
	for rows.Next() {
		var n Network
		var ca, la string
		if err := rows.Scan(&n.ID, &n.Name, &n.GatewayMAC, &n.CIDR, &n.SSID,
			&n.Status, &ca, &la, &n.DeviceCount); err != nil {
			return nil, err
		}
		n.CreatedAt, _ = time.Parse(time.RFC3339, ca)
		n.LastSeenAt, _ = time.Parse(time.RFC3339, la)
		out = append(out, &n)
	}
	return out, rows.Err()
}

// UpdateNetworkStatus changes the status of a network.
func (s *Store) UpdateNetworkStatus(ctx context.Context, id, status string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE networks SET status = ? WHERE id = ?`, status, id)
	return err
}

// UpdateNetworkName changes the display name of a network.
func (s *Store) UpdateNetworkName(ctx context.Context, id, name string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE networks SET name = ? WHERE id = ?`, name, id)
	return err
}

// ListDevicesOnNetwork returns devices associated with a specific network.
// Includes all group members of matched devices so grouping works correctly.
func (s *Store) ListDevicesOnNetwork(ctx context.Context, networkID string) ([]*Device, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT d.id, COALESCE(d.mac,''), COALESCE(d.ip,''), COALESCE(d.hostname,''),
		        COALESCE(d.hostname_source,''),
		        COALESCE(d.vendor,''), d.first_seen_at, d.last_seen_at,
		        COALESCE(d.seen_by_agent,''), COALESCE(d.kind,''), COALESCE(d.notes,''),
		        COALESCE(d.agent_id,''), COALESCE(d.services_json,''),
		        COALESCE(d.device_group_id,''),
		        COALESCE(d.static_lease,0), COALESCE(d.dhcp_seen_at,'')
		 FROM devices d
		 WHERE d.id IN (
		   SELECT dn.device_id FROM device_networks dn WHERE dn.network_id = ?
		 )
		 OR (d.device_group_id != '' AND d.device_group_id IN (
		   SELECT d2.device_group_id FROM devices d2
		   INNER JOIN device_networks dn2 ON dn2.device_id = d2.id
		   WHERE dn2.network_id = ? AND d2.device_group_id != ''
		 ))
		 ORDER BY d.ip`, networkID, networkID)
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

// ListDevicesOnMonitoredNetworks returns all devices that are associated
// with at least one monitored network, including group members.
func (s *Store) ListDevicesOnMonitoredNetworks(ctx context.Context) ([]*Device, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT DISTINCT d.id, COALESCE(d.mac,''), COALESCE(d.ip,''), COALESCE(d.hostname,''),
		        COALESCE(d.hostname_source,''),
		        COALESCE(d.vendor,''), d.first_seen_at, d.last_seen_at,
		        COALESCE(d.seen_by_agent,''), COALESCE(d.kind,''), COALESCE(d.notes,''),
		        COALESCE(d.agent_id,''), COALESCE(d.services_json,''),
		        COALESCE(d.device_group_id,''),
		        COALESCE(d.static_lease,0), COALESCE(d.dhcp_seen_at,'')
		 FROM devices d
		 WHERE d.id IN (
		   SELECT dn.device_id FROM device_networks dn
		   INNER JOIN networks n ON n.id = dn.network_id
		   WHERE n.status = ?
		 )
		 OR (d.device_group_id != '' AND d.device_group_id IN (
		   SELECT d2.device_group_id FROM devices d2
		   INNER JOIN device_networks dn2 ON dn2.device_id = d2.id
		   INNER JOIN networks n2 ON n2.id = dn2.network_id
		   WHERE n2.status = ? AND d2.device_group_id != ''
		 ))
		 ORDER BY d.ip`, NetworkStatusMonitored, NetworkStatusMonitored)
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

// MonitoredNetworkCount returns the number of networks with status "monitored".
func (s *Store) MonitoredNetworkCount(ctx context.Context) (int, error) {
	var count int
	err := s.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM networks WHERE status = ?`, NetworkStatusMonitored).Scan(&count)
	return count, err
}

// DiscoveredNetworkCount returns the number of networks with status "discovered"
// (new, unclassified networks).
func (s *Store) DiscoveredNetworkCount(ctx context.Context) (int, error) {
	var count int
	err := s.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM networks WHERE status = ?`, NetworkStatusDiscovered).Scan(&count)
	return count, err
}

// DeviceStatsForMonitored returns device aggregate stats scoped to monitored
// networks only. Returns the same DeviceStats shape but only counting devices
// that have at least one monitored-network association.
func (s *Store) DeviceStatsForMonitored(ctx context.Context) (DeviceStats, error) {
	var st DeviceStats
	cutoff := time.Now().UTC().Add(-24 * time.Hour).Format(time.RFC3339)
	err := s.DB.QueryRowContext(ctx,
		`SELECT
		   COUNT(DISTINCT d.id),
		   COUNT(DISTINCT COALESCE(NULLIF(d.device_group_id,''), '_ungrouped:' || d.id)),
		   COALESCE(SUM(CASE WHEN d.first_seen_at >= ? THEN 1 ELSE 0 END), 0),
		   COALESCE(SUM(CASE WHEN (d.device_group_id IS NULL OR d.device_group_id = '')
		                      AND (d.agent_id IS NULL OR d.agent_id = '') THEN 1 ELSE 0 END), 0)
		 FROM devices d
		 INNER JOIN device_networks dn ON dn.device_id = d.id
		 INNER JOIN networks n ON n.id = dn.network_id
		 WHERE n.status = ?`, cutoff, NetworkStatusMonitored).
		Scan(&st.Total, &st.Groups, &st.NewLast24h, &st.UngroupedNoAgent)
	return st, err
}

// NetworksForDevice returns all networks any of the given device IDs have
// been seen on (deduplicated). Pass all group member IDs to get the full
// picture for a logical device.
func (s *Store) NetworksForDevice(ctx context.Context, deviceIDs []string) ([]*Network, error) {
	if len(deviceIDs) == 0 {
		return nil, nil
	}
	placeholders := make([]string, len(deviceIDs))
	args := make([]any, len(deviceIDs))
	for i, id := range deviceIDs {
		placeholders[i] = "?"
		args[i] = id
	}
	query := `SELECT DISTINCT n.id, n.name, n.gateway_mac, COALESCE(n.cidr,''), COALESCE(n.ssid,''),
	                 n.status, n.created_at, n.last_seen_at, 0
	          FROM networks n
	          INNER JOIN device_networks dn ON dn.network_id = n.id
	          WHERE dn.device_id IN (` + strings.Join(placeholders, ",") + `)
	          ORDER BY n.name`
	rows, err := s.DB.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Network
	for rows.Next() {
		var n Network
		var ca, la string
		if err := rows.Scan(&n.ID, &n.Name, &n.GatewayMAC, &n.CIDR, &n.SSID,
			&n.Status, &ca, &la, &n.DeviceCount); err != nil {
			return nil, err
		}
		n.CreatedAt, _ = time.Parse(time.RFC3339, ca)
		n.LastSeenAt, _ = time.Parse(time.RFC3339, la)
		out = append(out, &n)
	}
	return out, rows.Err()
}
