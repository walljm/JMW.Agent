package httpsrv

import (
	"context"
	"log/slog"
	"strconv"
	"time"

	"github.com/walljm/jmwagent/internal/shared/duration"
)

// RunRollups starts a background goroutine that periodically aggregates raw
// metric snapshots into rollup tables and prunes old data. Blocks until ctx
// is cancelled.
func (s *Server) RunRollups(ctx context.Context) {
	s.rollupOnce(ctx)

	ticker := time.NewTicker(5 * time.Minute)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			s.rollupOnce(ctx)
		}
	}
}

func (s *Server) rollupOnce(ctx context.Context) {
	now := time.Now().UTC()

	// Aggregation passes — windows are generous to catch late arrivals.
	from5 := now.Add(-10 * time.Minute)
	if err := s.Store.Rollup5Min(ctx, from5, now); err != nil {
		slog.Warn("rollup: 5min failed", "err", err)
	}

	fromH := now.Add(-2 * time.Hour)
	if err := s.Store.RollupHourly(ctx, fromH, now); err != nil {
		slog.Warn("rollup: hourly failed", "err", err)
	}

	fromD := now.Add(-48 * time.Hour)
	if err := s.Store.RollupDaily(ctx, fromD, now); err != nil {
		slog.Warn("rollup: daily failed", "err", err)
	}

	// Read retention windows from DB on every tick so changes take effect
	// without a restart. parseDuration falls back to the hardcoded default
	// if the DB value is missing or unparseable.
	cfg, err := s.Store.GetAllConfig(ctx)
	if err != nil {
		slog.Warn("rollup: read retention config", "err", err)
		cfg = map[string]string{}
	}

	if err := s.Store.PruneRawSnapshots(ctx, parseDuration(cfg["retention.raw_metrics"], 48*time.Hour)); err != nil {
		slog.Warn("rollup: prune raw failed", "err", err)
	}
	if err := s.Store.PruneRollup5Min(ctx, parseDuration(cfg["retention.rollup_5min"], 168*time.Hour)); err != nil {
		slog.Warn("rollup: prune 5min failed", "err", err)
	}
	if err := s.Store.PruneRollupHourly(ctx, parseDuration(cfg["retention.rollup_hourly"], 2160*time.Hour)); err != nil {
		slog.Warn("rollup: prune hourly failed", "err", err)
	}
	if err := s.Store.PruneRollupDaily(ctx, parseDuration(cfg["retention.rollup_daily"], 8760*time.Hour)); err != nil {
		slog.Warn("rollup: prune daily failed", "err", err)
	}
	if err := s.Store.PruneRemovedContainers(ctx, parseDuration(cfg["retention.removed_containers"], 168*time.Hour)); err != nil {
		slog.Warn("rollup: prune containers failed", "err", err)
	}
	if err := s.Store.PruneStaleObservations(ctx, parseDuration(cfg["retention.stale_observations"], 720*time.Hour)); err != nil {
		slog.Warn("rollup: prune observations failed", "err", err)
	}
}

// parseDuration parses a duration string in any supported format:
// human-friendly ("7d", "1d 3h"), Go duration ("48h"), or legacy
// plain integer (seconds). Returns fallback if the string is empty
// or invalid.
func parseDuration(s string, fallback time.Duration) time.Duration {
	if s == "" {
		return fallback
	}
	if d, err := duration.Parse(s); err == nil && d > 0 {
		return d
	}
	if d, err := time.ParseDuration(s); err == nil && d > 0 {
		return d
	}
	if n, err := strconv.Atoi(s); err == nil && n > 0 {
		return time.Duration(n) * time.Second
	}
	return fallback
}
