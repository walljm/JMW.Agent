package httpsrv

import (
	"context"
	"log/slog"
	"time"
)

// RunRollups starts a background goroutine that periodically aggregates raw
// metric snapshots into rollup tables. Blocks until ctx is cancelled.
func (s *Server) RunRollups(ctx context.Context) {
	// Run an initial rollup pass, then on a timer.
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

	// 5-minute rollup: aggregate the last 10 minutes to catch late arrivals.
	from5 := now.Add(-10 * time.Minute)
	if err := s.Store.Rollup5Min(ctx, from5, now); err != nil {
		slog.Warn("rollup: 5min failed", "err", err)
	}

	// Hourly rollup: aggregate the last 2 hours.
	fromH := now.Add(-2 * time.Hour)
	if err := s.Store.RollupHourly(ctx, fromH, now); err != nil {
		slog.Warn("rollup: hourly failed", "err", err)
	}

	// Daily rollup: aggregate the last 2 days.
	fromD := now.Add(-48 * time.Hour)
	if err := s.Store.RollupDaily(ctx, fromD, now); err != nil {
		slog.Warn("rollup: daily failed", "err", err)
	}

	// Prune raw snapshots older than 48 hours (rollups cover historical queries).
	if err := s.Store.PruneRawSnapshots(ctx, 48*time.Hour); err != nil {
		slog.Warn("rollup: prune failed", "err", err)
	}
}
