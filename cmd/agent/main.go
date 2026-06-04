// Command jmw-agent runs the local monitoring agent.
package main

import (
	"context"
	"flag"
	"fmt"
	"log/slog"
	"math/rand/v2"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/agent/collect"
	agentcfg "github.com/walljm/jmwagent/internal/agent/config"
	"github.com/walljm/jmwagent/internal/agent/discover"
	"github.com/walljm/jmwagent/internal/agent/hostfs"
	"github.com/walljm/jmwagent/internal/agent/identity"
	"github.com/walljm/jmwagent/internal/agent/transport"
	"github.com/walljm/jmwagent/internal/agent/updater"
	"github.com/walljm/jmwagent/internal/shared/proto"
	"github.com/walljm/jmwagent/internal/shared/version"
)

type managedIdentity struct {
	path string
	mu   sync.RWMutex
	id   string
}

func newManagedIdentity(path, id string) *managedIdentity {
	return &managedIdentity{path: path, id: id}
}

func (m *managedIdentity) Get() string {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.id
}

func (m *managedIdentity) Adopt(canonicalID string) error {
	if canonicalID == "" {
		return nil
	}
	m.mu.RLock()
	current := m.id
	m.mu.RUnlock()
	if canonicalID == current {
		return nil
	}
	if err := identity.WriteID(m.path, canonicalID); err != nil {
		return err
	}
	m.mu.Lock()
	m.id = canonicalID
	m.mu.Unlock()
	slog.Info("agent identity adopted", "old_id", current, "canonical_id", canonicalID)
	return nil
}

func main() {
	var (
		cfgPath  = flag.String("config", "agent.toml", "path to agent config file")
		serverU  = flag.String("server", "", "server URL (overrides config)")
		idPath   = flag.String("id-file", "", "agent identity file (overrides config)")
		interval = flag.Int("interval", 0, "metrics interval seconds (overrides config)")
		showVer  = flag.Bool("version", false, "print version and exit")
	)
	flag.Parse()

	if *showVer {
		fmt.Println(version.Version)
		return
	}

	logger := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)

	cfg, err := agentcfg.Load(*cfgPath)
	if err != nil {
		slog.Error("load config", "err", err)
		os.Exit(1)
	}
	if *serverU != "" {
		cfg.ServerURL = *serverU
	}
	if v := os.Getenv("JMW_PSK"); v != "" {
		cfg.PSK = v
	}
	if *idPath != "" {
		cfg.IDFile = *idPath
	}
	if *interval > 0 {
		cfg.IntervalSecs = *interval
	}
	if cfg.ServerURL == "" || cfg.PSK == "" {
		slog.Error("server URL and PSK are required (config or flags)")
		os.Exit(1)
	}
	if cfg.UpdatePublicKey == "" {
		cfg.UpdatePublicKey = version.UpdatePublicKey
	}

	id, err := identity.EnsureID(cfg.IDFile)
	if err != nil {
		slog.Error("ensure id", "err", err)
		os.Exit(1)
	}
	slog.Info("agent starting", "id", id, "server", cfg.ServerURL, "version", version.Version)

	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	if err := run(ctx, cfg, *cfgPath, id); err != nil {
		slog.Error("agent exit", "err", err)
		os.Exit(1)
	}
}

