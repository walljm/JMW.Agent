// Package discover scans the local network for neighbors using OS-native tools.
package discover

import (
	"context"
	"net"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/walljm/jmwagent/internal/agent/containercache"
)

// Sighting is one observation of a device.
type Sighting struct {
	IP       string            `json:"ip"`
	MAC      string            `json:"mac"`
	Hostname string            `json:"hostname,omitempty"`
	Vendor   string            `json:"vendor,omitempty"` // OUI lookup from MAC
	Kind     string            `json:"kind,omitempty"`   // e.g. "container" when seen on a container bridge
	Method   string            `json:"method"`           // arp | mdns | ping
	SeenAt   time.Time         `json:"seen_at"`
	Services []string          `json:"services,omitempty"`
	TXT      map[string]string `json:"txt,omitempty"`
	// HostnameSources captures every name observed for this device this scan,
	// keyed by source name (see sourcePriority). The server uses this to
	// track aliases and pick the best name by source priority.
	HostnameSources map[string]string `json:"hostname_sources,omitempty"`
	// Probes carries optional per-protocol identity blobs (eureka, ipp,
	// roku, airplay, ldap, dhcp, ssh_fp, title). Sent as-is to the server
	// and stored in services_json for rendering on the device detail page.
	Probes map[string]map[string]string `json:"probes,omitempty"`
}

// sourcePriority ranks hostname sources from most to least authoritative.
// Higher = better. Keep in sync with store.HostnameSourcePriority on the
// server. New sources added here MUST also be added there.
var sourcePriority = map[string]int{
	"agent":   100,
	"docker":  95,
	"dhcp":    93, // self-announced by client to its DHCP server
	"mdns":    90,
	"llmnr":   85,
	"smb":     80,
	"nbns":    70,
	"ldap":    68, // dnsHostName from rootDSE — solid for DCs
	"snmp":    65,
	"eureka":  62, // Google Cast/Nest device-name
	"ipp":     60, // printer-name from IPP
	"roku":    58,
	"airplay": 55,
	"wsd":     52,
	"ssdp":    50,
	"garp":    45, // ARP entries scraped from gateway via SNMP
	"tls":     40,
	"rdns":    30,
	"http":    20,
	"ssh":     15,
}

// sourcesByPriority returns source names ordered most-to-least authoritative.
func sourcesByPriority() []string {
	out := make([]string, 0, len(sourcePriority))
	for k := range sourcePriority {
		out = append(out, k)
	}
	sort.Slice(out, func(i, j int) bool {
		return sourcePriority[out[i]] > sourcePriority[out[j]]
	})
	return out
}

