package httpsrv

import (
	"encoding/json"
	"net/http"
	"strconv"
	"strings"

	"github.com/go-chi/chi/v5"

	"github.com/walljm/jmwagent/internal/server/store"
)

func (s *Server) alertsList(w http.ResponseWriter, r *http.Request) {
	rules, _ := s.Store.ListAlertRules(r.Context())
	firings, _ := s.Store.ListFirings(r.Context(), 50)
	channels, _ := s.Store.ListChannels(r.Context())
	agents, _ := s.Store.ListAgents(r.Context(), store.AgentStatusApproved)
	csrf := s.ensureCSRF(w, r)

	// Build agent lookup for firing summaries.
	agentMap := map[string]string{}
	for _, a := range agents {
		agentMap[a.ID] = a.Hostname
	}
	ruleMap := map[int64]string{}
	for _, ru := range rules {
		ruleMap[ru.ID] = ru.Name
	}

	s.render(w, r, "alerts.html", map[string]any{
		"CSRFToken": csrf,
		"Title":     "Alerts",
		"Active":    "alerts",
		"Rules":     rules,
		"Firings":   firings,
		"Channels":  channels,
		"Agents":    agents,
		"AgentMap":  agentMap,
		"RuleMap":   ruleMap,
	})
}

func (s *Server) alertCreate(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	threshold, _ := strconv.ParseFloat(r.FormValue("threshold"), 64)
	dur, _ := strconv.Atoi(r.FormValue("duration_seconds"))
	if dur <= 0 {
		dur = 60
	}
	rule := &store.AlertRule{
		Name:            strings.TrimSpace(r.FormValue("name")),
		Enabled:         true,
		Metric:          r.FormValue("metric"),
		Op:              r.FormValue("op"),
		Threshold:       threshold,
		DurationSeconds: dur,
		TargetKind:      "agent",
		TargetID:        r.FormValue("target_id"),
		Severity:        r.FormValue("severity"),
	}
	if rule.TargetID == "" {
		rule.TargetKind = "all"
	}
	if rule.Severity == "" {
		rule.Severity = store.SeverityWarning
	}
	if cid := r.FormValue("channel_id"); cid != "" {
		if id, err := strconv.ParseInt(cid, 10, 64); err == nil && id > 0 {
			rule.ChannelID = &id
		}
	}
	if rule.Name == "" || rule.Metric == "" || rule.Op == "" {
		http.Error(w, "name, metric, op required", http.StatusBadRequest)
		return
	}
	if err := s.Store.CreateAlertRule(r.Context(), rule); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

func (s *Server) alertDelete(w http.ResponseWriter, r *http.Request) {
	id, _ := strconv.ParseInt(chi.URLParam(r, "id"), 10, 64)
	if err := s.Store.DeleteAlertRule(r.Context(), id); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

func (s *Server) channelCreate(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	kind := r.FormValue("kind")
	cfg := map[string]any{}
	switch kind {
	case "webhook":
		cfg["url"] = r.FormValue("url")
	case "email":
		cfg["host"] = r.FormValue("host")
		cfg["port"], _ = strconv.Atoi(r.FormValue("port"))
		cfg["username"] = r.FormValue("username")
		cfg["password"] = r.FormValue("password")
		cfg["from"] = r.FormValue("from")
		cfg["to"] = r.FormValue("to")
		cfg["tls"] = r.FormValue("tls") == "on"
	default:
		http.Error(w, "unknown channel kind", http.StatusBadRequest)
		return
	}
	ch := &store.NotificationChannel{
		Name:    r.FormValue("name"),
		Kind:    kind,
		Config:  cfg,
		Enabled: true,
	}
	if ch.Name == "" {
		http.Error(w, "name required", http.StatusBadRequest)
		return
	}
	if err := s.Store.CreateChannel(r.Context(), ch); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

func (s *Server) channelDelete(w http.ResponseWriter, r *http.Request) {
	id, _ := strconv.ParseInt(chi.URLParam(r, "id"), 10, 64)
	if err := s.Store.DeleteChannel(r.Context(), id); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

func (s *Server) devicesList(w http.ResponseWriter, r *http.Request) {
	devices, _ := s.Store.ListDevices(r.Context())
	agents, _ := s.Store.ListAgents(r.Context(), "")
	names := make(map[string]string, len(agents))
	for _, a := range agents {
		names[a.ID] = a.Hostname
	}
	s.render(w, r, "devices.html", map[string]any{
		"Title":      "Devices",
		"Active":     "devices",
		"Devices":    devices,
		"AgentNames": names,
	})
}
// MDNSProfile mirrors the JSON blob stored in devices.services_json.
type MDNSProfile struct {
	Hostname string            `json:"hostname,omitempty"`
	Services []string          `json:"services,omitempty"`
	TXT      map[string]string `json:"txt,omitempty"`
}

func (s *Server) deviceDetail(w http.ResponseWriter, r *http.Request) {
	id := strings.ToLower(chi.URLParam(r, "id"))
	d, err := s.Store.GetDevice(r.Context(), id)
	if err != nil || d == nil {
		http.NotFound(w, r)
		return
	}
	sightings, _ := s.Store.ListSightings(r.Context(), id, 200)
	aliases, _ := s.Store.ListHostnames(r.Context(), id)
	agents, _ := s.Store.ListAgents(r.Context(), "")
	names := make(map[string]string, len(agents))
	for _, a := range agents {
		names[a.ID] = a.Hostname
	}
	var profile *MDNSProfile
	if d.ServicesJSON != "" {
		var p MDNSProfile
		if err := json.Unmarshal([]byte(d.ServicesJSON), &p); err == nil {
			profile = &p
		}
	}
	managedHostname := ""
	if d.AgentID != "" {
		managedHostname = names[d.AgentID]
	}
	s.render(w, r, "device_detail.html", map[string]any{
		"Title":           "Device " + d.Hostname,
		"Active":          "devices",
		"Device":          d,
		"Sightings":       sightings,
		"Aliases":         aliases,
		"AgentNames":      names,
		"MDNS":            profile,
		"ManagedHostname": managedHostname,
	})
}