func run(ctx context.Context, cfg *agentcfg.Config, cfgPath, id string) error {
	cli := transport.New(cfg.ServerURL, cfg.PSK, cfg.PinnedSHA)
	agentID := newManagedIdentity(cfg.IDFile, id)

	// Registration loop — retries until the server approves this agent.
	var regResp *proto.RegisterResponse
	for {
		var err error
		regResp, err = cli.Register(ctx, &proto.RegisterRequest{
			AgentID:           agentID.Get(),
			Hostname:          collect.Hostname(),
			OS:                collect.OS(),
			Arch:              collect.Arch(),
			Version:           version.Version,
			EnabledSubsystems: []string{"metrics"},
		})
		if err != nil {
			slog.Warn("register failed; retrying", "err", err)
			if !sleepCtx(ctx, 15*time.Second) {
				return ctx.Err()
			}
			continue
		}
		// Pin server cert on first successful contact.
		if cfg.PinnedSHA == "" && regResp.ServerCertSHA256 != "" {
			cfg.PinnedSHA = regResp.ServerCertSHA256
			_ = agentcfg.Save(cfgPath, cfg)
			cli = transport.New(cfg.ServerURL, cfg.PSK, cfg.PinnedSHA)
			slog.Info("server cert pinned", "sha256", cfg.PinnedSHA)
		}
		if err := agentID.Adopt(regResp.CanonicalAgentID); err != nil {
			slog.Warn("adopt canonical agent id failed", "canonical_id", regResp.CanonicalAgentID, "err", err)
		}
		if regResp.Status == "approved" {
			slog.Info("agent approved")
			break
		}
		slog.Info("awaiting approval", "msg", regResp.Message)
		if !sleepCtx(ctx, 30*time.Second) {
			return ctx.Err()
		}
	}

	// Apply server-pushed intervals, falling back to local config when the
	// server returns 0 (unset or old server version).
	heartbeatSecs := cfg.IntervalSecs
	discoverySecs := cfg.DiscoveryIntervalSecs
	inventorySecs := cfg.InventoryIntervalSecs
	if regResp.HeartbeatInterval > 0 {
		heartbeatSecs = regResp.HeartbeatInterval
	}
	if regResp.DiscoveryIntervalSecs > 0 {
		discoverySecs = regResp.DiscoveryIntervalSecs
	}
	if regResp.InventoryIntervalSecs > 0 {
		inventorySecs = regResp.InventoryIntervalSecs
	}
	slog.Info("intervals",
		"heartbeat_secs", heartbeatSecs,
		"discovery_secs", discoverySecs,
		"inventory_secs", inventorySecs)

	metricTick := time.NewTicker(jitteredInterval(heartbeatSecs))
	defer metricTick.Stop()

	discoveryTick := time.NewTicker(time.Duration(discoverySecs) * time.Second)
	defer discoveryTick.Stop()

	invTick := time.NewTicker(time.Duration(inventorySecs) * time.Second)
	defer invTick.Stop()

	// Send first inventory before metrics so a reinstalled agent can adopt the
	// canonical ID before it writes fresh samples under a temporary ID.
	slog.Info("initial inventory: collecting")
	t0 := time.Now()
	if resp, err := sendInventory(ctx, cli, agentID.Get(), cfg.IncludePackages); err != nil {
		slog.Warn("initial inventory", "err", err, "elapsed", time.Since(t0))
	} else {
		if err := agentID.Adopt(resp.CanonicalAgentID); err != nil {
			slog.Warn("adopt canonical agent id failed", "canonical_id", resp.CanonicalAgentID, "err", err)
		}
		slog.Info("initial inventory: sent", "elapsed", time.Since(t0))
	}

	// Send first sample immediately.
	if err := sendSample(ctx, cli, agentID.Get()); err != nil {
		slog.Warn("initial sample", "err", err)
	}
	if resp, err := sendHeartbeatFull(ctx, cli, agentID.Get(), cfg.UpdatePublicKey); err != nil {
		slog.Warn("initial heartbeat", "err", err)
	} else if resp != nil {
		if err := agentID.Adopt(resp.CanonicalAgentID); err != nil {
			slog.Warn("adopt canonical agent id failed", "canonical_id", resp.CanonicalAgentID, "err", err)
		}
	}
	// Send first discovery scan immediately (best-effort).
	go func() {
		slog.Info("initial discovery: scanning")
		t0 := time.Now()
		if err := sendDiscoveries(ctx, cli, agentID.Get()); err != nil {
			slog.Warn("initial discovery", "err", err, "elapsed", time.Since(t0))
			return
		}
		slog.Info("initial discovery: sent", "elapsed", time.Since(t0))
	}()

	for {
		select {
		case <-ctx.Done():
			return ctx.Err()

		case <-metricTick.C:
			resp, err := sendHeartbeatFull(ctx, cli, agentID.Get(), cfg.UpdatePublicKey)
			if err != nil {
				slog.Warn("heartbeat", "err", err)
			} else if resp != nil {
				if err := agentID.Adopt(resp.CanonicalAgentID); err != nil {
					slog.Warn("adopt canonical agent id failed", "canonical_id", resp.CanonicalAgentID, "err", err)
				}
				// Re-arm tickers if the server changed any interval.
				if resp.NextHeartbeatIn > 0 && resp.NextHeartbeatIn != heartbeatSecs {
					heartbeatSecs = resp.NextHeartbeatIn
					metricTick.Reset(jitteredInterval(heartbeatSecs))
					slog.Info("heartbeat interval updated", "secs", heartbeatSecs)
				}
				if resp.DiscoveryIntervalSecs > 0 && resp.DiscoveryIntervalSecs != discoverySecs {
					discoverySecs = resp.DiscoveryIntervalSecs
					discoveryTick.Reset(time.Duration(discoverySecs) * time.Second)
					slog.Info("discovery interval updated", "secs", discoverySecs)
				}
				if resp.InventoryIntervalSecs > 0 && resp.InventoryIntervalSecs != inventorySecs {
					inventorySecs = resp.InventoryIntervalSecs
					invTick.Reset(time.Duration(inventorySecs) * time.Second)
					slog.Info("inventory interval updated", "secs", inventorySecs)
				}
			}
			if err := sendSample(ctx, cli, agentID.Get()); err != nil {
				slog.Warn("metrics", "err", err)
			}

		case <-discoveryTick.C:
			if err := sendDiscoveries(ctx, cli, agentID.Get()); err != nil {
				slog.Warn("discoveries", "err", err)
			}

		case <-invTick.C:
			resp, err := sendInventory(ctx, cli, agentID.Get(), cfg.IncludePackages)
			if err != nil {
				slog.Warn("inventory", "err", err)
			} else if resp != nil {
				if err := agentID.Adopt(resp.CanonicalAgentID); err != nil {
					slog.Warn("adopt canonical agent id failed", "canonical_id", resp.CanonicalAgentID, "err", err)
				}
			}
		}
	}
}

