package httpsrv

import (
	"encoding/json"
	"net/http"
	"strings"

	"github.com/go-chi/chi/v5"

	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// containersList renders the global container list across all hosts,
// filterable by state, compose project, and free-text name/image search.
func (s *Server) containersList(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	filter := store.ContainerFilter{
		AgentID:        strings.TrimSpace(q.Get("agent")),
		State:          strings.TrimSpace(q.Get("state")),
		ComposeProject: strings.TrimSpace(q.Get("project")),
		Search:         strings.TrimSpace(q.Get("q")),
	}
	cs, _ := s.Store.ListContainers(r.Context(), filter)

	// Build a hostname map so the table can render host links cheaply.
	agents, _ := s.Store.ListAgents(r.Context(), "")
	names := make(map[string]string, len(agents))
	for _, a := range agents {
		names[a.ID] = a.Hostname
	}

	// Distinct compose projects across the current result set, for the filter
	// dropdown. Collected from the (already-loaded) rows so we don't issue
	// another query.
	projSet := map[string]struct{}{}
	for _, c := range cs {
		if c.ComposeProject != "" {
			projSet[c.ComposeProject] = struct{}{}
		}
	}
	projects := make([]string, 0, len(projSet))
	for p := range projSet {
		projects = append(projects, p)
	}

	stats, _ := s.Store.ContainersSummary(r.Context())

	s.render(w, r, "containers.html", map[string]any{
		"Title":      "Containers",
		"Active":     "containers",
		"Containers": cs,
		"AgentNames": names,
		"Projects":   projects,
		"Filter":     filter,
		"Stats":      stats,
		"States":     []string{"running", "exited", "paused", "restarting", "created", "dead"},
	})
}

// containerDetail renders the rich detail view for one container.
func (s *Server) containerDetail(w http.ResponseWriter, r *http.Request) {
	agentID := chi.URLParam(r, "agentID")
	containerID := chi.URLParam(r, "containerID")
	c, err := s.Store.GetContainer(r.Context(), agentID, containerID)
	if err != nil || c == nil {
		http.NotFound(w, r)
		return
	}
	var rec *proto.DockerContainer
	if c.RecordJSON != "" {
		var parsed proto.DockerContainer
		if err := json.Unmarshal([]byte(c.RecordJSON), &parsed); err == nil {
			rec = &parsed
		}
	}
	a, _ := s.Store.GetAgent(r.Context(), agentID)
	eng, _ := s.Store.GetDockerEngine(r.Context(), agentID)
	var engRec *proto.DockerEngine
	if eng != nil && eng.RecordJSON != "" {
		var parsed proto.DockerEngine
		if err := json.Unmarshal([]byte(eng.RecordJSON), &parsed); err == nil {
			engRec = &parsed
		}
	}
	s.render(w, r, "container_detail.html", map[string]any{
		"Title":     c.Name,
		"Active":    "containers",
		"Container": c,
		"Record":    rec,
		"Agent":     a,
		"Engine":    eng,
		"EngineRec": engRec,
	})
}
