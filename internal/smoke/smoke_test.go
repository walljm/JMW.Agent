// Package smoke provides an end-to-end smoke test: start server in-process,
// register an agent, send a heartbeat + a metric snapshot, and verify the data
// landed in the database. Run with `go test ./internal/smoke -v`.
package smoke

import (
	"context"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"
	"time"

	"github.com/walljm/jmwagent/internal/server/config"
	httpsrv "github.com/walljm/jmwagent/internal/server/http"
	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

func TestSmokeAgentLifecycle(t *testing.T) {
	tmp := t.TempDir()
	cfg := &config.Config{
		Addr:                 ":0",
		DataDir:              tmp,
		AgentPSK:             "test-psk-12345",
		SessionLifetimeHours: 24,
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	st, err := store.Open(ctx, filepath.Join(tmp, "test.db"))
	if err != nil {
		t.Fatalf("open store: %v", err)
	}
	defer st.Close()

	srv, err := httpsrv.New(cfg, st, "")
	if err != nil {
		t.Fatalf("http server: %v", err)
	}

	ts := httptest.NewServer(srv.Router())
	defer ts.Close()

	agentID := "smoke-agent-001"

	// Register
	regBody := proto.RegisterRequest{
		AgentID:           agentID,
		Hostname:          "smokehost",
		OS:                "linux",
		Arch:              "amd64",
		Version:           "test",
		EnabledSubsystems: []string{"metrics"},
	}
	var regResp proto.RegisterResponse
	if err := postJSON(ts.URL+"/api/v1/agent/register", cfg.AgentPSK, regBody, &regResp); err != nil {
		t.Fatalf("register: %v", err)
	}
	if regResp.Status != store.AgentStatusPending {
		t.Fatalf("expected pending, got %q", regResp.Status)
	}

	// Approve via store directly.
	if err := st.ApproveAgent(ctx, agentID, "test"); err != nil {
		t.Fatalf("approve: %v", err)
	}

	// Heartbeat
	var hbResp proto.HeartbeatResponse
	if err := postJSON(ts.URL+"/api/v1/agent/heartbeat", cfg.AgentPSK,
		proto.HeartbeatRequest{AgentID: agentID, Version: "test", SentAt: time.Now()}, &hbResp); err != nil {
		t.Fatalf("heartbeat: %v", err)
	}
	if !hbResp.Approved {
		t.Fatalf("expected approved, got %+v", hbResp)
	}

	// Metrics
	now := time.Now().UTC()
	var mResp proto.MetricsResponse
	if err := postJSON(ts.URL+"/api/v1/agent/metrics", cfg.AgentPSK,
		proto.MetricsRequest{
			AgentID: agentID,
			Snapshots: []proto.MetricSnapshot{{
				Timestamp:     now,
				CPUPercent:    42.5,
				MemUsedBytes:  1_000_000,
				MemTotalBytes: 2_000_000,
				UptimeSeconds: 3600,
			}},
		}, &mResp); err != nil {
		t.Fatalf("metrics: %v", err)
	}
	if mResp.Accepted != 1 {
		t.Fatalf("expected accepted=1, got %d", mResp.Accepted)
	}

	latest, err := st.LatestSnapshot(ctx, agentID)
	if err != nil || latest == nil {
		t.Fatalf("read latest: err=%v latest=%v", err, latest)
	}
	if latest.CPUPercent < 42 || latest.CPUPercent > 43 {
		t.Fatalf("expected ~42.5 cpu, got %v", latest.CPUPercent)
	}

	// Discoveries
	var dResp proto.DiscoveryResponse
	if err := postJSON(ts.URL+"/api/v1/agent/discoveries", cfg.AgentPSK,
		proto.DiscoveryRequest{
			AgentID: agentID,
			Sightings: []proto.Sighting{
				{IP: "10.0.0.5", MAC: "aa:bb:cc:dd:ee:ff", Method: "arp", SeenAt: now},
			},
		}, &dResp); err != nil {
		t.Fatalf("discoveries: %v", err)
	}
	if dResp.Accepted != 1 {
		t.Fatalf("expected accepted=1, got %d", dResp.Accepted)
	}
	devs, err := st.ListDevices(ctx)
	if err != nil || len(devs) != 1 {
		t.Fatalf("list devices: err=%v len=%d", err, len(devs))
	}
	if devs[0].MAC != "aa:bb:cc:dd:ee:ff" {
		t.Fatalf("unexpected device mac: %q", devs[0].MAC)
	}

	// Pending+events sanity
	if events, err := st.ListEvents(ctx, 10); err != nil || len(events) == 0 {
		t.Fatalf("events: err=%v len=%d", err, len(events))
	}
}

func postJSON(url, psk string, body any, out any) error {
	return doRequest(http.MethodPost, url, psk, body, out)
}
