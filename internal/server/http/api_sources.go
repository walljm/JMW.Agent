package httpsrv

import (
	"encoding/json"
	"log/slog"
	"net/http"

	"github.com/go-chi/chi/v5"
	"github.com/walljm/jmwagent/internal/server/store"
)

// sourcesAPI returns the JSON API for sources CRUD.
func (s *Server) sourcesAPIList(w http.ResponseWriter, r *http.Request) {
	sources, err := s.Store.ListSourcesForUI(r.Context())
	if err != nil {
		slog.Error("list sources failed", "handler", "sourcesAPIList", "err", err)
		writeJSONError(w, http.StatusInternalServerError, "db_error", "internal error")
		return
	}
	writeJSON(w, http.StatusOK, sources)
}

func (s *Server) sourcesAPIGet(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	src, err := s.Store.GetSource(r.Context(), id)
	if err != nil {
		slog.Error("get source failed", "handler", "sourcesAPIGet", "id", id, "err", err)
		writeJSONError(w, http.StatusInternalServerError, "db_error", "internal error")
		return
	}
	if src == nil {
		writeJSONError(w, http.StatusNotFound, "not_found", "source not found")
		return
	}
	writeJSON(w, http.StatusOK, src)
}

func (s *Server) sourcesAPICreate(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Name             string `json:"name"`
		Kind             string `json:"kind"`
		Enabled          bool   `json:"enabled"`
		AgentID          string `json:"agent_id,omitempty"`
		ConfigJSON       string `json:"config_json"`
		PollIntervalSecs *int   `json:"poll_interval_seconds,omitempty"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "invalid JSON")
		return
	}
	if req.Name == "" || req.Kind == "" {
		writeJSONError(w, http.StatusBadRequest, "bad_request", "name and kind required")
		return
	}

	src := &store.Source{
		Name:                req.Name,
		Kind:                req.Kind,
		Enabled:             req.Enabled,
		AgentID:             req.AgentID,
		ConfigJSON:          req.ConfigJSON,
		PollIntervalSeconds: req.PollIntervalSecs,
	}
	if err := s.Store.CreateSource(r.Context(), src, nil); err != nil {
		slog.Error("create source failed", "handler", "sourcesAPICreate", "err", err)
		writeJSONError(w, http.StatusInternalServerError, "db_error", "internal error")
		return
	}
	writeJSON(w, http.StatusCreated, map[string]string{"id": src.ID})
}

func (s *Server) sourcesAPIDelete(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	if err := s.Store.DeleteSource(r.Context(), id); err != nil {
		slog.Error("delete source failed", "handler", "sourcesAPIDelete", "id", id, "err", err)
		writeJSONError(w, http.StatusInternalServerError, "db_error", "internal error")
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