// ScanARP returns the current ARP table sightings, enriched with hostnames
// and vendor data from many sources. Each name observed (per source) is
// recorded in HostnameSources so the server can track aliases.
// Sighting.Hostname is set to the best-priority name for convenience.
//
// The scan runs in roughly three phases:
//  1. Multicast/broadcast sweeps that discover everyone at once: mDNS,
//     SSDP, WS-Discovery. These are run in parallel before per-IP probes
//     so their results can enrich every sighting.
//  2. Per-IP unicast probes (NBNS, SNMP, LLMNR, SMB, TLS, HTTP, SSH,
//     reverse DNS) fanned out with a bounded worker pool.
//  3. Vendor lookup from MAC OUI, then promote the best-priority name
//     into Sighting.Hostname.
func ScanARP() []Sighting {
	now := time.Now().UTC()

	// --- Phase 0: prime the kernel ARP/neigh table. ---
	// A multicast ICMP echo to 224.0.0.1 forces every reachable host to
	// reply unicast; the kernel learns their MAC from the inbound frame
	// regardless of whether userspace consumes the reply, so the next
	// scanARP() read sees them. Best-effort: silent no-op if we lack
	// raw-ICMP capability.
	multicastPingPrime()

	out := scanARP()

	// --- Phase 0b: merge DHCP-server lease entries. ---
	// When the agent runs on a DHCP server, leases are the most
	// authoritative naming source we have (the client itself announced
	// the name). Add any leased IPs we don't already have an ARP entry
	// for, and stash the announced hostname on every match.
	leases := dhcpLookup()
	if len(leases) > 0 {
		seenMAC := make(map[string]int, len(out))
		for i, s := range out {
			seenMAC[s.MAC] = i
		}
		for _, l := range leases {
			if i, ok := seenMAC[l.MAC]; ok {
				if l.Hostname != "" {
					addSource(&out[i], "dhcp", l.Hostname)
				}
				continue
			}
			out = append(out, Sighting{
				IP:     l.IP,
				MAC:    l.MAC,
				Method: "dhcp",
				SeenAt: now,
			})
			if l.Hostname != "" {
				addSource(&out[len(out)-1], "dhcp", l.Hostname)
			}
		}
	}

	// --- Phase 0c: merge gateway SNMP ARP table. ---
	// If the gateway speaks SNMP (community "public" by default), pull
	// its ipNetToMediaPhysAddress so we surface devices on VLANs the
	// agent isn't on. Use a low-priority "garp" source that's beaten by
	// any directly-observed name.
	for _, e := range gatewayARPLookup(nil, 1500*time.Millisecond) {
		found := false
		for i, s := range out {
			if s.MAC == e.MAC {
				if s.IP == "" {
					out[i].IP = e.IP
				}
				found = true
				break
			}
		}
		if !found {
			out = append(out, Sighting{
				IP:     e.IP,
				MAC:    e.MAC,
				Method: "garp",
				SeenAt: now,
			})
		}
	}

	for i := range out {
		if out[i].SeenAt.IsZero() {
			out[i].SeenAt = now
		}
		if out[i].Method == "" {
			out[i].Method = "arp"
		}
	}

	// --- Phase 1: parallel broadcast/multicast sweeps. ---
	var (
		mdns map[string]MDNSInfo
		ssdp map[string]SSDPInfo
		wsd  map[string]WSDInfo
		wg   sync.WaitGroup
	)
	wg.Add(3)
	go func() { defer wg.Done(); mdns = mdnsLookup(2500 * time.Millisecond) }()
	go func() { defer wg.Done(); ssdp = ssdpLookup(2500 * time.Millisecond) }()
	go func() { defer wg.Done(); wsd = wsdLookup(2500 * time.Millisecond) }()
	wg.Wait()

	// Reverse-PTR follow-up over multicast: ask Avahi/mDNSResponder for
	// any IP that didn't resolve in the forward sweep. This catches
	// devices that don't advertise SRV/A but do answer reverse queries.
	{
		needs := make([]string, 0, len(out))
		for _, s := range out {
			if s.IP == "" {
				continue
			}
			if info, ok := mdns[s.IP]; ok && info.Hostname != "" && !looksLikeUUIDHost(info.Hostname) {
				continue
			}
			needs = append(needs, s.IP)
		}
		if mdns == nil {
			mdns = map[string]MDNSInfo{}
		}
		mdnsReversePTR(mdns, needs, 800*time.Millisecond)
	}

	for i := range out {
		ip := out[i].IP
		if info, ok := mdns[ip]; ok {
			// Only report mdns hostnames that look human-meaningful.
			// Google Cast / IoT devices fall back to UUID-shaped SRV
			// targets when the friendly TXT key (`fn`) isn't received;
			// reporting that UUID as our `mdns` source would overwrite
			// any previously-stored friendly name on the server (same
			// source, equal priority → latest wins). Skipping leaves
			// the existing friendly intact.
			if info.Hostname != "" && !looksLikeUUIDHost(info.Hostname) {
				addSource(&out[i], "mdns", info.Hostname)
			}
			out[i].Services = info.Services
			out[i].TXT = info.TXT
		}
		if info, ok := ssdp[ip]; ok {
			if info.FriendlyName != "" {
				addSource(&out[i], "ssdp", info.FriendlyName)
			} else if info.ModelName != "" {
				addSource(&out[i], "ssdp", info.ModelName)
			} else if info.Server != "" {
				addSource(&out[i], "ssdp", info.Server)
			}
		}
		if info, ok := wsd[ip]; ok {
			if info.FriendlyName != "" {
				addSource(&out[i], "wsd", info.FriendlyName)
			}
		}
	}

	// --- Phase 2: per-IP unicast probes. ---
	type result struct {
		nbns, snmp, llmnr, smb, tls, http, ssh, rdns string
		eureka                                       *EurekaInfo
		ipp                                          *IPPInfo
		roku                                         *RokuInfo
		airplay                                      *AirPlayInfo
		ldap                                         *LDAPInfo
		sshFP                                        *SSHFingerprint
	}
	results := make([]result, len(out))
	sem := make(chan struct{}, 24)
	var pwg sync.WaitGroup
	for i := range out {
		ip := out[i].IP
		if ip == "" {
			continue
		}
		pwg.Add(1)
		sem <- struct{}{}
		go func(i int, ip string) {
			defer pwg.Done()
			defer func() { <-sem }()
			// Run probes serially within a worker — the bounded pool already
			// gives us network-level concurrency, and serialising per-IP keeps
			// any single host from being overwhelmed by 8 parallel sockets.
			results[i].nbns = nbnsLookup(ip, 800*time.Millisecond)
			results[i].snmp = snmpLookup(ip, nil, 600*time.Millisecond)
			results[i].llmnr = llmnrLookup(ip, 500*time.Millisecond)
			results[i].smb = smbLookup(ip, 700*time.Millisecond)
			results[i].tls = tlsCertName(ip, 700*time.Millisecond)
			results[i].http = httpBanner(ip, 800*time.Millisecond)
			results[i].ssh = sshBanner(ip, 600*time.Millisecond)
			results[i].rdns = reverseLookup(ip)
			results[i].eureka = eurekaProbe(ip, 1200*time.Millisecond)
			results[i].ipp = ippProbe(ip, 1000*time.Millisecond)
			results[i].roku = rokuProbe(ip, 800*time.Millisecond)
			results[i].airplay = airPlayProbe(ip, 800*time.Millisecond)
			results[i].ldap = ldapProbe(ip, 800*time.Millisecond)
			// Only attempt the full SSH KEX handshake if the cheap
			// banner read already confirmed an SSH server is listening
			// — otherwise we'd waste 1.5s per non-SSH host doing a TCP
			// connect that gets immediately RSTd.
			if results[i].ssh != "" {
				results[i].sshFP = sshHostKey(ip, 1500*time.Millisecond)
			}
		}(i, ip)
	}
	pwg.Wait()

	for i := range out {
		r := results[i]
		if r.nbns != "" {
			addSource(&out[i], "nbns", r.nbns)
		}
		if r.snmp != "" {
			addSource(&out[i], "snmp", r.snmp)
		}
		if r.llmnr != "" {
			addSource(&out[i], "llmnr", r.llmnr)
		}
		if r.smb != "" {
			addSource(&out[i], "smb", r.smb)
		}
		if r.eureka != nil {
			if r.eureka.Name != "" {
				addSource(&out[i], "eureka", r.eureka.Name)
			}
			out[i].setProbe("eureka", map[string]string{
				"name":   r.eureka.Name,
				"model":  r.eureka.Model,
				"build":  r.eureka.Build,
				"mac":    r.eureka.MAC,
				"locale": r.eureka.Locale,
			})
			if out[i].Kind == "" && r.eureka.Model != "" {
				out[i].Kind = "google-cast"
			}
		}
		if r.ipp != nil {
			name := r.ipp.Name
			if name == "" {
				name = r.ipp.Make
			}
			if name != "" {
				addSource(&out[i], "ipp", name)
			}
			out[i].setProbe("ipp", map[string]string{
				"name":     r.ipp.Name,
				"make":     r.ipp.Make,
				"info":     r.ipp.Info,
				"location": r.ipp.Location,
			})
			if out[i].Kind == "" {
				out[i].Kind = "printer"
			}
		}
		if r.roku != nil {
			if r.roku.Name != "" {
				addSource(&out[i], "roku", r.roku.Name)
			}
			out[i].setProbe("roku", map[string]string{
				"name":         r.roku.Name,
				"model":        r.roku.Model,
				"model_number": r.roku.ModelNumber,
				"serial":       r.roku.Serial,
				"software":     r.roku.Software,
			})
			if out[i].Kind == "" {
				out[i].Kind = "roku"
			}
		}
		if r.airplay != nil {
			if r.airplay.Name != "" {
				addSource(&out[i], "airplay", r.airplay.Name)
			}
			out[i].setProbe("airplay", map[string]string{
				"name":    r.airplay.Name,
				"model":   r.airplay.Model,
				"version": r.airplay.Version,
			})
		}
		if r.ldap != nil {
			if r.ldap.DNSHostName != "" {
				addSource(&out[i], "ldap", r.ldap.DNSHostName)
			}
			out[i].setProbe("ldap", map[string]string{
				"dns_hostname":           r.ldap.DNSHostName,
				"default_naming_context": r.ldap.DefaultNamingContext,
				"ldap_service_name":      r.ldap.LDAPServiceName,
				"server_name":            r.ldap.ServerName,
			})
			if out[i].Kind == "" {
				out[i].Kind = "domain-controller"
			}
		}
		if r.sshFP != nil && r.sshFP.SHA256 != "" {
			out[i].setProbe("ssh_fp", map[string]string{
				"algo":   r.sshFP.Algorithm,
				"sha256": r.sshFP.SHA256,
			})
		}
		if r.tls != "" {
			addSource(&out[i], "tls", r.tls)
		}
		if r.http != "" {
			addSource(&out[i], "http", r.http)
		}
		if r.ssh != "" {
			addSource(&out[i], "ssh", r.ssh)
		}
		if r.rdns != "" {
			addSource(&out[i], "rdns", r.rdns)
		}
	}

	// --- Phase 3: vendor lookup + promote best name. ---
	for i := range out {
		// Local container runtime is the most authoritative source for a
		// container's identity — overrides anything bridge-name detection
		// or generic OUI lookup would have set.
		if e, ok := containercache.Lookup(out[i].MAC); ok {
			addSource(&out[i], "docker", e.Name)
			out[i].Vendor = "Docker"
			out[i].Kind = "container"
		}
		if out[i].Vendor == "" {
			out[i].Vendor = ouiLookup(out[i].MAC)
		}
		if h := bestHostname(out[i].HostnameSources); h != "" {
			out[i].Hostname = h
		}
	}
	return out
}

