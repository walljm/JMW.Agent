// Package infra discovers key cyber terrain services from agent inventories.
package infra

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"time"

	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// InfraService is one discovered infrastructure service.
type InfraService struct {
	ServiceType  string
	Label        string
	IP           string
	Hostname     string
	DeviceID     string
	AgentID      string
	Details      map[string]string // enriched metadata (domain, ad_level, vendor, etc.)
	MatchedPorts []int
	LastSeen     time.Time
}

// Scanner scans approved agent inventories for key terrain services and caches
// the results. It runs in the background on a fixed interval.
type Scanner struct {
	store *store.Store

	mu   sync.RWMutex
	svcs []InfraService
}

// New creates a Scanner backed by the given store.
func New(s *store.Store) *Scanner {
	return &Scanner{store: s}
}

// Run scans once immediately then every 2 minutes until ctx is cancelled.
func (sc *Scanner) Run(ctx context.Context) {
	sc.scan(ctx)
	t := time.NewTicker(2 * time.Minute)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			sc.scan(ctx)
		}
	}
}

// Services returns a snapshot of the last scan results.
func (sc *Scanner) Services() []InfraService {
	sc.mu.RLock()
	defer sc.mu.RUnlock()
	out := make([]InfraService, len(sc.svcs))
	copy(out, sc.svcs)
	return out
}

func (sc *Scanner) scan(ctx context.Context) {
	agents, err := sc.store.ListAgents(ctx, "approved")
	if err != nil {
		slog.Warn("infra: list agents failed", "err", err)
		return
	}

	var found []InfraService

	for _, agent := range agents {
		invJSON, _, err := sc.store.GetAgentInventory(ctx, agent.ID)
		if err != nil || invJSON == "" {
			continue
		}

		var inv proto.Inventory
		if err := json.Unmarshal([]byte(invJSON), &inv); err != nil {
			slog.Warn("infra: parse inventory failed", "agent", agent.ID, "err", err)
			continue
		}

		svcs := classify(agent.ID, agent.PrimaryIP, inv.Listening)
		if len(svcs) == 0 {
			continue
		}

		// Enrich LDAP/DC services with rootDSE data.
		for i := range svcs {
			if svcs[i].ServiceType == "ldap" || svcs[i].ServiceType == "domain-controller" {
				if agent.PrimaryIP != "" {
					probeCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
					details := probeLDAPRootDSE(probeCtx, agent.PrimaryIP)
					cancel()
					if len(details) > 0 {
						svcs[i].Details = details
						// Upgrade plain LDAP to DC if we got AD naming context.
						if svcs[i].ServiceType == "ldap" && details["domain"] != "" {
							svcs[i].ServiceType = "domain-controller"
							svcs[i].Label = "Domain Controller"
						}
					}
				}
			}
		}

		// Resolve hostname + device link via system record.
		sys, _ := sc.store.GetSystemByAgent(ctx, agent.ID)
		for i := range svcs {
			if sys != nil {
				svcs[i].Hostname = sys.Hostname
				svcs[i].DeviceID = sys.HardwareID
			}
		}

		found = append(found, svcs...)
	}

	sc.mu.Lock()
	sc.svcs = found
	sc.mu.Unlock()
}

// ── classification ────────────────────────────────────────────────────────────

type portSig struct {
	port  int
	proto string // "tcp" | "udp" | "" (matches both)
}

type serviceDef struct {
	ports       []portSig
	serviceType string
	label       string
}

// keyTerrainDefs is ordered: more-specific multi-port entries first so that
// combined signals (e.g. LDAP + Kerberos → DC) can be detected in classify.
var keyTerrainDefs = []serviceDef{
	{[]portSig{{389, "tcp"}, {636, "tcp"}, {3268, "tcp"}, {3269, "tcp"}}, "ldap", "LDAP Server"},
	{[]portSig{{88, "tcp"}, {88, "udp"}}, "kerberos", "Kerberos KDC"},
	{[]portSig{{53, "tcp"}, {53, "udp"}}, "dns", "DNS Server"},
	{[]portSig{{123, "udp"}}, "ntp", "NTP Server"},
	{[]portSig{{514, "udp"}, {514, "tcp"}, {6514, "tcp"}}, "syslog", "Syslog Collector"},
	{[]portSig{{1812, "udp"}, {1813, "udp"}}, "radius", "RADIUS Server"},
	{[]portSig{{1194, "tcp"}, {1194, "udp"}}, "openvpn", "OpenVPN"},
	{[]portSig{{51820, "udp"}}, "wireguard", "WireGuard"},
	{[]portSig{{500, "udp"}, {4500, "udp"}}, "ipsec", "IPSec VPN"},
	{[]portSig{{8006, "tcp"}}, "proxmox", "Proxmox VE"},
	{[]portSig{{902, "tcp"}}, "esxi", "VMware ESXi"},
}

func normProto(p string) string {
	p = strings.ToLower(p)
	if strings.HasSuffix(p, "6") {
		return p[:len(p)-1] // tcp6 → tcp, udp6 → udp
	}
	return p
}

func classify(agentID, ip string, ports []proto.ListeningPort) []InfraService {
	listening := make(map[string]bool, len(ports))
	for _, p := range ports {
		pr := normProto(p.Proto)
		listening[fmt.Sprintf("%s:%d", pr, p.Port)] = true
	}

	var svcs []InfraService
	hasLDAP := false
	hasKerberos := false

	for _, def := range keyTerrainDefs {
		var matched []int
		for _, ps := range def.ports {
			protos := []string{ps.proto}
			if ps.proto == "" {
				protos = []string{"tcp", "udp"}
			}
			for _, pr := range protos {
				if listening[fmt.Sprintf("%s:%d", pr, ps.port)] {
					matched = append(matched, ps.port)
					break
				}
			}
		}
		if len(matched) == 0 {
			continue
		}
		if def.serviceType == "ldap" {
			hasLDAP = true
		}
		if def.serviceType == "kerberos" {
			hasKerberos = true
		}
		svcs = append(svcs, InfraService{
			ServiceType:  def.serviceType,
			Label:        def.label,
			IP:           ip,
			AgentID:      agentID,
			MatchedPorts: matched,
			LastSeen:     time.Now().UTC(),
		})
	}

	// Combine LDAP + Kerberos → Domain Controller.
	if hasLDAP && hasKerberos {
		var dcPorts []int
		var rest []InfraService
		for _, s := range svcs {
			if s.ServiceType == "ldap" || s.ServiceType == "kerberos" {
				dcPorts = append(dcPorts, s.MatchedPorts...)
			} else {
				rest = append(rest, s)
			}
		}
		rest = append(rest, InfraService{
			ServiceType:  "domain-controller",
			Label:        "Domain Controller",
			IP:           ip,
			AgentID:      agentID,
			MatchedPorts: dcPorts,
			LastSeen:     time.Now().UTC(),
		})
		svcs = rest
	}

	return svcs
}
