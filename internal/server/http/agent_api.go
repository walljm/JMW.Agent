package httpsrv

import (
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// pickPrimaryIP picks the most reasonable IPv4 to display for an agent:
// the first non-loopback, non-link-local, non-private-fallback IPv4 from an
// up interface. Falls back to the first non-loopback IPv4 found.
func pickPrimaryIP(inv proto.Inventory) string {
	var fallback string
	for _, ifc := range inv.Network.Interfaces {
		if ifc.IsLoopback || !ifc.IsUp {
			continue
		}
		for _, cidr := range ifc.IPv4 {
			ip := cidr
			if i := strings.IndexByte(ip, '/'); i > 0 {
				ip = ip[:i]
			}
			parsed := net.ParseIP(ip)
			if parsed == nil || parsed.IsLoopback() || parsed.IsLinkLocalUnicast() {
				continue
			}
			if fallback == "" {
				fallback = ip
			}
			// Prefer non-private if available; otherwise return first match.
			return ip
		}
	}
	return fallback
}

func (s *Server) agentRegister(w http.ResponseWriter, r *http.Request) {
	var req proto.RegisterRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid JSON")
		return
	}
	if req.AgentID == "" || req.Hostname == "" {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "agent_id and hostname required")
		return
	}

	existing, err := s.Store.GetAgent(r.Context(), req.AgentID)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	if existing == nil {
		err := s.Store.CreateAgent(r.Context(), &store.Agent{
			ID:                req.AgentID,
			Hostname:          req.Hostname,
			OS:                req.OS,
			Arch:              req.Arch,
			Version:           req.Version,
			Status:            store.AgentStatusPending,
			EnabledSubsystems: req.EnabledSubsystems,
		})
		if err != nil {
			writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
			return
		}
		_ = s.Store.LogEvent(r.Context(), &store.Event{
			Type: "agent.registered", Severity: store.SeverityInfo,
			SourceKind: "agent", SourceID: req.AgentID,
			Summary: "Agent registered (pending approval): " + req.Hostname,
		})
	}

	a, _ := s.Store.GetAgent(r.Context(), req.AgentID)
	resp := proto.RegisterResponse{
		Status:            a.Status,
		HeartbeatInterval: s.heartbeatInterval,
		ServerCertSHA256:  s.ServerCertSHA,
	}
	if a.Status == store.AgentStatusPending {
		resp.Message = "Awaiting administrator approval."
	}
	writeJSON(w, http.StatusOK, resp)
}

func (s *Server) agentHeartbeat(w http.ResponseWriter, r *http.Request) {
	var req proto.HeartbeatRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid JSON")
		return
	}
	if req.AgentID == "" {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "agent_id required")
		return
	}
	a, err := s.Store.GetAgent(r.Context(), req.AgentID)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	if a == nil {
		writeJSONError(w, http.StatusNotFound, "not_found", "agent not found")
		return
	}
	if err := s.Store.TouchAgentHeartbeat(r.Context(), req.AgentID, req.Version, req.EnabledSubsystems); err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	resp := proto.HeartbeatResponse{
		Approved:        a.Status == store.AgentStatusApproved,
		NextHeartbeatIn: s.heartbeatInterval,
	}
	writeJSON(w, http.StatusOK, resp)
}

func (s *Server) agentMetrics(w http.ResponseWriter, r *http.Request) {	var req proto.MetricsRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid JSON")
		return
	}
	if req.AgentID == "" {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "agent_id required")
		return
	}
	a, err := s.Store.GetAgent(r.Context(), req.AgentID)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	if a == nil || a.Status != store.AgentStatusApproved {
		writeJSONError(w, http.StatusForbidden, "not_approved", "agent not approved")
		return
	}
	if err := s.Store.InsertSnapshots(r.Context(), req.AgentID, req.Snapshots); err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	writeJSON(w, http.StatusOK, proto.MetricsResponse{Accepted: len(req.Snapshots)})
}

func (s *Server) agentDiscoveries(w http.ResponseWriter, r *http.Request) {
	var req proto.DiscoveryRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid JSON")
		return
	}
	if req.AgentID == "" {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "agent_id required")
		return
	}
	a, err := s.Store.GetAgent(r.Context(), req.AgentID)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	if a == nil || a.Status != store.AgentStatusApproved {
		writeJSONError(w, http.StatusForbidden, "not_approved", "agent not approved")
		return
	}
	accepted := 0
	for _, sn := range req.Sightings {
		if sn.MAC == "" {
			continue
		}
		var servicesJSON string
		if len(sn.Services) > 0 || len(sn.TXT) > 0 || sn.Hostname != "" {
			blob, err := json.Marshal(map[string]any{
				"hostname": sn.Hostname,
				"services": sn.Services,
				"txt":      sn.TXT,
			})
			if err == nil {
				servicesJSON = string(blob)
			}
		}
		// Pick the best name + its source so the canonical hostname update is
		// priority-aware. Fall back to sn.Hostname (treated as unknown source) if
		// the agent didn't supply HostnameSources.
		bestName, bestSource := pickBestHostname(sn.HostnameSources)
		if bestName == "" && sn.Hostname != "" {
			bestName = sn.Hostname
		}
		dev := &store.Device{
			ID:             strings.ToLower(sn.MAC), // canonical id = MAC
			MAC:            strings.ToLower(sn.MAC),
			IP:             sn.IP,
			Hostname:       bestName,
			HostnameSource: bestSource,
			LastSeenAt:     sn.SeenAt,
			SeenByAgent:    req.AgentID,
			ServicesJSON:   servicesJSON,
		}
		if err := s.Store.UpsertDevice(r.Context(), dev); err != nil {
			continue
		}
		_ = s.Store.AddSighting(r.Context(), dev.ID, req.AgentID, sn.IP, sn.MAC, sn.Method, sn.SeenAt)
		// Record every observed alias so the detail page can surface conflicts.
		for src, name := range sn.HostnameSources {
			_ = s.Store.AddHostname(r.Context(), dev.ID, name, src, sn.SeenAt)
		}
		accepted++
	}
	writeJSON(w, http.StatusOK, proto.DiscoveryResponse{Accepted: accepted})
}

