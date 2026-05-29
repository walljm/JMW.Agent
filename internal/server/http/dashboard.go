package httpsrv

import (
	"encoding/json"
	"log/slog"
	"net/http"
	"sort"
	"strings"
	"time"

	"github.com/go-chi/chi/v5"

	"github.com/walljm/jmwagent/internal/server/store"
)

func (s *Server) dashboardGet(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	approved, _ := s.Store.ListAgents(ctx, store.AgentStatusApproved)
	pending, _ := s.Store.ListAgents(ctx, store.AgentStatusPending)
	events, _ := s.Store.ListEvents(ctx, 25)
	csrf := s.ensureCSRF(w, r)

	// Agent online/stale + recently-stale (online → offline within last hour).
	now := time.Now()
	online, stale := 0, 0
	var recentlyStale []*store.Agent
	for _, a := range approved {
		if a.LastHeartbeatAt == nil {
			stale++
			continue
		}
		gap := now.Sub(*a.LastHeartbeatAt)
		switch {
		case gap < 2*time.Minute:
			online++
		default:
			stale++
			if gap < time.Hour {
				recentlyStale = append(recentlyStale, a)
			}
		}
	}
	// Newest stale first.
	sort.SliceStable(recentlyStale, func(i, j int) bool {
		return recentlyStale[i].LastHeartbeatAt.After(*recentlyStale[j].LastHeartbeatAt)
	})

	// Aggregate KPIs (one round trip each, all cheap).
	// Device stats are scoped to monitored networks when any exist.
	monCount, _ := s.Store.MonitoredNetworkCount(ctx)
	var devStats store.DeviceStats
	if monCount > 0 {
		devStats, _ = s.Store.DeviceStatsForMonitored(ctx)
	} else {
		devStats, _ = s.Store.DeviceStats(ctx)
	}
	conStats, _ := s.Store.ContainersSummary(ctx)
	alertStats, _ := s.Store.AlertStats(ctx)
	dbSize, _ := s.Store.DBSize(ctx)

	// Top-N panels.
	topSources, _ := s.Store.TopEventSources(ctx, 24*time.Hour, 5)
	recentDevices, _ := s.Store.ListRecentDevices(ctx, 5)

	// New-network count for attention card.
	discoveredNetworks, _ := s.Store.DiscoveredNetworkCount(ctx)

	// Pending preview (first 3) — surfaced inline so admins don't have to
	// click through if there's nothing else demanding attention.
	pendingPreview := pending
	if len(pendingPreview) > 3 {
		pendingPreview = pendingPreview[:3]
	}

	// Server self-health.
	uptime := time.Since(s.StartedAt).Round(time.Second)

	agentDeviceIDs, _ := s.Store.AgentPrimaryDeviceIDs(ctx)

	s.render(w, r, "dashboard.html", map[string]any{
		"CSRFToken":          csrf,
		"Title":              "Dashboard",
		"Active":             "dashboard",
		"Approved":           approved,
		"Pending":            pending,
		"PendingPreview":     pendingPreview,
		"Events":             events,
		"OnlineCount":        online,
		"StaleCount":         stale,
		"PendingCount":       len(pending),
		"RecentlyStale":      recentlyStale,
		"DeviceStats":        devStats,
		"ContainerStats":     conStats,
		"AlertStats":         alertStats,
		"TopSources":         topSources,
		"RecentDevices":      recentDevices,
		"Uptime":             uptime.String(),
		"DBSize":             dbSize,
		"IngestCount":        s.IngestCount(),
		"DiscoveredNetworks": discoveredNetworks,
		"AgentDeviceIDs":     agentDeviceIDs,
	})
}

func (s *Server) agentsList(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	agents, _ := s.Store.ListAgents(ctx, store.AgentStatusApproved)
	pending, _ := s.Store.ListAgents(ctx, store.AgentStatusPending)
	tags, _ := s.Store.ListTagsForTargets(ctx, store.TagTargetAgent)
	agentDeviceIDs, _ := s.Store.AgentPrimaryDeviceIDs(ctx)
	csrf := s.ensureCSRF(w, r)
	s.render(w, r, "agents.html", map[string]any{
		"CSRFToken":      csrf,
		"Title":          "Agents",
		"Active":         "agents",
		"Agents":         agents,
		"Pending":        pending,
		"PendingCount":   len(pending),
		"Tags":           tags,
		"AgentDeviceIDs": agentDeviceIDs,
	})
}

