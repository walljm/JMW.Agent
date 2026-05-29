package httpsrv

import (
	"crypto/rand"
	"encoding/hex"
	"net/http"
	"time"

	"github.com/walljm/jmwagent/internal/server/store"
)

// loginGet renders the login (or first-boot setup) page.
func (s *Server) loginGet(w http.ResponseWriter, r *http.Request) {
	n, err := s.Store.CountUsers(r.Context())
	if err != nil {
		http.Error(w, "db error", http.StatusInternalServerError)
		return
	}
	csrf := s.ensureCSRF(w, r)
	if n == 0 {
		s.render(w, r, "setup.html", map[string]any{"CSRFToken": csrf})
		return
	}
	s.render(w, r, "login.html", map[string]any{
		"CSRFToken": csrf,
		"Error":     r.URL.Query().Get("err"),
	})
}

// loginPost authenticates and creates a session.
func (s *Server) loginPost(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	username := r.FormValue("username")
	password := r.FormValue("password")
	user, err := s.Store.AuthenticateUser(r.Context(), username, password)
	if err != nil {
		http.Redirect(w, r, "/login?err=invalid", http.StatusSeeOther)
		return
	}
	sessionLifetime, _ := s.Store.GetSessionLifetime(r.Context())
	sess, err := s.Store.CreateSession(r.Context(), user.ID, sessionLifetime)
	if err != nil {
		http.Error(w, "session error", http.StatusInternalServerError)
		return
	}
	setSessionCookie(w, sess.ID, sess.ExpiresAt)
	_ = s.Store.LogEvent(r.Context(), &store.Event{
		Type: "auth.login", Severity: store.SeverityInfo,
		SourceKind: "user", SourceID: user.Username,
		Summary: "User logged in",
	})
	http.Redirect(w, r, "/", http.StatusSeeOther)
}

// setupPost handles first-boot user creation.
func (s *Server) setupPost(w http.ResponseWriter, r *http.Request) {
	n, err := s.Store.CountUsers(r.Context())
	if err != nil {
		http.Error(w, "db error", http.StatusInternalServerError)
		return
	}
	if n != 0 {
		http.Redirect(w, r, "/login", http.StatusSeeOther)
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	username := r.FormValue("username")
	password := r.FormValue("password")
	confirm := r.FormValue("confirm")
	if password == "" || password != confirm {
		http.Redirect(w, r, "/login?err=mismatch", http.StatusSeeOther)
		return
	}
	user, err := s.Store.CreateUser(r.Context(), username, password)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	sessionLifetime, _ := s.Store.GetSessionLifetime(r.Context())
	sess, err := s.Store.CreateSession(r.Context(), user.ID, sessionLifetime)
	if err != nil {
		http.Error(w, "session error", http.StatusInternalServerError)
		return
	}
	setSessionCookie(w, sess.ID, sess.ExpiresAt)
	_ = s.Store.LogEvent(r.Context(), &store.Event{
		Type: "auth.setup", Severity: store.SeverityInfo,
		SourceKind: "user", SourceID: user.Username,
		Summary: "Initial user created",
	})
	http.Redirect(w, r, "/", http.StatusSeeOther)
}

// logoutPost destroys the active session.
func (s *Server) logoutPost(w http.ResponseWriter, r *http.Request) {
	if c, err := r.Cookie(SessionCookieName); err == nil {
		_ = s.Store.DeleteSession(r.Context(), c.Value)
	}
	clearSessionCookie(w)
	http.Redirect(w, r, "/login", http.StatusSeeOther)
}

func setSessionCookie(w http.ResponseWriter, id string, exp time.Time) {
	http.SetCookie(w, &http.Cookie{
		Name:     SessionCookieName,
		Value:    id,
		Path:     "/",
		Expires:  exp,
		HttpOnly: true,
		Secure:   true,
		SameSite: http.SameSiteLaxMode,
	})
}

func clearSessionCookie(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{
		Name:     SessionCookieName,
		Value:    "",
		Path:     "/",
		MaxAge:   -1,
		HttpOnly: true,
		Secure:   true,
		SameSite: http.SameSiteLaxMode,
	})
}

// ensureCSRF guarantees the response has a CSRF cookie and returns its value.
func (s *Server) ensureCSRF(w http.ResponseWriter, r *http.Request) string {
	if c, err := r.Cookie(CSRFCookieName); err == nil && c.Value != "" {
		return c.Value
	}
	b := make([]byte, 32)
	_, _ = rand.Read(b)
	tok := hex.EncodeToString(b)
	http.SetCookie(w, &http.Cookie{
		Name:     CSRFCookieName,
		Value:    tok,
		Path:     "/",
		HttpOnly: false, // CSRF cookie must be readable by JS to mirror in header
		Secure:   true,
		SameSite: http.SameSiteLaxMode,
	})
	return tok
}
