package httpsrv

import (
	"context"
	"embed"
	"errors"
	"fmt"
	"html/template"
	"io/fs"
	"log/slog"
	"net/http"
	"strings"
	"sync/atomic"
	"time"

	"github.com/go-chi/chi/v5"
	chimw "github.com/go-chi/chi/v5/middleware"

	"github.com/walljm/jmwagent/internal/server/alerting"
	"github.com/walljm/jmwagent/internal/server/config"
	"github.com/walljm/jmwagent/internal/server/infra"
	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/pipeline/adapters"
	"github.com/walljm/jmwagent/internal/server/releases"
	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/server/terrain"
	"github.com/walljm/jmwagent/internal/shared/version"
)

//go:embed templates/*.html templates/partials/*.html
var templateFS embed.FS

//go:embed static
var staticFS embed.FS

// Server is the dashboard + API server.
type Server struct {
	Config        *config.Config
	Store         *store.Store
	Terrain       *terrain.Poller
	Infra         *infra.Scanner
	Releases      *releases.Manager
	Ingestor      *pipeline.Ingestor
	templates     *template.Template
	ServerCertSHA string
	StartedAt     time.Time

	// ingestCount is the total number of agent API requests served since
	// process start (heartbeats + metrics + discoveries + inventory).
	// Read with atomic.LoadInt64; the dashboard derives a recent rate by
	// snapshotting at known intervals.
	ingestCount atomic.Int64
}

// New constructs a server. ctx is used only for DB reads during construction.
func New(ctx context.Context, cfg *config.Config, st *store.Store, certSHA string) (*Server, error) {
	tmpl, err := loadTemplates()
	if err != nil {
		return nil, err
	}

	// Load terrain connection config from DB. On first boot (DB keys all empty)
	// migrate any values from the legacy server.toml [terrain] section into the
	// DB so the admin UI reflects them immediately.
	terrainCfg, err := st.GetTerrainConfig(ctx)
	if err != nil {
		slog.Warn("read terrain config from db", "err", err)
	}
	if terrainCfg.URL == "" && cfg.Terrain.URL != "" {
		terrainCfg.URL = cfg.Terrain.URL
		terrainCfg.Token = cfg.Terrain.Token
		terrainCfg.Username = cfg.Terrain.Username
		terrainCfg.Password = cfg.Terrain.Password
		// Persist to DB so the admin page shows the migrated values.
		for k, v := range map[string]string{
			"terrain.url":      terrainCfg.URL,
			"terrain.token":    terrainCfg.Token,
			"terrain.username": terrainCfg.Username,
			"terrain.password": terrainCfg.Password,
		} {
			if err := st.SetConfig(ctx, k, v); err != nil {
				slog.Warn("terrain migration: persist to db failed", "key", k, "err", err)
			}
		}
		slog.Info("migrated terrain config from server.toml to database")
	}

	poller := terrain.New(terrain.Config{
		URL:      terrainCfg.URL,
		Token:    terrainCfg.Token,
		Username: terrainCfg.Username,
		Password: terrainCfg.Password,
	})

	// Legacy DeviceSink removed — entity pipeline is the sole write path.
	rel := releases.New(cfg.ReleasesDir)
	if err := rel.Scan(); err != nil {
		slog.Warn("initial releases scan failed", "dir", cfg.ReleasesDir, "err", err)
	}

	// Build the entity pipeline ingestor with all known adapters.
	ing := pipeline.NewIngestor(st,
		&adapters.AgentDiscovery{},
		&adapters.AgentInventory{},
		&adapters.AgentMetrics{Store: st},
		&adapters.TerrainDHCP{},
		&adapters.TerrainDNS{},
	)

	// Wire pipeline sink so terrain polls also feed entity pipeline.
	poller.SetPipelineSink(&terrainPipelineSink{ingestor: ing, store: st})

	return &Server{
		Config:        cfg,
		Store:         st,
		Terrain:       poller,
		Infra:         infra.New(st),
		Releases:      rel,
		Ingestor:      ing,
		templates:     tmpl,
		ServerCertSHA: certSHA,
		StartedAt:     time.Now().UTC(),
	}, nil
}

// terrainPipelineSink adapts *pipeline.Ingestor to terrain.PipelineSink.
type terrainPipelineSink struct {
	ingestor *pipeline.Ingestor
	store    *store.Store
}

