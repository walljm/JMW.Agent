package httpsrv

import (
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"strings"
	"time"

	"github.com/go-chi/chi/v5"
	"github.com/walljm/jmwagent/internal/server/pipeline/adapters"
	"github.com/walljm/jmwagent/internal/server/releases"
	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// pickPrimaryMAC returns the first non-loopback, non-empty MAC from the
// inventory — the canonical interface the agent reports itself as.
func pickPrimaryMAC(inv proto.Inventory) string {
	for _, ifc := range inv.Network.Interfaces {
		if ifc.IsLoopback || ifc.MAC == "" {
			continue
		}
		return ifc.MAC
	}
	return ""
}

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
	heartbeatSecs, discoverySecs, inventorySecs, err := s.Store.GetAgentIntervals(r.Context())
	if err != nil {
		slog.Warn("register: read agent intervals", "err", err)
	}
	resp := proto.RegisterResponse{
		Status:                a.Status,
		HeartbeatInterval:     heartbeatSecs,
		DiscoveryIntervalSecs: discoverySecs,
		InventoryIntervalSecs: inventorySecs,
		ServerCertSHA256:      s.ServerCertSHA,
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
	heartbeatSecs, discoverySecs, inventorySecs, err := s.Store.GetAgentIntervals(r.Context())
	if err != nil {
		slog.Warn("heartbeat: read agent intervals", "err", err)
	}
	resp := proto.HeartbeatResponse{
		Approved:              a.Status == store.AgentStatusApproved,
		NextHeartbeatIn:       heartbeatSecs,
		DiscoveryIntervalSecs: discoverySecs,
		InventoryIntervalSecs: inventorySecs,
	}
	// Offer an update when a strictly-newer clean release exists on disk
	// for the agent's platform. The agent's own version may be a dev/dirty
	// build — semver ordering still places clean releases above prerelease
	// builds at the same MAJOR.MINOR.PATCH, so the comparison Just Works.
	if s.Releases != nil && s.Releases.Enabled() && a.OS != "" && a.Arch != "" {
		if e, ok := s.Releases.Latest(a.OS, a.Arch); ok && releases.SemverGreater(req.Version, e.Version) {
			resp.Update = &proto.UpdateInfo{
				Version: e.Version,
				URL:     "/api/v1/agent/releases/" + e.Version + "/" + e.Filename,
				SHA256:  e.SHA256,
				Size:    e.Size,
			}
		}
	}
	writeJSON(w, http.StatusOK, resp)
}

func (s *Server) agentMetrics(w http.ResponseWriter, r *http.Request) {
	var req proto.MetricsRequest
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

	if s.Ingestor == nil {
		writeJSONError(w, http.StatusInternalServerError, "server_error", "ingestor not initialised")
		return
	}
	srcID, srcErr := s.Store.EnsureAgentSource(r.Context(), req.AgentID, a.Hostname)
	if srcErr != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", srcErr.Error())
		return
	}
	payload := &adapters.AgentMetricsPayload{
		AgentID:   req.AgentID,
		Snapshots: req.Snapshots,
	}
	if _, pErr := s.Ingestor.Ingest(r.Context(), "agent-metrics", srcID, payload); pErr != nil {
		slog.Warn("pipeline ingest failed", "kind", "agent-metrics", "agent", req.AgentID, "err", pErr)
		writeJSONError(w, http.StatusInternalServerError, "db_error", pErr.Error())
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
	// Count accepted sightings (those with a MAC).
	accepted := 0
	for _, sn := range req.Sightings {
		if sn.MAC != "" {
			accepted++
		}
	}

	// Feed through the entity pipeline as the sole write path.
	if s.Ingestor != nil {
		srcID, srcErr := s.Store.EnsureAgentSource(r.Context(), req.AgentID, a.Hostname)
		if srcErr == nil {
			if _, pErr := s.Ingestor.Ingest(r.Context(), "agent-discovery", srcID, &req); pErr != nil {
				slog.Warn("pipeline ingest failed", "kind", "agent-discovery", "agent", req.AgentID, "err", pErr)
			}
		}
	}

	// Upsert the reporting network (gateway MAC + CIDR + SSID). Device-to-network
	// association is computed by CIDR matching in ListNetworks, so we only need
	// the networks row to exist.
	if req.Network != nil && req.Network.GatewayMAC != "" {
		if _, err := s.Store.UpsertNetwork(r.Context(),
			req.Network.GatewayMAC, req.Network.CIDR, req.Network.SSID, time.Now().UTC()); err != nil {
			slog.Warn("upsert network failed", "agent", req.AgentID, "gw_mac", req.Network.GatewayMAC, "err", err)
		}
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
	_ = s.Store.LogEvent(r.Context(), &store.Event{
		Type: "agent.inventory", Severity: store.SeverityInfo,
		SourceKind: "agent", SourceID: req.AgentID,
		Summary: "Inventory updated: " + a.Hostname,
	})

	// Feed through the entity pipeline as the sole write path.
	if s.Ingestor != nil {
		srcID, srcErr := s.Store.EnsureAgentSource(r.Context(), req.AgentID, a.Hostname)
		if srcErr == nil {
			if _, pErr := s.Ingestor.Ingest(r.Context(), "agent-inventory", srcID, &req.Inventory); pErr != nil {
				slog.Warn("pipeline ingest failed", "kind", "agent-inventory", "agent", req.AgentID, "err", pErr)
			}
		}
	}

	// Link the agent to its hardware so Device.AgentID is populated on the
	// device detail page (hydrateDevices joins systems on hardware_id).
	if mac := pickPrimaryMAC(req.Inventory); mac != "" {
		_ = s.Store.EnsureAgentSystem(r.Context(), req.AgentID, mac)
	}

	// Write expanded metric snapshots from inventory data (temperature, battery).
	if len(req.Inventory.Hardware.Temperatures) > 0 {
		temps := make([]store.TempSnapshot, len(req.Inventory.Hardware.Temperatures))
		for i, t := range req.Inventory.Hardware.Temperatures {
			temps[i] = store.TempSnapshot{Sensor: t.Name, Celsius: t.Celsius}
		}
		_ = s.Store.InsertTemperatureSnapshots(r.Context(), req.AgentID, collected, temps)
	}
	if ch := req.Inventory.Chassis; ch != nil && ch.Battery != nil {
		_ = s.Store.InsertBatterySnapshot(r.Context(), req.AgentID, collected,
			ch.Battery.ChargePercent, ch.Battery.State, ch.Battery.HealthPercent)
	}

	writeJSON(w, http.StatusOK, proto.InventoryResponse{Accepted: true})
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func writeJSONError(w http.ResponseWriter, code int, errCode, msg string) {
	writeJSON(w, code, proto.ErrorResponse{Error: errCode, Code: errCode, Message: msg})
}

// agentReleaseDownload serves an agent binary from the configured releases
// directory. It is PSK-authenticated (router middleware) and limited to
// entries that the manager has indexed; arbitrary path access is refused.
func (s *Server) agentReleaseDownload(w http.ResponseWriter, r *http.Request) {
	if s.Releases == nil || !s.Releases.Enabled() {
		writeJSONError(w, http.StatusNotFound, "not_found", "releases not configured")
		return
	}
	version := chi.URLParam(r, "version")
	filename := chi.URLParam(r, "filename")
	if version == "" || filename == "" || strings.ContainsAny(version, "/\\") || strings.ContainsAny(filename, "/\\") {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid path")
		return
	}
	entry, ok := s.Releases.Lookup(version, filename)
	if !ok {
		writeJSONError(w, http.StatusNotFound, "not_found", "release not found")
		return
	}
	f, err := s.Releases.Open(entry)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "open_failed", err.Error())
		return
	}
	defer f.Close()
	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Length", fmt.Sprintf("%d", entry.Size))
	w.Header().Set("X-JMW-Release-Version", entry.Version)
	w.Header().Set("X-JMW-Release-SHA256", entry.SHA256)
	w.Header().Set("Content-Disposition", "attachment; filename=\""+entry.Filename+"\"")
	_, _ = io.Copy(w, f)
}
