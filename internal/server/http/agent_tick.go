package httpsrv

import (
	"encoding/json"
	"log/slog"
	"net/http"
	"time"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/pipeline/adapters"
	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// agentTick handles the coalesced agent tick endpoint that replaces the
// separate /metrics, /discoveries, and /inventory endpoints.
func (s *Server) agentTick(w http.ResponseWriter, r *http.Request) {
	var req proto.TickRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid JSON")
		return
	}
	if req.AgentID == "" {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "agent_id required")
		return
	}
	if req.Metrics != nil && len(req.Metrics.Snapshots) > 1000 {
		writeJSONError(w, http.StatusRequestEntityTooLarge, "payload_too_large", "too many snapshots")
		return
	}
	if req.Discoveries != nil && len(req.Discoveries.Sightings) > 10000 {
		writeJSONError(w, http.StatusRequestEntityTooLarge, "payload_too_large", "too many sightings")
		return
	}
	a, err := s.Store.GetAgent(r.Context(), req.AgentID)
	if err != nil {
		slog.Error("get agent failed", "handler", "agentTick", "agent_id", req.AgentID, "err", err)
		writeJSONError(w, http.StatusInternalServerError, "db_error", "internal error")
		return
	}
	if a == nil || a.Status != store.AgentStatusApproved {
		writeJSONError(w, http.StatusForbidden, "not_approved", "agent not approved")
		return
	}

	// Touch heartbeat.
	_ = s.Store.TouchAgentHeartbeat(r.Context(), req.AgentID, req.Version, nil)

	ctx := r.Context()
	srcID, srcErr := s.Store.EnsureAgentSource(ctx, req.AgentID, a.Hostname)
	if srcErr != nil {
		slog.Error("ensure agent source failed", "handler", "agentTick", "agent_id", req.AgentID, "err", srcErr)
		writeJSONError(w, http.StatusInternalServerError, "db_error", "internal error")
		return
	}

	// Process metrics section.
	if req.Metrics != nil && len(req.Metrics.Snapshots) > 0 {
		payload := &adapters.AgentMetricsPayload{
			AgentID:   req.AgentID,
			Snapshots: req.Metrics.Snapshots,
		}
		if _, pErr := s.Ingestor.Ingest(ctx, "agent-metrics", srcID, payload); pErr != nil {
			slog.Warn("tick: metrics ingest failed", "agent", req.AgentID, "err", pErr)
		}
	}

	// Process discoveries section.
	if req.Discoveries != nil {
		if len(req.Discoveries.Sightings) > 0 {
			req.Discoveries.AgentID = req.AgentID
			if _, pErr := s.Ingestor.Ingest(ctx, "agent-discovery", srcID, req.Discoveries); pErr != nil {
				slog.Warn("tick: discovery ingest failed", "agent", req.AgentID, "err", pErr)
			}
		}
		// Upsert the reporting network from gateway MAC + CIDR + SSID. Device-to-
		// network membership is computed by CIDR match in ListNetworks, so only
		// the networks row itself needs to exist.
		if req.Discoveries.Network != nil && req.Discoveries.Network.GatewayMAC != "" {
			if _, err := s.Store.UpsertNetwork(ctx,
				req.Discoveries.Network.GatewayMAC,
				req.Discoveries.Network.CIDR,
				req.Discoveries.Network.SSID,
				time.Now().UTC()); err != nil {
				slog.Warn("tick: upsert network failed", "agent", req.AgentID, "gw_mac", req.Discoveries.Network.GatewayMAC, "err", err)
			}
		}
	}

	// Process inventory section.
	if req.Inventory != nil {
		collected := req.Inventory.Inventory.CollectedAt
		if collected.IsZero() {
			collected = time.Now().UTC()
		}
		blob, _ := json.Marshal(req.Inventory.Inventory)
		primaryIP := pickPrimaryIP(req.Inventory.Inventory)
		_ = s.Store.SetAgentInventory(ctx, req.AgentID, string(blob), primaryIP, collected)

		if _, pErr := s.Ingestor.Ingest(ctx, "agent-inventory", srcID, &req.Inventory.Inventory); pErr != nil {
			slog.Warn("tick: inventory ingest failed", "agent", req.AgentID, "err", pErr)
		}

		// Link the agent to its hardware so Device.AgentID is populated on the
		// device detail page (hydrateDevices joins systems on hardware_id).
		if mac := pickPrimaryMAC(req.Inventory.Inventory); mac != "" {
			_ = s.Store.EnsureAgentSystem(ctx, req.AgentID, pipeline.NormalizeMAC(mac), a.Hostname, a.OS)
		}

		// Temperature + battery snapshots.
		if len(req.Inventory.Inventory.Hardware.Temperatures) > 0 {
			temps := make([]store.TempSnapshot, len(req.Inventory.Inventory.Hardware.Temperatures))
			for i, t := range req.Inventory.Inventory.Hardware.Temperatures {
				temps[i] = store.TempSnapshot{Sensor: t.Name, Celsius: t.Celsius}
			}
			_ = s.Store.InsertTemperatureSnapshots(ctx, req.AgentID, collected, temps)
		}
		if ch := req.Inventory.Inventory.Chassis; ch != nil && ch.Battery != nil {
			_ = s.Store.InsertBatterySnapshot(ctx, req.AgentID, collected,
				ch.Battery.ChargePercent, ch.Battery.State, ch.Battery.HealthPercent)
		}
	}

	resp := proto.TickResponse{Accepted: true}
	writeJSON(w, http.StatusOK, resp)
}