// addSource records (source -> name) on a sighting, lowercasing the name.
func addSource(s *Sighting, source, name string) {
	name = strings.ToLower(strings.TrimSpace(name))
	if name == "" || name == s.IP {
		return
	}
	if s.HostnameSources == nil {
		s.HostnameSources = map[string]string{}
	}
	s.HostnameSources[source] = name
}

// setProbe stores a non-empty probe result map under the given key,
// dropping empty values so the wire payload stays compact.
func (s *Sighting) setProbe(key string, kv map[string]string) {
	clean := make(map[string]string, len(kv))
	for k, v := range kv {
		v = strings.TrimSpace(v)
		if v == "" {
			continue
		}
		clean[k] = v
	}
	if len(clean) == 0 {
		return
	}
	if s.Probes == nil {
		s.Probes = map[string]map[string]string{}
	}
	s.Probes[key] = clean
}

// bestHostname picks the highest-priority name from a sources map.
func bestHostname(srcs map[string]string) string {
	for _, src := range sourcesByPriority() {
		if v, ok := srcs[src]; ok && v != "" {
			return v
		}
	}
	return ""
}

// reverseLookup attempts a best-effort PTR lookup with a short timeout.
// Returns "" if the result is empty, an error, or just echoes the IP.
func reverseLookup(ip string) string {
	if ip == "" {
		return ""
	}
	ctx, cancel := context.WithTimeout(context.Background(), 500*time.Millisecond)
	defer cancel()
	var r net.Resolver
	names, err := r.LookupAddr(ctx, ip)
	if err != nil || len(names) == 0 {
		return ""
	}
	name := strings.TrimSuffix(names[0], ".")
	if name == "" || name == ip {
		return ""
	}
	return name
}
