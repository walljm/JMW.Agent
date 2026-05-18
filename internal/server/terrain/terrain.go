// Package terrain polls LAN DNS/DHCP infrastructure and caches the results.
package terrain

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"sync"
	"time"
)

// Kind identifies the type of DNS/DHCP server detected.
type Kind string

const (
	KindAdGuard    Kind = "AdGuard Home"
	KindTechnitium Kind = "Technitium DNS"
	KindPiHole     Kind = "Pi-hole"
	KindUnknown    Kind = "Unknown"
)

// TopEntry is a name+count pair from DNS stats.
type TopEntry struct {
	Name  string
	Count int64
}

// DNSStats holds aggregated DNS query statistics.
type DNSStats struct {
	TotalQueries   int64
	BlockedQueries int64
	BlockedPct     float64
	TopQueried     []TopEntry
	TopBlocked     []TopEntry
	TopClients     []TopEntry
}

// DHCPLease is one active DHCP lease.
type DHCPLease struct {
	IP       string
	MAC      string
	Hostname string
	Expires  time.Time
	Static   bool
}

// DHCPStatus holds DHCP server configuration and current leases.
type DHCPStatus struct {
	Enabled    bool
	Interface  string
	Gateway    string
	SubnetMask string
	RangeStart string
	RangeEnd   string
	Leases     []DHCPLease
}

// Status is the full cached state of the terrain server.
type Status struct {
	Kind       Kind
	URL        string
	Reachable  bool
	DNS        *DNSStats
	DHCP       *DHCPStatus
	Error      string
	LastPolled time.Time
}

// Config holds terrain poller configuration.
type Config struct {
	// URL is the base URL of the AdGuard Home, Technitium DNS, or Pi-hole instance.
	// e.g. "http://192.168.1.54:3000" (AdGuard), "http://192.168.1.54:5380" (Technitium),
	// or "http://192.168.1.54" (Pi-hole).
	// If empty, auto-detection is attempted.
	URL string
	// Token is a Technitium API token (preferred over username/password for Technitium).
	Token    string
	Username string
	Password string
}

// DeviceSink receives DHCP leases observed by the poller so the server can
// overlay them onto its devices table. Implemented by *store.Store; defined
// as an interface here to keep the terrain package free of a store import.
type DeviceSink interface {
	UpsertFromDHCPLease(ctx context.Context, lease DHCPLeaseToSink, sourceAgent string, observedAt time.Time) error
}

// DHCPLeaseToSink is the lease shape the poller hands to the sink. Kept
// separate from DHCPLease so the sink doesn't pick up presentation-only
// fields that may be added later.
type DHCPLeaseToSink struct {
	MAC      string
	IP       string
	Hostname string
	Static   bool
	Expires  time.Time
}

// Poller periodically polls the LAN DNS/DHCP server and caches the result.
type Poller struct {
	cfg    Config
	sink   DeviceSink
	client *http.Client

	mu        sync.RWMutex
	status    Status
	techToken string // cached Technitium session token from login
}

// New creates a Poller. If cfg.URL is empty, auto-detection runs on first poll.
func New(cfg Config) *Poller {
	return &Poller{
		cfg: cfg,
		client: &http.Client{
			Timeout: 5 * time.Second,
			Transport: &http.Transport{
				TLSClientConfig: &tls.Config{InsecureSkipVerify: true}, //nolint:gosec -- LAN-only, self-signed cert expected
			},
		},
		status: Status{Kind: KindUnknown},
	}
}

// SetDeviceSink wires a sink that receives DHCP leases after each successful
// poll. Optional; if nil, lease overlay is skipped.
func (p *Poller) SetDeviceSink(s DeviceSink) {
	p.mu.Lock()
	p.sink = s
	p.mu.Unlock()
}

// Run polls until ctx is cancelled, with a 60-second interval.
func (p *Poller) Run(ctx context.Context) {
	p.poll(ctx)
	t := time.NewTicker(60 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			p.poll(ctx)
		}
	}
}

// Status returns a copy of the last-polled status.
func (p *Poller) Status() Status {
	p.mu.RLock()
	defer p.mu.RUnlock()
	return p.status
}

