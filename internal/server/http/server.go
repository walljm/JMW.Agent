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
	"time"

	"github.com/go-chi/chi/v5"
	chimw "github.com/go-chi/chi/v5/middleware"

	"github.com/walljm/jmwagent/internal/server/alerting"
	"github.com/walljm/jmwagent/internal/server/config"
	"github.com/walljm/jmwagent/internal/server/store"
)

//go:embed templates/*.html templates/partials/*.html
var templateFS embed.FS

//go:embed static
var staticFS embed.FS

// Server is the dashboard + API server.
type Server struct {
	Config        *config.Config
	Store         *store.Store
	templates     *template.Template
	ServerCertSHA string

	heartbeatInterval int
}

// New constructs a server.
func New(cfg *config.Config, st *store.Store, certSHA string) (*Server, error) {
	tmpl, err := loadTemplates()
	if err != nil {
		return nil, err
	}
	return &Server{
		Config:            cfg,
		Store:             st,
		templates:         tmpl,
		ServerCertSHA:     certSHA,
		heartbeatInterval: 30,
	}, nil
}

func loadTemplates() (*template.Template, error) {
	root := template.New("").Funcs(template.FuncMap{
		"humanBytes": humanBytes,
		"shortTime":  shortTime,
		"sinceShort": sinceShort,
		"divf":       divf,
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
		ar.Post("/register", s.agentRegister)
		ar.Post("/heartbeat", s.agentHeartbeat)
		ar.Post("/metrics", s.agentMetrics)
		ar.Post("/discoveries", s.agentDiscoveries)
		ar.Post("/inventory", s.agentInventory)
	})

	// Dashboard (browser, session-authenticated).
	r.Group(func(pr chi.Router) {
		pr.Use(s.requireSession)
		pr.With(s.requireCSRF).Post("/logout", s.logoutPost)

		pr.Get("/", s.dashboardGet)
		pr.Get("/clients", s.clientsList)
		pr.Get("/clients/{id}", s.clientDetail)
		pr.Get("/pending", s.pendingList)
		pr.Get("/events", s.eventsList)
		pr.Get("/alerts", s.alertsList)
		pr.Get("/devices", s.devicesList)
		pr.Get("/devices/{id}", s.deviceDetail)
		pr.Get("/containers", s.containersList)
		pr.Get("/containers/{agentID}/{containerID}", s.containerDetail)

		// Dashboard mutations (require both session + CSRF).
		pr.With(s.requireCSRF).Post("/clients/{id}/approve", s.clientApprove)
		pr.With(s.requireCSRF).Post("/clients/{id}/deregister", s.clientDeregister)
		pr.With(s.requireCSRF).Post("/alerts", s.alertCreate)
		pr.With(s.requireCSRF).Post("/alerts/{id}/delete", s.alertDelete)
		pr.With(s.requireCSRF).Post("/channels", s.channelCreate)
		pr.With(s.requireCSRF).Post("/channels/{id}/delete", s.channelDelete)

		// Dashboard JSON for charts.
		pr.Get("/api/v1/ui/clients/{id}/metrics", s.uiClientMetrics)
	})

	return r
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
