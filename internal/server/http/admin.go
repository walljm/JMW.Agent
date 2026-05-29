package httpsrv

import (
	"fmt"
	"net/http"
	"strconv"
	"time"

	"github.com/walljm/jmwagent/internal/server/terrain"
)

// adminGet renders the admin settings page.
func (s *Server) adminGet(w http.ResponseWriter, r *http.Request) {
	cfg, err := s.Store.GetAllConfig(r.Context())
	if err != nil {
		http.Error(w, "db error", http.StatusInternalServerError)
		return
	}
	terrainStatus := s.Terrain.Status()
	csrf := s.ensureCSRF(w, r)
	s.render(w, r, "admin.html", map[string]any{
		"Active":        "admin",
		"Group":         "system",
		"CSRFToken":     csrf,
		"Config":        cfg,
		"TerrainStatus": terrainStatus,
		"Flash":         r.URL.Query().Get("flash"),
		"Error":         r.URL.Query().Get("err"),
	})
}

// adminIntervalsPost saves the three agent collection intervals.
func (s *Server) adminIntervalsPost(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Redirect(w, r, "/admin?err=bad_form", http.StatusSeeOther)
		return
	}

	// Validate all three fields before writing any of them.
	type kv struct{ key, val string }
	entries := []kv{
		{"agent.heartbeat_interval_secs", r.FormValue("heartbeat_interval_secs")},
		{"agent.discovery_interval_secs", r.FormValue("discovery_interval_secs")},
		{"agent.inventory_interval_secs", r.FormValue("inventory_interval_secs")},
	}
	for _, e := range entries {
		n, err := strconv.Atoi(e.val)
		if err != nil || n <= 0 {
			http.Redirect(w, r, fmt.Sprintf("/admin?err=invalid_%s", e.key), http.StatusSeeOther)
			return
		}
	}
	for _, e := range entries {
		if err := s.Store.SetConfig(r.Context(), e.key, e.val); err != nil {
			http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
			return
		}
	}

	http.Redirect(w, r, "/admin?flash=intervals_saved", http.StatusSeeOther)
}

// adminRetentionPost saves data retention window settings.
func (s *Server) adminRetentionPost(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Redirect(w, r, "/admin?err=bad_form", http.StatusSeeOther)
		return
	}

	retentionKeys := []string{
		"retention.raw_metrics",
		"retention.rollup_5min",
		"retention.rollup_hourly",
		"retention.rollup_daily",
		"retention.removed_containers",
		"retention.stale_observations",
	}

	// Build validated key→value pairs before writing anything.
	type kv struct{ key, val string }
	var toSave []kv
	for _, key := range retentionKeys {
		formKey := key[len("retention."):]
		val := r.FormValue(formKey)
		if val == "" {
			continue
		}
		d, err := time.ParseDuration(val)
		if err != nil || d <= 0 {
			http.Redirect(w, r, fmt.Sprintf("/admin?err=invalid_%s", key), http.StatusSeeOther)
			return
		}
		toSave = append(toSave, kv{key, val})
	}
	for _, e := range toSave {
		if err := s.Store.SetConfig(r.Context(), e.key, e.val); err != nil {
			http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
			return
		}
	}
	http.Redirect(w, r, "/admin?flash=retention_saved", http.StatusSeeOther)
}

// adminSessionPost saves the session lifetime.
func (s *Server) adminSessionPost(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Redirect(w, r, "/admin?err=bad_form", http.StatusSeeOther)
		return
	}
	hours, err := strconv.Atoi(r.FormValue("session_lifetime_hours"))
	if err != nil || hours <= 0 {
		http.Redirect(w, r, "/admin?err=invalid_session_lifetime", http.StatusSeeOther)
		return
	}
	if err := s.Store.SetConfig(r.Context(), "auth.session_lifetime_hours", strconv.Itoa(hours)); err != nil {
		http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
		return
	}
	http.Redirect(w, r, "/admin?flash=session_saved", http.StatusSeeOther)
}

// adminTerrainPost saves terrain connection settings and updates the running
// poller immediately so changes take effect without a restart.
func (s *Server) adminTerrainPost(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Redirect(w, r, "/admin?err=bad_form", http.StatusSeeOther)
		return
	}

	pollSecs := r.FormValue("terrain_poll_interval_secs")

	// Validate poll interval.
	n, err := strconv.Atoi(pollSecs)
	if err != nil || n <= 0 {
		http.Redirect(w, r, "/admin?err=invalid_terrain_poll_interval", http.StatusSeeOther)
		return
	}

	// Always save the non-secret fields (slice keeps order deterministic).
	type kv struct{ key, val string }
	plainFields := []kv{
		{"terrain.url", r.FormValue("terrain_url")},
		{"terrain.username", r.FormValue("terrain_username")},
		{"terrain.poll_interval_secs", strconv.Itoa(n)},
	}
	for _, e := range plainFields {
		if err := s.Store.SetConfig(r.Context(), e.key, e.val); err != nil {
			http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
			return
		}
	}

	// Only overwrite token/password when the user actually typed a new value.
	// Blank submission means "keep existing" — never wipe credentials silently.
	if t := r.FormValue("terrain_token"); t != "" {
		if err := s.Store.SetConfig(r.Context(), "terrain.token", t); err != nil {
			http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
			return
		}
	}
	if p := r.FormValue("terrain_password"); p != "" {
		if err := s.Store.SetConfig(r.Context(), "terrain.password", p); err != nil {
			http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
			return
		}
	}

	// Read back the full config (including any unchanged token/password) to
	// push a complete config to the running poller.
	terrainCfg, err := s.Store.GetTerrainConfig(r.Context())
	if err != nil {
		http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
		return
	}
	s.Terrain.SetConfig(terrain.Config{
		URL:      terrainCfg.URL,
		Token:    terrainCfg.Token,
		Username: terrainCfg.Username,
		Password: terrainCfg.Password,
	})

	http.Redirect(w, r, "/admin?flash=terrain_saved", http.StatusSeeOther)
}