func (p *Poller) poll(ctx context.Context) {
	url := p.cfg.URL
	if url == "" {
		url = p.detect(ctx)
	}
	if url == "" {
		p.setStatus(Status{
			Kind:       KindUnknown,
			Error:      "no DNS/DHCP server found",
			LastPolled: time.Now().UTC(),
		})
		return
	}

	// Try each backend in turn.
	if s, ok := p.pollAdGuard(ctx, url); ok {
		p.setStatus(s)
		return
	}
	if s, ok := p.pollTechnitium(ctx, url); ok {
		p.setStatus(s)
		return
	}
	if s, ok := p.pollPiHole(ctx, url); ok {
		p.setStatus(s)
		return
	}

	p.setStatus(Status{
		Kind:       KindUnknown,
		URL:        url,
		Reachable:  false,
		Error:      "server responded but is not AdGuard Home, Technitium DNS, or Pi-hole",
		LastPolled: time.Now().UTC(),
	})
}

func (p *Poller) setStatus(s Status) {
	p.mu.Lock()
	p.status = s
	p.mu.Unlock()
	p.overlayLeases(s)
}

// overlayLeases hands every fresh DHCP lease to the configured sink. Stale
// (expired, non-static) leases are skipped because most DHCP servers retain
// expired leases in their list endpoint for a while.
func (p *Poller) overlayLeases(s Status) {
	p.mu.RLock()
	sink := p.sink
	p.mu.RUnlock()
	if sink == nil || !s.Reachable || s.DHCP == nil || len(s.DHCP.Leases) == 0 {
		return
	}
	now := time.Now().UTC()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	for _, l := range s.DHCP.Leases {
		if !l.Static && !l.Expires.IsZero() && l.Expires.Before(now) {
			continue
		}
		if err := sink.UpsertFromDHCPLease(ctx, DHCPLeaseToSink{
			MAC:      l.MAC,
			IP:       l.IP,
			Hostname: l.Hostname,
			Static:   l.Static,
			Expires:  l.Expires,
		}, "terrain", now); err != nil {
			slog.Warn("terrain: dhcp overlay failed", "mac", l.MAC, "err", err)
		}
	}
}

// detect probes candidate LAN addresses to find an AdGuard, Technitium, or Pi-hole instance.
// Returns the base URL of the first server found, or empty string.
func (p *Poller) detect(ctx context.Context) string {
	candidates := p.buildCandidates()
	for _, u := range candidates {
		if _, ok := p.pollAdGuard(ctx, u); ok {
			slog.Info("terrain: auto-detected AdGuard Home", "url", u)
			return u
		}
		if _, ok := p.pollTechnitium(ctx, u); ok {
			slog.Info("terrain: auto-detected Technitium DNS", "url", u)
			return u
		}
		if _, ok := p.pollPiHole(ctx, u); ok {
			slog.Info("terrain: auto-detected Pi-hole", "url", u)
			return u
		}
	}
	return ""
}

// buildCandidates returns a list of base URLs to probe during auto-detection.
// Derives LAN subnets from local network interfaces and probes common IPs/ports
// for AdGuard Home (3000), Technitium DNS (5380), and Pi-hole (80).
func (p *Poller) buildCandidates() []string {
	subnets := lanSubnets()
	var out []string
	for _, s := range subnets {
		for _, ip := range subnetCandidateIPs(s) {
			out = append(out, fmt.Sprintf("http://%s:3000", ip)) // AdGuard
			out = append(out, fmt.Sprintf("http://%s:5380", ip)) // Technitium
			out = append(out, fmt.Sprintf("http://%s:80", ip))   // Pi-hole
		}
	}
	return out
}

// lanSubnets returns the /24 prefixes of non-loopback, non-link-local LAN interfaces.
func lanSubnets() []string {
	ifaces, err := net.Interfaces()
	if err != nil {
		return nil
	}
	var out []string
	for _, iface := range ifaces {
		if iface.Flags&net.FlagLoopback != 0 || iface.Flags&net.FlagUp == 0 {
			continue
		}
		addrs, err := iface.Addrs()
		if err != nil {
			continue
		}
		for _, a := range addrs {
			var ip net.IP
			switch v := a.(type) {
			case *net.IPNet:
				ip = v.IP
			case *net.IPAddr:
				ip = v.IP
			}
			if ip == nil || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
				continue
			}
			ip4 := ip.To4()
			if ip4 == nil {
				continue
			}
			// Only private RFC-1918 space.
			if !isPrivate(ip4) {
				continue
			}
			out = append(out, fmt.Sprintf("%d.%d.%d", ip4[0], ip4[1], ip4[2]))
		}
	}
	return out
}