func (t *terrainPipelineSink) IngestDHCP(ctx context.Context, status *terrain.DHCPStatus) error {
	srcID, err := t.store.EnsureTerrainSource(ctx)
	if err != nil {
		return err
	}
	_, err = t.ingestor.Ingest(ctx, "terrain-dhcp", srcID, status)
	return err
}

func (t *terrainPipelineSink) IngestDNS(ctx context.Context, records []terrain.DNSRecord) error {
	srcID, err := t.store.EnsureTerrainSource(ctx)
	if err != nil {
		return err
	}
	_, err = t.ingestor.Ingest(ctx, "terrain-dns", srcID, records)
	return err
}

// countIngest counts each agent-API request so the dashboard can show an
// ingestion rate. Cheap, lock-free; not persisted across restarts.
func (s *Server) countIngest(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		s.ingestCount.Add(1)
		next.ServeHTTP(w, r)
	})
}

// IngestCount returns the running total of agent API requests served.
func (s *Server) IngestCount() int64 { return s.ingestCount.Load() }

func loadTemplates() (*template.Template, error) {
	root := template.New("").Funcs(template.FuncMap{
		"humanBytes":    humanBytes,
		"humanDuration": humanDuration,
		"shortTime":     shortTime,
		"sinceShort":    sinceShort,
		"divf":          divf,
		"deref":         derefBool,
		"agentStatus":   agentStatus,
	})
	// First, parse all partials so layouts can reference them by name.
	partials, err := fs.Glob(templateFS, "templates/partials/*.html")
	if err != nil {
		return nil, err
	}
	if len(partials) > 0 {
		if _, err := root.ParseFS(templateFS, partials...); err != nil {
			return nil, err
		}
	}
	pages, err := fs.Glob(templateFS, "templates/*.html")
	if err != nil {
		return nil, err
	}
	if _, err := root.ParseFS(templateFS, pages...); err != nil {
		return nil, err
	}
	return root, nil
}

// Router returns the configured router.
func (s *Server) Router() http.Handler {
	r := chi.NewRouter()
	r.Use(chimw.RealIP)
	r.Use(chimw.Recoverer)
	r.Use(secHeaders)
	r.Use(apiVersionHeader)
	r.Use(chimw.Timeout(60 * time.Second))

	// Static assets.
	sub, _ := fs.Sub(staticFS, "static")
	r.Handle("/static/*", http.StripPrefix("/static/", http.FileServer(http.FS(sub))))

	// Public routes.
	r.Get("/healthz", func(w http.ResponseWriter, r *http.Request) { w.Write([]byte("ok")) })
	r.Get("/login", s.loginGet)
	r.With(s.requireCSRF).Post("/login", s.loginPost)
	r.With(s.requireCSRF).Post("/setup", s.setupPost)

	// Agent (machine-to-machine) API: PSK-authenticated, no session/CSRF.
	r.Route("/api/v1/agent", func(ar chi.Router) {
		ar.Use(s.requireAgentPSK)
		ar.Use(s.countIngest)
		ar.Post("/register", s.agentRegister)
		ar.Post("/heartbeat", s.agentHeartbeat)
		ar.Post("/metrics", s.agentMetrics)
		ar.Post("/discoveries", s.agentDiscoveries)
		ar.Post("/inventory", s.agentInventory)
		ar.Post("/tick", s.agentTick)
		ar.Get("/releases/{version}/{filename}", s.agentReleaseDownload)
	})

	// Dashboard (browser, session-authenticated).
	r.Group(func(pr chi.Router) {
		pr.Use(s.requireSession)
		pr.With(s.requireCSRF).Post("/logout", s.logoutPost)

		pr.Get("/", s.dashboardGet)
		pr.Get("/agents", s.agentsList)
		pr.Get("/agents/{id}", s.agentDetail)
		pr.Get("/pending", s.pendingList)
		pr.Get("/events", s.eventsList)
		pr.Get("/alerts", s.alertsList)
		pr.Get("/devices", s.devicesList)
		pr.Get("/devices/{id}", s.deviceDetail)
		pr.Get("/networks", s.networksList)
		pr.Get("/containers", s.containersList)
		pr.Get("/containers/{agentID}/{containerID}", s.containerDetail)
		pr.Get("/terrain", s.terrainGet)

		// Legacy redirects: /clients/* → /agents/* (bookmarks).
		pr.Get("/clients", redirectTo("/agents"))
		pr.Get("/clients/{id}", redirectPath("/clients", "/agents"))

		// Dashboard JSON for terrain.
		pr.Get("/api/v1/ui/terrain", s.uiTerrainStatus)

		// Dashboard mutations (require both session + CSRF).
		pr.With(s.requireCSRF).Post("/agents/{id}/approve", s.agentApprove)
		pr.With(s.requireCSRF).Post("/agents/{id}/deregister", s.agentDeregister)
		pr.With(s.requireCSRF).Post("/agents/{id}/edit", s.agentEdit)
		pr.With(s.requireCSRF).Post("/devices/{id}/edit", s.deviceEdit)
		pr.With(s.requireCSRF).Post("/networks/{id}/edit", s.networkEdit)
		pr.With(s.requireCSRF).Post("/alerts", s.alertCreate)
		pr.With(s.requireCSRF).Post("/alerts/{id}/delete", s.alertDelete)
		pr.With(s.requireCSRF).Post("/channels", s.channelCreate)
		pr.With(s.requireCSRF).Post("/channels/{id}/delete", s.channelDelete)

		// Dashboard JSON for charts.
		pr.Get("/api/v1/ui/agents/{id}/metrics", s.uiAgentMetrics)

		// Sources CRUD API (session-authenticated).
		pr.Get("/api/v1/ui/sources", s.sourcesAPIList)
		pr.Get("/api/v1/ui/sources/{id}", s.sourcesAPIGet)
		pr.With(s.requireCSRF).Post("/api/v1/ui/sources", s.sourcesAPICreate)
		pr.With(s.requireCSRF).Delete("/api/v1/ui/sources/{id}", s.sourcesAPIDelete)

		// Entity-based views.
		pr.Get("/api/v1/ui/hardware", s.hardwareListAPI)
		pr.Get("/api/v1/ui/hardware/{id}", s.hardwareDetailAPI)

		// Admin settings.
		pr.Get("/admin", s.adminGet)
		pr.With(s.requireCSRF).Post("/admin/intervals", s.adminIntervalsPost)
		pr.With(s.requireCSRF).Post("/admin/retention", s.adminRetentionPost)
		pr.With(s.requireCSRF).Post("/admin/session", s.adminSessionPost)
		pr.With(s.requireCSRF).Post("/admin/terrain", s.adminTerrainPost)
	})

	return r
}