// agentDetail redirects /agents/{id} to the canonical entity URL at
// /devices/{interfaceID}. The merged device detail page renders all known
// data about the entity (discovery + agent inventory + metrics).
func (s *Server) agentDetail(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	a, err := s.Store.GetAgent(r.Context(), id)
	if err != nil || a == nil {
		http.NotFound(w, r)
		return
	}
	devID, _ := s.Store.PrimaryDeviceIDForAgent(r.Context(), id)
	if devID == "" {
		// Agent has registered but no interfaces have been recorded yet
		// (no heartbeat / discovery processed). Send them somewhere useful.
		http.Redirect(w, r, "/agents", http.StatusSeeOther)
		return
	}
	http.Redirect(w, r, "/devices/"+devID, http.StatusSeeOther)
}

// agentEdit handles POST /agents/{id}/edit — updates description (notes)
// and tags for a managed agent.
func (s *Server) agentEdit(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	a, err := s.Store.GetAgent(r.Context(), id)
	if err != nil || a == nil {
		http.NotFound(w, r)
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	notes := strings.TrimSpace(r.FormValue("description"))
	if len(notes) > 2000 {
		notes = notes[:2000]
	}
	tags := store.ParseTagInput(r.FormValue("tags"))
	if err := s.Store.UpdateAgentNotes(r.Context(), id, notes); err != nil {
		slog.Error("update agent notes failed", "handler", "agentEdit", "agent_id", id, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	if err := s.Store.SetTagsForTarget(r.Context(), store.TagTargetAgent, id, tags); err != nil {
		slog.Error("set agent tags failed", "handler", "agentEdit", "agent_id", id, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	devID, _ := s.Store.PrimaryDeviceIDForAgent(r.Context(), id)
	if devID != "" {
		http.Redirect(w, r, "/devices/"+devID, http.StatusSeeOther)
		return
	}
	http.Redirect(w, r, "/agents/"+id, http.StatusSeeOther)
}

func (s *Server) pendingList(w http.ResponseWriter, r *http.Request) {
	pending, _ := s.Store.ListAgents(r.Context(), store.AgentStatusPending)
	csrf := s.ensureCSRF(w, r)
	s.render(w, r, "pending.html", map[string]any{
		"CSRFToken": csrf,
		"Title":     "Pending Approvals",
		"Active":    "pending",
		"Pending":   pending,
	})
}

func (s *Server) eventsList(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	events, _ := s.Store.ListEvents(ctx, 250)
	agents, _ := s.Store.ListAgents(ctx, store.AgentStatusApproved)
	agentDeviceIDs, _ := s.Store.AgentPrimaryDeviceIDs(ctx)
	agentNames := make(map[string]string, len(agents))
	for _, a := range agents {
		agentNames[a.ID] = a.Hostname
	}
	s.render(w, r, "events.html", map[string]any{
		"Title":          "Event Log",
		"Active":         "events",
		"Events":         events,
		"AgentNames":     agentNames,
		"AgentDeviceIDs": agentDeviceIDs,
	})
}

func (s *Server) agentApprove(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	user := userFrom(r)
	approver := ""
	if user != nil {
		approver = user.Username
	}
	if err := s.Store.ApproveAgent(r.Context(), id, approver); err != nil {
		slog.Error("approve agent failed", "handler", "agentApprove", "agent_id", id, "err", err)
		http.Error(w, "internal error", http.StatusBadRequest)
		return
	}
	a, _ := s.Store.GetAgent(r.Context(), id)
	host := id
	if a != nil {
		host = a.Hostname
	}
	_ = s.Store.LogEvent(r.Context(), &store.Event{
		Type: "agent.approved", Severity: store.SeverityInfo,
		SourceKind: "agent", SourceID: id,
		Summary: "Agent approved: " + host,
		Detail:  map[string]any{"by": approver},
	})
	http.Redirect(w, r, "/pending", http.StatusSeeOther)
}

func (s *Server) agentDeregister(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	if err := s.Store.DeregisterAgent(r.Context(), id); err != nil {
		slog.Error("deregister agent failed", "handler", "agentDeregister", "agent_id", id, "err", err)
		http.Error(w, "internal error", http.StatusBadRequest)
		return
	}
	_ = s.Store.LogEvent(r.Context(), &store.Event{
		Type: "agent.deregistered", Severity: store.SeverityWarning,
		SourceKind: "agent", SourceID: id,
		Summary: "Agent deregistered",
	})
	http.Redirect(w, r, "/agents", http.StatusSeeOther)
}

func (s *Server) uiAgentMetrics(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	since := time.Now().UTC().Add(-1 * time.Hour)
	if v := r.URL.Query().Get("since"); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			since = time.Now().UTC().Add(-d)
		}
	}
	snaps, err := s.Store.SnapshotsSince(r.Context(), id, since)
	if err != nil {
		slog.Error("snapshots since failed", "handler", "uiAgentMetrics", "agent_id", id, "err", err)
		writeJSONError(w, http.StatusInternalServerError, "db_error", "internal error")
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"snapshots": snaps,
	})
}