// subnetCandidateIPs returns the most-likely DHCP/DNS server IPs in a /24.
func subnetCandidateIPs(prefix string) []string {
	return []string{
		prefix + ".1",
		prefix + ".53",
		prefix + ".54",
		prefix + ".2",
	}
}

func isPrivate(ip net.IP) bool {
	private := []net.IPNet{
		{IP: net.IP{10, 0, 0, 0}, Mask: net.CIDRMask(8, 32)},
		{IP: net.IP{172, 16, 0, 0}, Mask: net.CIDRMask(12, 32)},
		{IP: net.IP{192, 168, 0, 0}, Mask: net.CIDRMask(16, 32)},
	}
	for _, n := range private {
		if n.Contains(ip) {
			return true
		}
	}
	return false
}

// ── AdGuard Home ─────────────────────────────────────────────────────────────

// adguardStatus is the response from GET /control/status.
type adguardStatus struct {
	Running           bool     `json:"running"`
	ProtectionEnabled bool     `json:"protection_enabled"`
	DNSAddresses      []string `json:"dns_addresses"`
	DNSPort           int      `json:"dns_port"`
	HTTPPort          int      `json:"http_port"`
	Version           string   `json:"version"`
}

// adguardStats is the response from GET /control/stats.
type adguardStats struct {
	NumDNSQueries         int64            `json:"num_dns_queries"`
	NumBlockedFiltering   int64            `json:"num_blocked_filtering"`
	TopQueriedDomains     []map[string]int `json:"top_queried_domains"`
	TopBlockedDomains     []map[string]int `json:"top_blocked_domains"`
	TopClients            []map[string]int `json:"top_clients"`
}

// adguardDHCP is the response from GET /control/dhcp/status.
type adguardDHCP struct {
	Enabled       bool   `json:"enabled"`
	InterfaceName string `json:"interface_name"`
	V4            struct {
		GatewayIP   string `json:"gateway_ip"`
		SubnetMask  string `json:"subnet_mask"`
		RangeStart  string `json:"range_start"`
		RangeEnd    string `json:"range_end"`
	} `json:"v4"`
	Leases []struct {
		IP       string `json:"ip"`
		MAC      string `json:"mac"`
		Hostname string `json:"hostname"`
		Expires  string `json:"expires"`
	} `json:"leases"`
	StaticLeases []struct {
		IP       string `json:"ip"`
		MAC      string `json:"mac"`
		Hostname string `json:"hostname"`
	} `json:"static_leases"`
}

