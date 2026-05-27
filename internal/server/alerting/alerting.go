// Package alerting evaluates alert rules and dispatches notifications.
package alerting

import (
	"context"
	"fmt"
	"log/slog"
	"time"

	"github.com/walljm/jmwagent/internal/server/notify"
	"github.com/walljm/jmwagent/internal/server/store"
)

// Evaluator runs the alert evaluation loop.
type Evaluator struct {
	Store    *store.Store
	Interval time.Duration
}

// Run blocks until ctx is done, evaluating rules at every tick.
func (e *Evaluator) Run(ctx context.Context) {
	if e.Interval <= 0 {
		e.Interval = 30 * time.Second
	}
	t := time.NewTicker(e.Interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			if err := e.evaluateOnce(ctx); err != nil {
				slog.Warn("alert eval", "err", err)
			}
		}
	}
}

func (e *Evaluator) evaluateOnce(ctx context.Context) error {
	rules, err := e.Store.ListAlertRules(ctx)
	if err != nil {
		return err
	}
	if len(rules) == 0 {
		return nil
	}
	agents, err := e.Store.ListAgents(ctx, store.AgentStatusApproved)
	if err != nil {
		return err
	}

	// Load maintenance windows once per cycle.
	storeWindows, err := e.Store.ListMaintenanceWindows(ctx)
	if err != nil {
		return err
	}
	windows := make([]MaintenanceWindow, len(storeWindows))
	for i, mw := range storeWindows {
		windows[i] = MaintenanceWindow{
			ID:         mw.ID,
			Name:       mw.Name,
			TargetKind: mw.TargetKind,
			TargetID:   mw.TargetID,
			StartsAt:   mw.StartsAt,
			EndsAt:     mw.EndsAt,
			Recurrence: mw.Recurrence,
		}
	}

	now := time.Now().UTC()
	for _, r := range rules {
		if !r.Enabled {
			continue
		}
		for _, a := range agents {
			if r.TargetKind == "agent" && r.TargetID != "" && r.TargetID != a.ID {
				continue
			}

			// Check maintenance window.
			if IsInMaintenance(windows, "agent", a.ID, now) {
				continue
			}

			// Use new dispatch if metric_kind is set; fall back to legacy.
			var val float64
			var ok bool
			if r.MetricKind != "" {
				val, ok = FetchMetric(ctx, e.Store, r.MetricKind, r.MetricPath, a.ID)
			} else {
				val, ok = metricValueFor(ctx, e.Store, r.Metric, a)
			}
			if !ok {
				continue
			}

			breach := false
			switch r.Op {
			case "gt":
				breach = val > r.Threshold
			case "lt":
				breach = val < r.Threshold
			}

			open, _ := e.Store.OpenFiring(ctx, r.ID, a.ID)
			if breach {
				if open == nil {
					f := &store.AlertFiring{
						RuleID:    r.ID,
						AgentID:   a.ID,
						LastValue: val,
						Summary:   FormatSummary(r.Name, a.Hostname, r.MetricKind, r.MetricPath, r.Op, r.Threshold, val),
					}
					if err := e.Store.StartFiring(ctx, f); err != nil {
						slog.Warn("start firing", "err", err)
						continue
					}
					_ = e.Store.LogEvent(ctx, &store.Event{
						Type: "alert.firing", Severity: r.Severity,
						SourceKind: "agent", SourceID: a.ID,
						Summary: f.Summary,
						Detail:  map[string]any{"rule_id": r.ID, "value": val},
					})
					if r.ChannelID != nil {
						ch, err := e.Store.GetChannel(ctx, *r.ChannelID)
						if err == nil && ch != nil && ch.Enabled {
							if err := notify.Send(ctx, ch, r.Severity, f.Summary); err != nil {
								slog.Warn("notify", "err", err)
							} else {
								_ = e.Store.MarkFiringNotified(ctx, f.ID)
							}
						}
					}
				}
			} else if open != nil {
				if err := e.Store.ResolveFiring(ctx, open.ID); err != nil {
					slog.Warn("resolve firing", "err", err)
					continue
				}
				_ = e.Store.LogEvent(ctx, &store.Event{
					Type: "alert.resolved", Severity: store.SeverityInfo,
					SourceKind: "agent", SourceID: a.ID,
					Summary: fmt.Sprintf("Resolved: %s on %s", r.Name, a.Hostname),
					Detail:  map[string]any{"rule_id": r.ID, "value": val},
				})
			}
		}
	}
	return nil
}

// metricValueFor returns the current metric value the rule cares about.
func metricValueFor(ctx context.Context, st *store.Store, metric string, a *store.Agent) (float64, bool) {
	if metric == "offline_minutes" {
		if a.LastHeartbeatAt == nil {
			return 9999, true
		}
		return time.Since(*a.LastHeartbeatAt).Minutes(), true
	}
	sn, err := st.LatestSnapshot(ctx, a.ID)
	if err != nil || sn == nil {
		return 0, false
	}
	switch metric {
	case "cpu_pct":
		return sn.CPUPercent, true
	case "mem_pct":
		if sn.MemTotalBytes == 0 {
			return 0, false
		}
		return float64(sn.MemUsedBytes) / float64(sn.MemTotalBytes) * 100, true
	}
	return 0, false
}
