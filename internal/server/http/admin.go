package httpsrv

import (
	"fmt"
	"net/http"
	"strconv"
	"time"

	"github.com/walljm/jmwagent/internal/server/terrain"
	"github.com/walljm/jmwagent/internal/shared/duration"
)

// durationKeys are config keys whose values should be displayed as
// human-friendly duration strings (e.g. "1d 3h" instead of "90000").
var durationKeys = []string{
	"agent.heartbeat_interval_secs",
	"agent.discovery_interval_secs",
	"agent.inventory_interval_secs",
	"terrain.poll_interval_secs",
	"auth.session_lifetime_hours",
	"retention.raw_metrics",
	"retention.rollup_5min",
	"retention.rollup_hourly",
	"retention.rollup_daily",
	"retention.removed_containers",
	"retention.stale_observations",
}

// adminGet renders the admin settings page.
func (s *Server) adminGet(w http.ResponseWriter, r *http.Request) {
	cfg, err := s.Store.GetAllConfig(r.Context())
	if err != nil {
		http.Error(w, "db error", http.StatusInternalServerError)
		return
	}
	normalizeDurationDisplay(cfg)
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

// normalizeDurationDisplay re-formats known duration config values into the
// canonical human-friendly form for display. Handles legacy numeric values
// that haven't been migrated yet.
func normalizeDurationDisplay(cfg map[string]string) {
	for _, key := range durationKeys {
		raw, ok := cfg[key]
		if !ok || raw == "" {
			continue
		}
		if d, err := parseDurationDisplay(raw); err == nil && d > 0 {
			cfg[key] = duration.Format(d)
		}
	}
}

// parseDurationDisplay is a lenient parser for display purposes. It accepts
// human-friendly ("1d 3h"), Go duration ("48h"), and legacy plain integers
// (treated as seconds).
func parseDurationDisplay(s string) (time.Duration, error) {
	if d, err := duration.Parse(s); err == nil {
		return d, nil
	}
	if d, err := time.ParseDuration(s); err == nil {
		return d, nil
	}
	if n, err := strconv.Atoi(s); err == nil && n > 0 {
		return time.Duration(n) * time.Second, nil
	}
	return 0, fmt.Errorf("unrecognized duration %q", s)
}

// adminIntervalsPost saves the three agent collection intervals.
func (s *Server) adminIntervalsPost(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Redirect(w, r, "/admin?err=bad_form", http.StatusSeeOther)
		return
	}

	type kv struct{ key, val string }
	entries := []kv{
		{"agent.heartbeat_interval_secs", r.FormValue("heartbeat_interval")},
		{"agent.discovery_interval_secs", r.FormValue("discovery_interval")},
		{"agent.inventory_interval_secs", r.FormValue("inventory_interval")},
	}
	for i, e := range entries {
		d, err := duration.Parse(e.val)
		if err != nil || d <= 0 {
			http.Redirect(w, r, fmt.Sprintf("/admin?err=invalid_%s", e.key), http.StatusSeeOther)
			return
		}
		entries[i].val = duration.Format(d)
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

	type kv struct{ key, val string }
	var toSave []kv
	for _, key := range retentionKeys {
		formKey := key[len("retention."):]
		val := r.FormValue(formKey)
		if val == "" {
			continue
		}
		d, err := duration.Parse(val)
		if err != nil || d <= 0 {
			http.Redirect(w, r, fmt.Sprintf("/admin?err=invalid_%s", key), http.StatusSeeOther)
			return
		}
		toSave = append(toSave, kv{key, duration.Format(d)})
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
	d, err := duration.Parse(r.FormValue("session_lifetime"))
	if err != nil || d <= 0 {
		http.Redirect(w, r, "/admin?err=invalid_session_lifetime", http.StatusSeeOther)
		return
	}
	if err := s.Store.SetConfig(r.Context(), "auth.session_lifetime_hours", duration.Format(d)); err != nil {
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

	pollVal := r.FormValue("terrain_poll_interval")

	d, err := duration.Parse(pollVal)
	if err != nil || d <= 0 {
		http.Redirect(w, r, "/admin?err=invalid_terrain_poll_interval", http.StatusSeeOther)
		return
	}

	type kv struct{ key, val string }
	plainFields := []kv{
		{"terrain.url", r.FormValue("terrain_url")},
		{"terrain.username", r.FormValue("terrain_username")},
		{"terrain.poll_interval_secs", duration.Format(d)},
	}
	for _, e := range plainFields {
		if err := s.Store.SetConfig(r.Context(), e.key, e.val); err != nil {
			http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
			return
		}
	}

	// Only overwrite token/password when the user actually typed a new value.
	// Blank submission means "keep existing" — never wipe credentials silently.
	// Use SetConfigEncrypted to encrypt secrets with the DEK.
	if t := r.FormValue("terrain_token"); t != "" {
		if err := s.Store.SetConfigEncrypted(r.Context(), "terrain.token", t); err != nil {
			http.Redirect(w, r, "/admin?err=db_error", http.StatusSeeOther)
			return
		}
	}
	if p := r.FormValue("terrain_password"); p != "" {
		if err := s.Store.SetConfigEncrypted(r.Context(), "terrain.password", p); err != nil {
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