// sendHeartbeatFull sends a heartbeat and returns the full response so the
// caller can inspect interval directives and update offers.
func sendHeartbeatFull(ctx context.Context, cli *transport.Client, id, updatePublicKey string) (*proto.HeartbeatResponse, error) {
	resp, err := cli.Heartbeat(ctx, &proto.HeartbeatRequest{
		AgentID:           id,
		Version:           version.Version,
		EnabledSubsystems: []string{"metrics"},
		SentAt:            time.Now().UTC(),
	})
	if err != nil {
		return nil, err
	}
	if resp != nil && resp.Update != nil {
		if hostfs.Active() || os.Getenv("SUPERVISOR_TOKEN") != "" {
			slog.Debug("update offered but skipped (containerized; handled by image or Supervisor updater)", "version", resp.Update.Version)
			return resp, nil
		}
		slog.Info("server offered update", "version", resp.Update.Version)
		if err := updater.Apply(ctx, cli, resp.Update, updatePublicKey); err != nil {
			slog.Warn("update apply failed; will retry on next heartbeat", "err", err)
		}
		// On success Apply does not return (process is replaced).
	}
	return resp, nil
}

func sendHeartbeat(ctx context.Context, cli *transport.Client, id, updatePublicKey string) error {
	_, err := sendHeartbeatFull(ctx, cli, id, updatePublicKey)
	return err
}

func sendSample(ctx context.Context, cli *transport.Client, id string) error {
	sn := collect.Snapshot()
	_, err := cli.Metrics(ctx, &proto.MetricsRequest{
		AgentID:   id,
		Snapshots: []proto.MetricSnapshot{sn},
	})
	return err
}

func sendDiscoveries(ctx context.Context, cli *transport.Client, id string) error {
	raw := discover.ScanARP()
	if len(raw) == 0 {
		return nil
	}
	out := make([]proto.Sighting, 0, len(raw))
	for _, s := range raw {
		out = append(out, proto.Sighting{
			IP:              s.IP,
			MAC:             s.MAC,
			Hostname:        s.Hostname,
			Vendor:          s.Vendor,
			Kind:            s.Kind,
			Method:          s.Method,
			SeenAt:          s.SeenAt,
			Services:        s.Services,
			TXT:             s.TXT,
			HostnameSources: s.HostnameSources,
			Probes:          s.Probes,
		})
	}
	_, err := cli.Discoveries(ctx, &proto.DiscoveryRequest{
		AgentID:   id,
		Sightings: out,
		Network:   discover.NetworkContext(),
	})
	return err
}

func sendInventory(ctx context.Context, cli *transport.Client, id string, includePackages bool) (*proto.InventoryResponse, error) {
	inv := collect.Inventory(ctx, includePackages)
	resp, err := cli.Inventory(ctx, &proto.InventoryRequest{
		AgentID:   id,
		Inventory: inv,
	})
	return resp, err
}

// jitteredInterval returns base ± 25% so agents spread their heartbeats
// naturally without significantly changing their effective average rate.
func jitteredInterval(secs int) time.Duration {
	base := time.Duration(secs) * time.Second
	quarter := base / 4
	return base - quarter/2 + time.Duration(rand.Int64N(int64(quarter)))
}

func sleepCtx(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return true
	}
}