func (p *Poller) pollAdGuard(ctx context.Context, base string) (Status, bool) {
	var st adguardStatus
	if err := p.getJSON(ctx, base+"/control/status", &st); err != nil {
		if err == errUnauthorized {
			return Status{
				Kind:       KindAdGuard,
				URL:        base,
				Reachable:  false,
				Error:      "AdGuard Home requires credentials — set username and password in [terrain] config",
				LastPolled: time.Now().UTC(),
			}, true
		}
		return Status{}, false
	}
	if !st.Running {
		return Status{}, false
	}

	s := Status{
		Kind:       KindAdGuard,
		URL:        base,
		Reachable:  true,
		LastPolled: time.Now().UTC(),
	}

	// DNS stats.
	var stats adguardStats
	if err := p.getJSON(ctx, base+"/control/stats", &stats); err == nil {
		dns := &DNSStats{
			TotalQueries:   stats.NumDNSQueries,
			BlockedQueries: stats.NumBlockedFiltering,
		}
		if dns.TotalQueries > 0 {
			dns.BlockedPct = float64(dns.BlockedQueries) / float64(dns.TotalQueries) * 100
		}
		dns.TopQueried = flattenTopMap(stats.TopQueriedDomains, 10)
		dns.TopBlocked = flattenTopMap(stats.TopBlockedDomains, 10)
		dns.TopClients = flattenTopMap(stats.TopClients, 10)
		s.DNS = dns
	}

	// DHCP status.
	var dhcp adguardDHCP
	if err := p.getJSON(ctx, base+"/control/dhcp/status", &dhcp); err == nil {
		d := &DHCPStatus{
			Enabled:    dhcp.Enabled,
			Interface:  dhcp.InterfaceName,
			Gateway:    dhcp.V4.GatewayIP,
			SubnetMask: dhcp.V4.SubnetMask,
			RangeStart: dhcp.V4.RangeStart,
			RangeEnd:   dhcp.V4.RangeEnd,
		}
		for _, l := range dhcp.Leases {
			exp, _ := time.Parse(time.RFC3339, l.Expires)
			d.Leases = append(d.Leases, DHCPLease{
				IP:       l.IP,
				MAC:      l.MAC,
				Hostname: l.Hostname,
				Expires:  exp,
				Static:   false,
			})
		}
		for _, l := range dhcp.StaticLeases {
			d.Leases = append(d.Leases, DHCPLease{
				IP:       l.IP,
				MAC:      l.MAC,
				Hostname: l.Hostname,
				Static:   true,
			})
		}
		s.DHCP = d
	}

	return s, true
}

// ── Technitium DNS ────────────────────────────────────────────────────────────

// technitiumEnvelope is the common Technitium API response wrapper.
type technitiumEnvelope struct {
	Response     json.RawMessage `json:"response"`
	Status       string          `json:"status"`
	ErrorMessage string          `json:"errorMessage"`
}

type technitiumStats struct {
	Stats struct {
		TotalQueries int64 `json:"totalQueries"`
		TotalBlocked int64 `json:"totalBlocked"`
	} `json:"stats"`
	TopDomains        []technitiumTopEntry `json:"topDomains"`
	TopBlockedDomains []technitiumTopEntry `json:"topBlockedDomains"`
	TopClients        []technitiumTopEntry `json:"topClients"`
}

type technitiumTopEntry struct {
	Name string `json:"name"`
	Hits int64  `json:"hits"`
}

type technitiumScopesList struct {
	Scopes []technitiumScopeSummary `json:"scopes"`
}

type technitiumScopeSummary struct {
	Name             string `json:"name"`
	Enabled          bool   `json:"enabled"`
	StartingAddress  string `json:"startingAddress"`
	EndingAddress    string `json:"endingAddress"`
	SubnetMask       string `json:"subnetMask"`
	NetworkAddress   string `json:"networkAddress"`
	InterfaceAddress string `json:"interfaceAddress"`
}

type technitiumScopeDetail struct {
	Name             string `json:"name"`
	StartingAddress  string `json:"startingAddress"`
	EndingAddress    string `json:"endingAddress"`
	SubnetMask       string `json:"subnetMask"`
	RouterAddress    string `json:"routerAddress"`
	InterfaceAddress string `json:"interfaceAddress"`
}

type technitiumLeasesList struct {
	Leases []technitiumLease `json:"leases"`
}

type technitiumLease struct {
	Scope            string `json:"scope"`
	Type             string `json:"type"` // "Dynamic", "Reserved", "Static"
	HardwareAddress  string `json:"hardwareAddress"`
	Address          string `json:"address"`
	HostName         string `json:"hostName"`
	LeaseObtained    string `json:"leaseObtained"`
	LeaseExpires     string `json:"leaseExpires"`
}

// technitiumLeaseTimeFmt matches the format Technitium emits, e.g. "08/25/2020 17:52:51".
const technitiumLeaseTimeFmt = "01/02/2006 15:04:05"

