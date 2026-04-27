package httpsrv

import (
	"encoding/json"
	"net/http"
	"time"

	"github.com/go-chi/chi/v5"

	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

func (s *Server) dashboardGet(w http.ResponseWriter, r *http.Request) {
	approved, _ := s.Store.ListAgents(r.Context(), store.AgentStatusApproved)
	pending, _ := s.Store.ListAgents(r.Context(), store.AgentStatusPending)
	events, _ := s.Store.ListEvents(r.Context(), 25)
	csrf := s.ensureCSRF(w, r)
	online := 0
	stale := 0
	for _, a := range approved {
		if a.LastHeartbeatAt != nil && time.Since(*a.LastHeartbeatAt) < 2*time.Minute {
			online++
		} else {
			stale++
		}
	}
	s.render(w, r, "dashboard.html", map[string]any{
		"CSRFToken":    csrf,
		"Title":        "Dashboard",
		"Active":       "dashboard",
		"Approved":     approved,
		"Pending":      pending,
		"Events":       events,
		"OnlineCount":  online,
		"StaleCount":   stale,
		"PendingCount": len(pending),
	})
}

func (s *Server) clientsList(w http.ResponseWriter, r *http.Request) {
	agents, _ := s.Store.ListAgents(r.Context(), store.AgentStatusApproved)
	csrf := s.ensureCSRF(w, r)
	s.render(w, r, "clients.html", map[string]any{
		"CSRFToken": csrf,
		"Title":     "Clients",
		"Active":    "clients",
		"Agents":    agents,
	})
}

func (s *Server) clientDetail(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	a, err := s.Store.GetAgent(r.Context(), id)
	if err != nil || a == nil {
		http.NotFound(w, r)
		return
	}
	latest, _ := s.Store.LatestSnapshot(r.Context(), id)
	invJSON, invAt, _ := s.Store.GetAgentInventory(r.Context(), id)
	var inv *proto.Inventory
	if invJSON != "" {
		var parsed proto.Inventory
		if err := json.Unmarshal([]byte(invJSON), &parsed); err == nil {
			inv = &parsed
		}
	}
	csrf := s.ensureCSRF(w, r)
	s.render(w, r, "client_detail.html", map[string]any{
		"CSRFToken":     csrf,
		"Title":         a.Hostname,
		"Active":        "clients",
		"Agent":         a,
		"Latest":        latest,
		"Inventory":     inv,
		"InventoryAt":   invAt,
		"HasInventory":  inv != nil,
	})
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
	events, _ := s.Store.ListEvents(r.Context(), 250)
	s.render(w, r, "events.html", map[string]any{
		"Title":  "Event Log",
		"Active": "events",
		"Events": events,
	})
}

func (s *Server) clientApprove(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	user := userFrom(r)
	approver := ""
	if user != nil {
		approver = user.Username
	}
	if err := s.Store.ApproveAgent(r.Context(), id, approver); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
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

func (s *Server) clientDeregister(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	if err := s.Store.DeregisterAgent(r.Context(), id); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	_ = s.Store.LogEvent(r.Context(), &store.Event{
		Type: "agent.deregistered", Severity: store.SeverityWarning,
		SourceKind: "agent", SourceID: id,
		Summary: "Agent deregistered",
	})
	http.Redirect(w, r, "/clients", http.StatusSeeOther)
}

func (s *Server) uiClientMetrics(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	since := time.Now().UTC().Add(-1 * time.Hour)
	if v := r.URL.Query().Get("since"); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			since = time.Now().UTC().Add(-d)
		}
	}
	snaps, err := s.Store.SnapshotsSince(r.Context(), id, since)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"snapshots": snaps,
	})
}
