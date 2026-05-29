package httpsrv

import (
	"log/slog"
	"net/http"
	"strings"

	"github.com/go-chi/chi/v5"

	"github.com/walljm/jmwagent/internal/server/store"
)

func (s *Server) networksList(w http.ResponseWriter, r *http.Request) {
	networks, _ := s.Store.ListNetworks(r.Context(), "")
	csrf := s.ensureCSRF(w, r)
	s.render(w, r, "networks.html", map[string]any{
		"CSRFToken": csrf,
		"Title":     "Networks",
		"Active":    "networks",
		"Networks":  networks,
	})
}

func (s *Server) networkEdit(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}

	name := strings.TrimSpace(r.FormValue("name"))
	status := strings.TrimSpace(r.FormValue("status"))

	// Validate status.
	switch status {
	case store.NetworkStatusDiscovered, store.NetworkStatusMonitored, store.NetworkStatusIgnored:
		// ok
	default:
		http.Error(w, "invalid status", http.StatusBadRequest)
		return
	}

	if err := s.Store.UpdateNetworkName(r.Context(), id, name); err != nil {
		slog.Error("update network name failed", "handler", "networkEdit", "id", id, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	if err := s.Store.UpdateNetworkStatus(r.Context(), id, status); err != nil {
		slog.Error("update network status failed", "handler", "networkEdit", "id", id, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}

	http.Redirect(w, r, "/networks", http.StatusSeeOther)
}
