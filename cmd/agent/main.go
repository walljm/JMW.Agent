// Command jmw-agent runs the local monitoring agent.
package main

import (
	"context"
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
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

func main() {
	var (
		cfgPath  = flag.String("config", "agent.toml", "path to agent config file")
		serverU  = flag.String("server", "", "server URL (overrides config)")
		psk      = flag.String("psk", "", "agent PSK (overrides config)")
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
	if *psk != "" {
		cfg.PSK = *psk
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

	// Register loop until approved.
	for {
		regResp, err := cli.Register(ctx, &proto.RegisterRequest{
			AgentID:           id,
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
		if regResp.Status == "approved" {
			slog.Info("agent approved")
			break
		}
		slog.Info("awaiting approval", "msg", regResp.Message)
		if !sleepCtx(ctx, 30*time.Second) {
			return ctx.Err()
		}
	}

	// Heartbeat + metrics loop.
	tick := time.NewTicker(time.Duration(cfg.IntervalSecs) * time.Second)
	defer tick.Stop()

	invInterval := time.Duration(cfg.InventoryIntervalSecs) * time.Second
	invTick := time.NewTicker(invInterval)
	defer invTick.Stop()

	// Send first sample immediately.
	if err := sendSample(ctx, cli, id); err != nil {
		slog.Warn("initial sample", "err", err)
	}
	if err := sendHeartbeat(ctx, cli, id); err != nil {
		slog.Warn("initial heartbeat", "err", err)
	}
	// Send first inventory immediately (best-effort).
	go func() {
		slog.Info("initial inventory: collecting")
		t0 := time.Now()
		if err := sendInventory(ctx, cli, id, cfg.IncludePackages); err != nil {
			slog.Warn("initial inventory", "err", err, "elapsed", time.Since(t0))
			return
		}
		slog.Info("initial inventory: sent", "elapsed", time.Since(t0))
	}()
	// Send first discovery scan immediately (best-effort). On a fresh
	// deploy the agent's behaviour may have changed (new probes, new
	// hostname rules, etc.) and we want the server to see those results
	// without waiting a full heartbeat interval.
	go func() {
		slog.Info("initial discovery: scanning")
		t0 := time.Now()
		if err := sendDiscoveries(ctx, cli, id); err != nil {
			slog.Warn("initial discovery", "err", err, "elapsed", time.Since(t0))
			return
		}
		slog.Info("initial discovery: sent", "elapsed", time.Since(t0))
	}()

	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-tick.C:
			if err := sendHeartbeat(ctx, cli, id); err != nil {
				slog.Warn("heartbeat", "err", err)
			}
			if err := sendSample(ctx, cli, id); err != nil {
				slog.Warn("metrics", "err", err)
			}
			if err := sendDiscoveries(ctx, cli, id); err != nil {
				slog.Warn("discoveries", "err", err)
			}
		case <-invTick.C:
			if err := sendInventory(ctx, cli, id, cfg.IncludePackages); err != nil {
				slog.Warn("inventory", "err", err)
			}
		}
	}
}

func sendHeartbeat(ctx context.Context, cli *transport.Client, id string) error {
	resp, err := cli.Heartbeat(ctx, &proto.HeartbeatRequest{
		AgentID:           id,
		Version:           version.Version,
		EnabledSubsystems: []string{"metrics"},
		SentAt:            time.Now().UTC(),
	})
	if err != nil {
		return err
	}
	if resp != nil && resp.Update != nil {
		// Inside containers (JMW_HOST_ROOT set) the binary lives on a
		// read-only image layer; the in-process updater can't rewrite it.
		// Image-level updates are handled out-of-band by Watchtower or a
		// manual `docker pull`, so just acknowledge and move on.
		if hostfs.Active() {
			slog.Debug("update offered but skipped (containerized; handled by image updater)", "version", resp.Update.Version)
			return nil
		}
		slog.Info("server offered update", "version", resp.Update.Version)
		if err := updater.Apply(ctx, cli, resp.Update); err != nil {
			slog.Warn("update apply failed; will retry on next heartbeat", "err", err)
		}
		// On success Apply does not return (process is replaced).
	}
	return nil
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

func sendInventory(ctx context.Context, cli *transport.Client, id string, includePackages bool) error {
	inv := collect.Inventory(ctx, includePackages)
	_, err := cli.Inventory(ctx, &proto.InventoryRequest{
		AgentID:   id,
		Inventory: inv,
	})
	return err
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
