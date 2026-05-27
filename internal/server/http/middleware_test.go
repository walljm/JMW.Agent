package httpsrv

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestAPIVersionHeader_OnAPIPath(t *testing.T) {
	handler := apiVersionHeader(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))

	paths := []string{
		"/api/v1/agent/heartbeat",
		"/api/v1/devices",
		"/api/v1/sources",
		"/api/whatever",
	}
	for _, path := range paths {
		req := httptest.NewRequest("GET", path, nil)
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)

		got := rec.Header().Get("X-API-Version")
		if got != "1" {
			t.Errorf("path %q: X-API-Version = %q, want \"1\"", path, got)
		}
	}
}

func TestAPIVersionHeader_AbsentOnNonAPIPath(t *testing.T) {
	handler := apiVersionHeader(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))

	paths := []string{
		"/",
		"/healthz",
		"/login",
		"/static/style.css",
		"/dashboard",
		"/devices",
	}
	for _, path := range paths {
		req := httptest.NewRequest("GET", path, nil)
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)

		got := rec.Header().Get("X-API-Version")
		if got != "" {
			t.Errorf("path %q: X-API-Version = %q, want empty (non-API path)", path, got)
		}
	}
}

func TestAPIVersionHeader_PreservesDownstreamHeaders(t *testing.T) {
	handler := apiVersionHeader(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Custom", "hello")
		w.WriteHeader(http.StatusOK)
	}))

	req := httptest.NewRequest("GET", "/api/v1/test", nil)
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Header().Get("X-API-Version") != "1" {
		t.Error("missing X-API-Version on API path")
	}
	if rec.Header().Get("X-Custom") != "hello" {
		t.Error("downstream header lost")
	}
}
