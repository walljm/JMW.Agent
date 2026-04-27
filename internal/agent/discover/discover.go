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
}

// sourcePriority ranks hostname sources from most to least authoritative.
// Higher = better. Keep in sync with store.HostnameSourcePriority on the
// server. New sources added here MUST also be added there.
var sourcePriority = map[string]int{
	"agent":  100,
	"docker": 95,
	"mdns":   90,
	"llmnr": 85,
	"smb":   80,
	"nbns":  70,
	"snmp":  65,
	"wsd":   60,
	"ssdp":  50,
	"tls":   40,
	"rdns":  30,
	"http":  20,
	"ssh":   15,
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
	out := scanARP()
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

	for i := range out {
		ip := out[i].IP
		if info, ok := mdns[ip]; ok {
			if info.Hostname != "" {
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
