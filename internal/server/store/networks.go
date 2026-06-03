package store

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"net"
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
			        n.status, n.created_at, n.last_seen_at
			 FROM networks n
			 ORDER BY CASE n.status
			   WHEN 'monitored' THEN 0
			   WHEN 'discovered' THEN 1
			   WHEN 'ignored' THEN 2
			   ELSE 3 END,
			 n.last_seen_at DESC`)
	} else {
		rows, err = s.DB.QueryContext(ctx,
			`SELECT n.id, n.name, n.gateway_mac, COALESCE(n.cidr,''), COALESCE(n.ssid,''),
			        n.status, n.created_at, n.last_seen_at
			 FROM networks n
			 WHERE n.status = ?
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
			&n.Status, &ca, &la); err != nil {
			return nil, err
		}
		n.CreatedAt, _ = time.Parse(time.RFC3339, ca)
		n.LastSeenAt, _ = time.Parse(time.RFC3339, la)
		out = append(out, &n)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	// Populate DeviceCount per network by bucketing all unique IPv4 addresses
	// against each network's CIDR. SQLite has no CIDR operator, so we do this
	// in Go with a single sweep over the address table.
	if err := s.populateNetworkDeviceCounts(ctx, out); err != nil {
		return out, err
	}
	return out, nil
}

// populateNetworkDeviceCounts fills DeviceCount on each network by matching
// IPv4 addresses (one per interface) against the network CIDR. Networks with
// an empty or unparseable CIDR are left at zero.
func (s *Store) populateNetworkDeviceCounts(ctx context.Context, nets []*Network) error {
	if len(nets) == 0 {
		return nil
	}
	type parsedNet struct {
		idx int
		n   *net.IPNet
	}
	var parsed []parsedNet
	for i, nw := range nets {
		if nw.CIDR == "" {
			continue
		}
		_, ipNet, err := net.ParseCIDR(nw.CIDR)
		if err != nil {
			continue
		}
		parsed = append(parsed, parsedNet{idx: i, n: ipNet})
	}
	if len(parsed) == 0 {
		return nil
	}

	// One IPv4 address per interface, choosing the most recently seen.
	rows, err := s.DB.QueryContext(ctx,
		`SELECT interface_id, address FROM interface_addresses
		 WHERE family = 'ipv4'
		 ORDER BY interface_id, last_seen_at DESC`)
	if err != nil {
		return err
	}
	defer rows.Close()

	seen := make(map[string]bool)
	for rows.Next() {
		var ifID, addr string
		if err := rows.Scan(&ifID, &addr); err != nil {
			return err
		}
		if seen[ifID] {
			continue
		}
		seen[ifID] = true
		if i := strings.IndexByte(addr, '/'); i > 0 {
			addr = addr[:i]
		}
		ip := net.ParseIP(addr)
		if ip == nil {
			continue
		}
		for _, p := range parsed {
			if p.n.Contains(ip) {
				nets[p.idx].DeviceCount++
				break
			}
		}
	}
	return rows.Err()
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

// ListDevicesOnNetwork returns devices whose IPv4 address falls within the
// network's CIDR range. Returns an empty list when the network is not found
// or has no CIDR — callers should not assume a fallback to all devices.
func (s *Store) ListDevicesOnNetwork(ctx context.Context, networkID string) ([]*Device, error) {
	nw, err := s.GetNetwork(ctx, networkID)
	if err != nil || nw == nil || nw.CIDR == "" {
		return nil, nil
	}
	return s.ListDevicesInCIDR(ctx, nw.CIDR)
}

// ListUnclassifiedDevices returns devices whose IPv4 address does not fall
// within any known network's CIDR, plus devices with no IP address at all.
func (s *Store) ListUnclassifiedDevices(ctx context.Context) ([]*Device, error) {
	nets, err := s.ListNetworks(ctx, "")
	if err != nil {
		return nil, err
	}
	var ipNets []*net.IPNet
	for _, n := range nets {
		if n.CIDR == "" {
			continue
		}
		_, ipNet, err := net.ParseCIDR(n.CIDR)
		if err != nil {
			continue
		}
		ipNets = append(ipNets, ipNet)
	}

	all, err := s.ListDevices(ctx)
	if err != nil {
		return nil, err
	}

	// If no networks have CIDRs, nothing can be classified — return empty.
	if len(ipNets) == 0 {
		return all, nil
	}

	var out []*Device
	for _, d := range all {
		if d.IP == "" {
			out = append(out, d)
			continue
		}
		ip := net.ParseIP(d.IP)
		if ip == nil {
			out = append(out, d)
			continue
		}
		classified := false
		for _, n := range ipNets {
			if n.Contains(ip) {
				classified = true
				break
			}
		}
		if !classified {
			out = append(out, d)
		}
	}
	return out, nil
}

// ListDevicesOnMonitoredNetworks returns devices on any monitored network.
func (s *Store) ListDevicesOnMonitoredNetworks(ctx context.Context) ([]*Device, error) {
	nets, err := s.ListNetworks(ctx, NetworkStatusMonitored)
	if err != nil || len(nets) == 0 {
		return s.ListDevices(ctx)
	}
	var cidrs []string
	for _, n := range nets {
		if n.CIDR != "" {
			cidrs = append(cidrs, n.CIDR)
		}
	}
	if len(cidrs) == 0 {
		return s.ListDevices(ctx)
	}
	return s.ListDevicesInCIDRs(ctx, cidrs)
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

// DeviceStatsForMonitored returns entity stats. Since per-network device
// tracking is no longer maintained, this delegates to DeviceStats.
func (s *Store) DeviceStatsForMonitored(ctx context.Context) (DeviceStats, error) {
	return s.DeviceStats(ctx)
}

// NetworksForDevice returns networks associated with the given interface IDs.
// In the entity model, per-device network associations are not maintained.
// Returns empty until the network model is extended.
func (s *Store) NetworksForDevice(_ context.Context, _ []string) ([]*Network, error) {
	return nil, nil
}
