// Package discover scans the local network for neighbors using OS-native tools.
package discover

import (
	"context"
	"net"
	"strings"
	"sync"
	"time"
)

// Sighting is one observation of a device.
type Sighting struct {
	IP       string            `json:"ip"`
	MAC      string            `json:"mac"`
	Hostname string            `json:"hostname,omitempty"`
	Method   string            `json:"method"` // arp | mdns | ping
	SeenAt   time.Time         `json:"seen_at"`
	Services []string          `json:"services,omitempty"`
	TXT      map[string]string `json:"txt,omitempty"`
	// HostnameSources captures every name observed for this device this scan,
	// keyed by source (mdns | nbns | rdns). The server uses this to track
	// aliases and pick the best name by source priority.
	HostnameSources map[string]string `json:"hostname_sources,omitempty"`
}

// ScanARP returns the current ARP table sightings, enriched with hostnames
// from mDNS, NetBIOS (NBSTAT), and reverse DNS. Each name observed (per
// source) is recorded in HostnameSources so the server can track aliases.
// Sighting.Hostname is set to the best-priority name for convenience.
func ScanARP() []Sighting {
	now := time.Now().UTC()
	out := scanARP()
	mdns := mdnsLookup(2500 * time.Millisecond)
	for i := range out {
		if out[i].SeenAt.IsZero() {
			out[i].SeenAt = now
		}
		if out[i].Method == "" {
			out[i].Method = "arp"
		}
		if info, ok := mdns[out[i].IP]; ok {
			if info.Hostname != "" {
				addSource(&out[i], "mdns", info.Hostname)
			}
			out[i].Services = info.Services
			out[i].TXT = info.TXT
		}
	}

	// NetBIOS: query every IP (cheap, and we want the name even if mDNS already
	// answered, so we can record it as an alias).
	type job struct {
		idx int
		ip  string
	}
	var jobs []job
	for i := range out {
		if out[i].IP != "" {
			jobs = append(jobs, job{i, out[i].IP})
		}
	}
	if len(jobs) > 0 {
		sem := make(chan struct{}, 16)
		results := make([]string, len(jobs))
		var wg sync.WaitGroup
		for k, j := range jobs {
			wg.Add(1)
			sem <- struct{}{}
			go func(k int, j job) {
				defer wg.Done()
				defer func() { <-sem }()
				results[k] = nbnsLookup(j.ip, 400*time.Millisecond)
			}(k, j)
		}
		wg.Wait()
		for k, j := range jobs {
			if results[k] != "" {
				addSource(&out[j.idx], "nbns", results[k])
			}
		}
	}

	// Reverse DNS for everyone (recorded as alias even when mDNS/NBNS won).
	for i := range out {
		if name := reverseLookup(out[i].IP); name != "" {
			addSource(&out[i], "rdns", name)
		}
	}

	// Promote the best-source name into Hostname for convenience.
	for i := range out {
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
// Priority: mdns > nbns > rdns.
func bestHostname(srcs map[string]string) string {
	for _, src := range []string{"mdns", "nbns", "rdns"} {
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