func (p *Poller) pollTechnitium(ctx context.Context, base string) (Status, bool) {
	token, err := p.technitiumToken(ctx, base)
	if err != nil {
		// Reached the host but auth failed → report a definitive Technitium status.
		if err == errUnauthorized {
			return Status{
				Kind:       KindTechnitium,
				URL:        base,
				Reachable:  false,
				Error:      "Technitium DNS requires credentials — set token (or username and password) in [terrain] config",
				LastPolled: time.Now().UTC(),
			}, true
		}
		return Status{}, false
	}

	// Dashboard stats. If token expired, refresh once.
	var stats technitiumStats
	if err := p.technitiumCall(ctx, base, "/api/dashboard/stats/get", url.Values{
		"type": []string{"LastDay"},
		"utc":  []string{"true"},
	}, token, &stats); err != nil {
		if err == errUnauthorized {
			// Cached token may be stale; force fresh login if possible.
			p.mu.Lock()
			p.techToken = ""
			p.mu.Unlock()
			token, err = p.technitiumToken(ctx, base)
			if err != nil {
				return Status{}, false
			}
			if err := p.technitiumCall(ctx, base, "/api/dashboard/stats/get", url.Values{
				"type": []string{"LastDay"},
				"utc":  []string{"true"},
			}, token, &stats); err != nil {
				return Status{}, false
			}
		} else {
			return Status{}, false
		}
	}

	s := Status{
		Kind:       KindTechnitium,
		URL:        base,
		Reachable:  true,
		LastPolled: time.Now().UTC(),
	}

	dns := &DNSStats{
		TotalQueries:   stats.Stats.TotalQueries,
		BlockedQueries: stats.Stats.TotalBlocked,
	}
	if dns.TotalQueries > 0 {
		dns.BlockedPct = float64(dns.BlockedQueries) / float64(dns.TotalQueries) * 100
	}
	dns.TopQueried = technitiumTop(stats.TopDomains, 10)
	dns.TopBlocked = technitiumTop(stats.TopBlockedDomains, 10)
	dns.TopClients = technitiumTop(stats.TopClients, 10)
	s.DNS = dns

	// DHCP: pick first enabled scope (else first scope), then enrich + filter leases.
	var scopes technitiumScopesList
	if err := p.technitiumCall(ctx, base, "/api/dhcp/scopes/list", nil, token, &scopes); err == nil && len(scopes.Scopes) > 0 {
		chosen := scopes.Scopes[0]
		for _, sc := range scopes.Scopes {
			if sc.Enabled {
				chosen = sc
				break
			}
		}
		d := &DHCPStatus{
			Enabled:    chosen.Enabled,
			Interface:  chosen.Name,
			SubnetMask: chosen.SubnetMask,
			RangeStart: chosen.StartingAddress,
			RangeEnd:   chosen.EndingAddress,
		}
		// Enrich with routerAddress from scope detail.
		var detail technitiumScopeDetail
		if err := p.technitiumCall(ctx, base, "/api/dhcp/scopes/get", url.Values{
			"name": []string{chosen.Name},
		}, token, &detail); err == nil {
			d.Gateway = detail.RouterAddress
		}

		var leases technitiumLeasesList
		if err := p.technitiumCall(ctx, base, "/api/dhcp/leases/list", nil, token, &leases); err == nil {
			for _, l := range leases.Leases {
				if l.Scope != "" && l.Scope != chosen.Name {
					continue
				}
				exp, _ := time.ParseInLocation(technitiumLeaseTimeFmt, l.LeaseExpires, time.Local)
				static := strings.EqualFold(l.Type, "Reserved") || strings.EqualFold(l.Type, "Static")
				d.Leases = append(d.Leases, DHCPLease{
					IP:       l.Address,
					MAC:      l.HardwareAddress,
					Hostname: l.HostName,
					Expires:  exp,
					Static:   static,
				})
			}
		}
		s.DHCP = d
	}

	return s, true
}

