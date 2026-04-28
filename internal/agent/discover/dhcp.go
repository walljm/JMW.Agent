package discover

import (
	"bufio"
	"encoding/csv"
	"os"
	"strings"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
)

// DHCPLease is one client entry parsed out of a DHCP server's lease file.
// Hostname is whatever the client announced (option 12 / FQDN option) —
// often the cleanest identity available because it's self-reported.
type DHCPLease struct {
	IP       string
	MAC      string
	Hostname string
}

// dhcpLookup returns a map keyed by IP of leases parsed from any local DHCP
// server lease file we can find. Empty result is normal: most agents are
// not running on a DHCP server. When an agent IS the LAN's DHCP server
// (e.g. dnsmasq on a UDM/router/Pi-hole box), this is the most authoritative
// hostname source we have because the client itself announced it.
//
// Reads (in order, returning the first that exists and parses cleanly):
//   - /var/lib/misc/dnsmasq.leases   (dnsmasq, default)
//   - /var/lib/dnsmasq/dnsmasq.leases
//   - /tmp/dhcp.leases               (OpenWrt)
//   - /var/lib/dhcp/dhcpd.leases     (ISC dhcpd, Debian/Ubuntu)
//   - /var/lib/dhcpd/dhcpd.leases    (ISC dhcpd, RHEL)
//   - /var/lib/kea/kea-leases4.csv   (Kea DHCPv4)
func dhcpLookup() map[string]DHCPLease {
	candidates := []struct {
		path  string
		parse func(string) map[string]DHCPLease
	}{
		{"/var/lib/misc/dnsmasq.leases", parseDnsmasqLeases},
		{"/var/lib/dnsmasq/dnsmasq.leases", parseDnsmasqLeases},
		{"/tmp/dhcp.leases", parseDnsmasqLeases},
		{"/var/lib/dhcp/dhcpd.leases", parseISCLeases},
		{"/var/lib/dhcpd/dhcpd.leases", parseISCLeases},
		{"/var/lib/kea/kea-leases4.csv", parseKeaLeases},
	}
	for _, c := range candidates {
		path := hostfs.Path(c.path)
		if _, err := os.Stat(path); err != nil {
			continue
		}
		if m := c.parse(path); len(m) > 0 {
			return m
		}
	}
	return nil
}

// parseDnsmasqLeases reads the dnsmasq lease format:
//
//	<expiry-epoch> <mac> <ip> <hostname-or-*> <client-id-or-*>
//
// Hostname "*" means the client did not announce one.
func parseDnsmasqLeases(path string) map[string]DHCPLease {
	f, err := os.Open(path)
	if err != nil {
		return nil
	}
	defer f.Close()
	out := map[string]DHCPLease{}
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Fields(sc.Text())
		if len(fields) < 4 {
			continue
		}
		mac := strings.ToLower(fields[1])
		ip := fields[2]
		host := fields[3]
		if host == "*" {
			host = ""
		}
		if ip == "" || mac == "" {
			continue
		}
		out[ip] = DHCPLease{IP: ip, MAC: mac, Hostname: host}
	}
	return out
}

// parseISCLeases reads ISC dhcpd's lease database. Each lease is a block:
//
//	lease 192.168.1.42 {
//	  hardware ethernet aa:bb:cc:dd:ee:ff;
//	  client-hostname "kitchenpi";
//	  ...
//	}
//
// We keep only the most recent block per IP (later blocks override earlier
// ones — dhcpd appends).
func parseISCLeases(path string) map[string]DHCPLease {
	f, err := os.Open(path)
	if err != nil {
		return nil
	}
	defer f.Close()
	out := map[string]DHCPLease{}
	var cur DHCPLease
	inLease := false
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if strings.HasPrefix(line, "lease ") && strings.HasSuffix(line, "{") {
			inLease = true
			cur = DHCPLease{IP: strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(line, "lease "), "{"))}
			continue
		}
		if !inLease {
			continue
		}
		if line == "}" {
			if cur.IP != "" && cur.MAC != "" {
				out[cur.IP] = cur
			}
			inLease = false
			continue
		}
		switch {
		case strings.HasPrefix(line, "hardware ethernet "):
			v := strings.TrimSuffix(strings.TrimPrefix(line, "hardware ethernet "), ";")
			cur.MAC = strings.ToLower(strings.TrimSpace(v))
		case strings.HasPrefix(line, "client-hostname "):
			v := strings.TrimSuffix(strings.TrimPrefix(line, "client-hostname "), ";")
			v = strings.Trim(strings.TrimSpace(v), `"`)
			if v != "" {
				cur.Hostname = v
			}
		}
	}
	return out
}

// parseKeaLeases reads Kea DHCPv4's CSV lease file. Header columns include:
//
//	address,hwaddr,client_id,valid_lifetime,expire,subnet_id,
//	fqdn_fwd,fqdn_rev,hostname,state,user_context,pool_id
//
// We use address, hwaddr, hostname.
func parseKeaLeases(path string) map[string]DHCPLease {
	f, err := os.Open(path)
	if err != nil {
		return nil
	}
	defer f.Close()
	r := csv.NewReader(f)
	r.FieldsPerRecord = -1
	rows, err := r.ReadAll()
	if err != nil || len(rows) < 2 {
		return nil
	}
	idxAddr, idxMAC, idxHost := -1, -1, -1
	for i, h := range rows[0] {
		switch strings.ToLower(strings.TrimSpace(h)) {
		case "address":
			idxAddr = i
		case "hwaddr":
			idxMAC = i
		case "hostname":
			idxHost = i
		}
	}
	if idxAddr < 0 || idxMAC < 0 {
		return nil
	}
	out := map[string]DHCPLease{}
	for _, row := range rows[1:] {
		if idxAddr >= len(row) || idxMAC >= len(row) {
			continue
		}
		ip := strings.TrimSpace(row[idxAddr])
		mac := strings.ToLower(strings.TrimSpace(row[idxMAC]))
		host := ""
		if idxHost >= 0 && idxHost < len(row) {
			host = strings.TrimSpace(row[idxHost])
		}
		if ip == "" || mac == "" {
			continue
		}
		// Kea allows multiple rows per IP (lease history). Keep latest.
		out[ip] = DHCPLease{IP: ip, MAC: mac, Hostname: host}
	}
	return out
}