func (s *Server) agentInventory(w http.ResponseWriter, r *http.Request) {
	var req proto.InventoryRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid JSON")
		return
	}
	if req.AgentID == "" {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "agent_id required")
		return
	}
	a, err := s.Store.GetAgent(r.Context(), req.AgentID)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	if a == nil || a.Status != store.AgentStatusApproved {
		writeJSONError(w, http.StatusForbidden, "not_approved", "agent not approved")
		return
	}
	blob, err := json.Marshal(req.Inventory)
	if err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "marshal inventory: "+err.Error())
		return
	}
	collected := req.Inventory.CollectedAt
	if collected.IsZero() {
		collected = time.Now().UTC()
	}
	primaryIP := pickPrimaryIP(req.Inventory)
	if err := s.Store.SetAgentInventory(r.Context(), req.AgentID, string(blob), primaryIP, collected); err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	// Cross-correlate: register each non-loopback agent interface as a known device
	// so neighbor sightings from other agents merge by MAC and surface this agent.
	now := time.Now().UTC()
	for _, ifc := range req.Inventory.Network.Interfaces {
		if ifc.IsLoopback || ifc.MAC == "" {
			continue
		}
		ip := ""
		for _, cidr := range ifc.IPv4 {
			s := cidr
			if i := strings.IndexByte(s, '/'); i > 0 {
				s = s[:i]
			}
			parsed := net.ParseIP(s)
			if parsed == nil || parsed.IsLoopback() || parsed.IsLinkLocalUnicast() {
				continue
			}
			ip = s
			break
		}
		_ = s.Store.UpsertDevice(r.Context(), &store.Device{
			ID:             strings.ToLower(ifc.MAC),
			MAC:            strings.ToLower(ifc.MAC),
			IP:             ip,
			Hostname:       a.Hostname,
			HostnameSource: "agent",
			Kind:           "agent",
			AgentID:        req.AgentID,
			SeenByAgent:    req.AgentID,
			FirstSeenAt:    now,
			LastSeenAt:     now,
		})
		_ = s.Store.AddHostname(r.Context(), strings.ToLower(ifc.MAC), a.Hostname, "agent", now)
	}
	// Sync Docker containers and engine info reported alongside inventory.
	// Containers are first-class entities (their own list/detail views) but
	// their lifecycle follows this host: containers absent from the report
	// are removed.
	if di := req.Inventory.Docker; di != nil {
		_ = s.Store.UpsertDockerEngine(r.Context(), req.AgentID, di, collected)
		if di.Reachable {
			added, _, removed, syncErr := s.Store.SyncContainers(r.Context(), req.AgentID, di.Containers, collected)
			if syncErr == nil && (added > 0 || removed > 0) {
				_ = s.Store.LogEvent(r.Context(), &store.Event{
					Type: "containers.changed", Severity: store.SeverityInfo,
					SourceKind: "agent", SourceID: req.AgentID,
					Summary: fmt.Sprintf("Containers on %s: +%d / -%d", a.Hostname, added, removed),
					Detail:  map[string]any{"added": added, "removed": removed},
				})
			}
		}
	} else {
		// Docker not reported: clear out any prior engine record.
		_ = s.Store.UpsertDockerEngine(r.Context(), req.AgentID, nil, collected)
	}
	_ = s.Store.LogEvent(r.Context(), &store.Event{
		Type: "agent.inventory", Severity: store.SeverityInfo,
		SourceKind: "agent", SourceID: req.AgentID,
		Summary: "Inventory updated: " + a.Hostname,
	})
	writeJSON(w, http.StatusOK, proto.InventoryResponse{Accepted: true})
}

// pickBestHostname returns the highest-priority (name, source) from a
// per-source map, mirroring store.HostnameSourcePriority.
func pickBestHostname(srcs map[string]string) (string, string) {
	for _, src := range []string{"agent", "mdns", "nbns", "rdns"} {
		if v, ok := srcs[src]; ok && v != "" {
			return v, src
		}
	}
	return "", ""
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func writeJSONError(w http.ResponseWriter, code int, errCode, msg string) {
	writeJSON(w, code, proto.ErrorResponse{Error: errCode, Code: errCode, Message: msg})
}