// technitiumToken returns a usable API token: cfg.Token if set, otherwise a
// cached login token, otherwise a fresh login via username/password.
func (p *Poller) technitiumToken(ctx context.Context, base string) (string, error) {
	if p.cfg.Token != "" {
		return p.cfg.Token, nil
	}
	p.mu.RLock()
	cached := p.techToken
	p.mu.RUnlock()
	if cached != "" {
		return cached, nil
	}
	if p.cfg.Username == "" || p.cfg.Password == "" {
		return "", errUnauthorized
	}

	q := url.Values{
		"user":        []string{p.cfg.Username},
		"pass":        []string{p.cfg.Password},
		"includeInfo": []string{"false"},
	}
	loginURL := base + "/api/user/login?" + q.Encode()
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, loginURL, nil)
	if err != nil {
		return "", err
	}
	resp, err := p.client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("technitium login HTTP %d", resp.StatusCode)
	}
	// Login response puts token at the top level alongside status, not under `response`.
	var body struct {
		Token  string `json:"token"`
		Status string `json:"status"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return "", err
	}
	if body.Status != "ok" || body.Token == "" {
		return "", errUnauthorized
	}
	p.mu.Lock()
	p.techToken = body.Token
	p.mu.Unlock()
	return body.Token, nil
}

// technitiumCall issues a GET against the Technitium API and decodes the
// envelope's `response` field into out. Returns errUnauthorized if the
// envelope status is "invalid-token".
func (p *Poller) technitiumCall(ctx context.Context, base, path string, q url.Values, token string, out any) error {
	if q == nil {
		q = url.Values{}
	}
	q.Set("token", token)
	u := base + path + "?" + q.Encode()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return err
	}
	resp, err := p.client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	var env technitiumEnvelope
	if err := json.NewDecoder(resp.Body).Decode(&env); err != nil {
		return err
	}
	switch env.Status {
	case "ok":
		if out == nil || len(env.Response) == 0 {
			return nil
		}
		return json.Unmarshal(env.Response, out)
	case "invalid-token":
		return errUnauthorized
	case "error":
		if env.ErrorMessage != "" {
			return fmt.Errorf("technitium error: %s", env.ErrorMessage)
		}
		return fmt.Errorf("technitium error")
	default:
		return fmt.Errorf("technitium status %q", env.Status)
	}
}

func technitiumTop(in []technitiumTopEntry, limit int) []TopEntry {
	out := make([]TopEntry, 0, len(in))
	for _, e := range in {
		out = append(out, TopEntry{Name: e.Name, Count: e.Hits})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Count > out[j].Count })
	if len(out) > limit {
		out = out[:limit]
	}
	return out
}

// ── Pi-hole ───────────────────────────────────────────────────────────────────

type piholeAPIResp struct {
	DomainsBeingBlocked int64   `json:"domains_being_blocked"`
	DNSQueriesToday     int64   `json:"dns_queries_today"`
	AdsBlockedToday     int64   `json:"ads_blocked_today"`
	AdsPercentageToday  float64 `json:"ads_percentage_today"`
	Status              string  `json:"status"`
}

func (p *Poller) pollPiHole(ctx context.Context, base string) (Status, bool) {
	var resp piholeAPIResp
	url := base + "/admin/api.php?summary"
	if err := p.getJSON(ctx, url, &resp); err != nil {
		return Status{}, false
	}
	// Pi-hole returns status:"enabled" or "disabled"; unknown hosts return nothing parseable.
	if resp.Status == "" && resp.DNSQueriesToday == 0 {
		return Status{}, false
	}

	s := Status{
		Kind:      KindPiHole,
		URL:       base,
		Reachable: true,
		DNS: &DNSStats{
			TotalQueries:   resp.DNSQueriesToday,
			BlockedQueries: resp.AdsBlockedToday,
			BlockedPct:     resp.AdsPercentageToday,
		},
		LastPolled: time.Now().UTC(),
	}
	return s, true
}

// ── helpers ───────────────────────────────────────────────────────────────────

// errUnauthorized is returned when the server requires credentials.
var errUnauthorized = fmt.Errorf("HTTP 401: credentials required")

func (p *Poller) getJSON(ctx context.Context, url string, out any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	if p.cfg.Username != "" {
		req.SetBasicAuth(p.cfg.Username, p.cfg.Password)
	}
	resp, err := p.client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusUnauthorized {
		return errUnauthorized
	}
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	return json.NewDecoder(resp.Body).Decode(out)
}

// flattenTopMap converts AdGuard's []map[string]int into sorted TopEntry slice.
func flattenTopMap(list []map[string]int, limit int) []TopEntry {
	var out []TopEntry
	for _, m := range list {
		for k, v := range m {
			out = append(out, TopEntry{Name: k, Count: int64(v)})
			break // each map has exactly one entry
		}
		if len(out) >= limit {
			break
		}
	}
	return out
}