// redirectTo returns a handler that issues a 308 permanent redirect to dest.
func redirectTo(dest string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, dest, http.StatusPermanentRedirect)
	}
}

// redirectPath returns a handler that rewrites the request path's old prefix
// to a new prefix and issues a 308 redirect. Used for /clients/* → /agents/*.
func redirectPath(oldPrefix, newPrefix string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p := r.URL.Path
		if strings.HasPrefix(p, oldPrefix) {
			p = newPrefix + strings.TrimPrefix(p, oldPrefix)
		}
		http.Redirect(w, r, p, http.StatusPermanentRedirect)
	}
}

// secHeaders sets baseline security headers on every response.
func secHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("Content-Security-Policy",
			"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'")
		next.ServeHTTP(w, r)
	})
}

// apiVersionHeader sets X-API-Version on all /api/ responses.
func apiVersionHeader(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasPrefix(r.URL.Path, "/api/") {
			w.Header().Set("X-API-Version", "1")
		}
		next.ServeHTTP(w, r)
	})
}

// requireAgentPSK validates the agent shared key.
func (s *Server) requireAgentPSK(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got := r.Header.Get("X-Agent-PSK")
		if got == "" {
			authz := r.Header.Get("Authorization")
			if strings.HasPrefix(authz, "Bearer ") {
				got = strings.TrimPrefix(authz, "Bearer ")
			}
		}
		if got == "" || got != s.Config.AgentPSK {
			http.Error(w, "agent psk required", http.StatusUnauthorized)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// render executes a named template.
func (s *Server) render(w http.ResponseWriter, r *http.Request, name string, data map[string]any) {
	if data == nil {
		data = map[string]any{}
	}
	if u := userFrom(r); u != nil {
		data["User"] = u
	}
	// Always available to every template.
	if _, ok := data["ServerVersion"]; !ok {
		data["ServerVersion"] = version.Version
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if err := s.templates.ExecuteTemplate(w, name, data); err != nil {
		slog.Error("template error", "tmpl", name, "err", err)
		http.Error(w, "template error", http.StatusInternalServerError)
	}
}

// StartBackground spawns periodic janitorial tasks.
func (s *Server) StartBackground(ctx context.Context) {
	go func() {
		t := time.NewTicker(15 * time.Minute)
		defer t.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-t.C:
				if n, err := s.Store.PurgeExpiredSessions(ctx); err == nil && n > 0 {
					slog.Info("purged expired sessions", "n", n)
				}
			}
		}
	}()
	go (&alerting.Evaluator{Store: s.Store, Interval: 30 * time.Second}).Run(ctx)
	go s.Terrain.Run(ctx, func() int {
		v, _ := s.Store.GetTerrainPollInterval(ctx)
		return v
	})
	go s.Infra.Run(ctx)
	go s.RunRollups(ctx)
	if s.Releases != nil && s.Releases.Enabled() {
		go func() {
			t := time.NewTicker(2 * time.Minute)
			defer t.Stop()
			for {
				select {
				case <-ctx.Done():
					return
				case <-t.C:
					if err := s.Releases.Scan(); err != nil {
						slog.Warn("releases rescan failed", "err", err)
					}
				}
			}
		}()
	}
}

// ErrNotFound is returned when an entity isn't found.
var ErrNotFound = errors.New("not found")

func humanBytes(n any) string {
	var v float64
	switch x := n.(type) {
	case uint64:
		v = float64(x)
	case int64:
		v = float64(x)
	case int:
		v = float64(x)
	case float64:
		v = x
	default:
		return fmt.Sprintf("%v", n)
	}
	const unit = 1024.0
	if v < unit {
		return fmt.Sprintf("%.0f B", v)
	}
	suffixes := []string{"KB", "MB", "GB", "TB", "PB"}
	i := -1
	for v >= unit && i < len(suffixes)-1 {
		v /= unit
		i++
	}
	return fmt.Sprintf("%.1f %s", v, suffixes[i])
}

func shortTime(t time.Time) string {
	if t.IsZero() {
		return "—"
	}
	return t.Local().Format("2006-01-02 15:04")
}

// divf divides any numeric template arg by a float divisor. Used by
// templates that need to render e.g. NanoCPUs as fractional CPUs.
func divf(num any, div float64) float64 {
	if div == 0 {
		return 0
	}
	switch x := num.(type) {
	case int64:
		return float64(x) / div
	case uint64:
		return float64(x) / div
	case int:
		return float64(x) / div
	case float64:
		return x / div
	default:
		return 0
	}
}

// humanDuration formats an integer second count as a compact "3d 4h 12m"
// string, picking the two largest non-zero units. Renders "<1m" for very
// short uptimes so we don't surface raw seconds in the UI.
func humanDuration(secs any) string {
	var s int64
	switch v := secs.(type) {
	case int64:
		s = v
	case int:
		s = int64(v)
	case uint64:
		s = int64(v)
	case float64:
		s = int64(v)
	default:
		return "—"
	}
	if s <= 0 {
		return "—"
	}
	days := s / 86400
	hours := (s % 86400) / 3600
	mins := (s % 3600) / 60
	switch {
	case days > 0:
		if hours > 0 {
			return fmt.Sprintf("%dd %dh", days, hours)
		}
		return fmt.Sprintf("%dd", days)
	case hours > 0:
		if mins > 0 {
			return fmt.Sprintf("%dh %dm", hours, mins)
		}
		return fmt.Sprintf("%dh", hours)
	case mins > 0:
		return fmt.Sprintf("%dm", mins)
	}
	return "<1m"
}

// derefBool dereferences a *bool for templates; nil renders as false.
func derefBool(b *bool) bool {
	if b == nil {
		return false
	}
	return *b
}

func sinceShort(t *time.Time) string {
	if t == nil || t.IsZero() {
		return "never"
	}
	d := time.Since(*t)
	switch {
	case d < time.Minute:
		return fmt.Sprintf("%ds ago", int(d.Seconds()))
	case d < time.Hour:
		return fmt.Sprintf("%dm ago", int(d.Minutes()))
	case d < 24*time.Hour:
		return fmt.Sprintf("%dh ago", int(d.Hours()))
	default:
		return fmt.Sprintf("%dd ago", int(d.Hours())/24)
	}
}

// agentStatus returns a one-word status keyword for an agent's last
// heartbeat: "online" (< 2 min), "stale" (< 1 hour), or "offline".
// Templates use this both for label text and as a CSS modifier suffix
// on .badge-status (e.g. .badge-status.online).
func agentStatus(t *time.Time) string {
	if t == nil || t.IsZero() {
		return "offline"
	}
	d := time.Since(*t)
	switch {
	case d < 2*time.Minute:
		return "online"
	case d < time.Hour:
		return "stale"
	default:
		return "offline"
	}
}
