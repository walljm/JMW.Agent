// Package httpsrv contains the HTTP handlers, middleware, and routing for the dashboard + API.
package httpsrv

import (
	"context"
	"net/http"

	"github.com/walljm/jmwagent/internal/server/store"
)

type ctxKey int

const (
	ctxUser ctxKey = iota
	ctxSession
)

// SessionCookieName is the cookie that carries the session token.
const SessionCookieName = "jmw_session"

// CSRFCookieName is the cookie that carries the CSRF token.
const CSRFCookieName = "jmw_csrf"

// CSRFHeaderName is the header expected to mirror the CSRF cookie on writes.
const CSRFHeaderName = "X-CSRF-Token"

// requireSession is middleware that loads & validates the session.
func (s *Server) requireSession(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		c, err := r.Cookie(SessionCookieName)
		if err != nil || c.Value == "" {
			redirectToLogin(w, r)
			return
		}
		sess, err := s.Store.GetSession(r.Context(), c.Value)
		if err != nil || sess == nil {
			redirectToLogin(w, r)
			return
		}
		user, err := s.Store.GetUserByID(r.Context(), sess.UserID)
		if err != nil || user == nil {
			redirectToLogin(w, r)
			return
		}
		_ = s.Store.TouchSession(r.Context(), sess.ID)
		ctx := context.WithValue(r.Context(), ctxUser, user)
		ctx = context.WithValue(ctx, ctxSession, sess)
		next.ServeHTTP(w, r.WithContext(ctx))
	})
}

// requireCSRF validates the CSRF token on unsafe methods.
func (s *Server) requireCSRF(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet, http.MethodHead, http.MethodOptions:
			next.ServeHTTP(w, r)
			return
		}
		c, err := r.Cookie(CSRFCookieName)
		if err != nil || c.Value == "" {
			http.Error(w, "missing csrf cookie", http.StatusForbidden)
			return
		}
		hdr := r.Header.Get(CSRFHeaderName)
		if hdr == "" {
			hdr = r.FormValue("csrf_token")
		}
		if hdr == "" || hdr != c.Value {
			http.Error(w, "csrf mismatch", http.StatusForbidden)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// userFrom returns the authenticated user (or nil).
func userFrom(r *http.Request) *store.User {
	v, _ := r.Context().Value(ctxUser).(*store.User)
	return v
}

// sessionFrom returns the active session (or nil).
func sessionFrom(r *http.Request) *store.Session {
	v, _ := r.Context().Value(ctxSession).(*store.Session)
	return v
}

func redirectToLogin(w http.ResponseWriter, r *http.Request) {
	if r.Header.Get("Accept") == "application/json" || r.URL.Path == "/api" || hasPrefix(r.URL.Path, "/api/") {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	http.Redirect(w, r, "/login", http.StatusSeeOther)
}

func hasPrefix(s, p string) bool {
	return len(s) >= len(p) && s[:len(p)] == p
}
