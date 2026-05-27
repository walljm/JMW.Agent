package alerting

import (
	"context"
	"fmt"
	"time"

	"github.com/walljm/jmwagent/internal/server/store"
)

// MetricFetcher retrieves the current value for a given metric_kind + metric_path
// combination for a specific agent. Returns (value, ok).
type MetricFetcher func(ctx context.Context, st *store.Store, agentID, metricPath string) (float64, bool)

// dispatch maps metric_kind → evaluator function.
var dispatch = map[string]MetricFetcher{
	"numeric_snapshot": fetchNumericSnapshot,
	"disk_usage":       fetchDiskUsage,
	"temperature":      fetchTemperature,
	"offline":          fetchOffline,
	"source_health":    fetchSourceHealth,
}

func fetchNumericSnapshot(ctx context.Context, st *store.Store, agentID, metricPath string) (float64, bool) {
	sn, err := st.LatestSnapshot(ctx, agentID)
	if err != nil || sn == nil {
		return 0, false
	}
	switch metricPath {
	case "cpu_pct":
		return sn.CPUPercent, true
	case "mem_pct":
		if sn.MemTotalBytes == 0 {
			return 0, false
		}
		return float64(sn.MemUsedBytes) / float64(sn.MemTotalBytes) * 100, true
	case "load_1":
		return sn.Load1, true
	case "load_5":
		return sn.Load5, true
	case "load_15":
		return sn.Load15, true
	default:
		return 0, false
	}
}

func fetchDiskUsage(ctx context.Context, st *store.Store, agentID, metricPath string) (float64, bool) {
	// metricPath is the device name (or empty for "any disk").
	disks, err := st.LatestDiskSnapshots(ctx, agentID)
	if err != nil || len(disks) == 0 {
		return 0, false
	}
	// If metricPath specified, find that device; otherwise return worst (highest usage).
	var best float64
	found := false
	for _, d := range disks {
		if d.TotalBytes == 0 {
			continue
		}
		pct := float64(d.UsedBytes) / float64(d.TotalBytes) * 100
		if metricPath != "" && d.Device == metricPath {
			return pct, true
		}
		if pct > best {
			best = pct
			found = true
		}
	}
	if metricPath != "" {
		return 0, false // specific disk not found
	}
	return best, found
}

func fetchTemperature(ctx context.Context, st *store.Store, agentID, metricPath string) (float64, bool) {
	temps, err := st.LatestTemperatureSnapshots(ctx, agentID)
	if err != nil || len(temps) == 0 {
		return 0, false
	}
	// metricPath is sensor name or empty (worst = highest temp).
	var best float64
	found := false
	for _, t := range temps {
		if metricPath != "" && t.Sensor == metricPath {
			return t.Celsius, true
		}
		if t.Celsius > best {
			best = t.Celsius
			found = true
		}
	}
	if metricPath != "" {
		return 0, false
	}
	return best, found
}

func fetchOffline(ctx context.Context, st *store.Store, agentID, _ string) (float64, bool) {
	a, err := st.GetAgent(ctx, agentID)
	if err != nil || a == nil {
		return 9999, true
	}
	if a.LastHeartbeatAt == nil {
		return 9999, true
	}
	return time.Since(*a.LastHeartbeatAt).Minutes(), true
}

func fetchSourceHealth(ctx context.Context, st *store.Store, agentID, metricPath string) (float64, bool) {
	// metricPath is source_id; returns consecutive error count.
	src, err := st.GetSource(ctx, metricPath)
	if err != nil || src == nil {
		return 0, false
	}
	return float64(src.ConsecutiveErrorCount), true
}

// FetchMetric dispatches to the appropriate fetcher for the given kind.
func FetchMetric(ctx context.Context, st *store.Store, kind, path, agentID string) (float64, bool) {
	fetcher, ok := dispatch[kind]
	if !ok {
		return 0, false
	}
	return fetcher(ctx, st, agentID, path)
}

// MaintenanceWindow represents a maintenance window during which alerts are suppressed.
type MaintenanceWindow struct {
	ID         int64
	Name       string
	TargetKind string
	TargetID   string
	StartsAt   time.Time
	EndsAt     time.Time
	Recurrence string
}

// IsInMaintenance checks if an agent is currently in a maintenance window.
func IsInMaintenance(windows []MaintenanceWindow, targetKind, targetID string, now time.Time) bool {
	for _, w := range windows {
		if !windowActive(w, now) {
			continue
		}
		// Global window.
		if w.TargetKind == "all" {
			return true
		}
		// Match by target.
		if w.TargetKind == targetKind && (w.TargetID == "" || w.TargetID == targetID) {
			return true
		}
	}
	return false
}

func windowActive(w MaintenanceWindow, now time.Time) bool {
	switch w.Recurrence {
	case "daily":
		// Check if current time-of-day is within the window's time range.
		start := timeOfDay(w.StartsAt)
		end := timeOfDay(w.EndsAt)
		cur := timeOfDay(now)
		if start <= end {
			return cur >= start && cur < end
		}
		return cur >= start || cur < end // overnight
	case "weekly":
		// Same weekday and time-of-day check.
		if now.Weekday() != w.StartsAt.Weekday() {
			return false
		}
		start := timeOfDay(w.StartsAt)
		end := timeOfDay(w.EndsAt)
		cur := timeOfDay(now)
		return cur >= start && cur < end
	default:
		// One-time window.
		return now.After(w.StartsAt) && now.Before(w.EndsAt)
	}
}

func timeOfDay(t time.Time) int {
	return t.Hour()*3600 + t.Minute()*60 + t.Second()
}

// FlapThreshold is the number of transitions within FlapWindow that marks a
// firing as flapping. Once flapping, notifications are suppressed.
const (
	FlapThreshold = 4
	FlapWindow    = 10 * time.Minute
)

// IsFlapping returns true if the transition count exceeds the flap threshold.
func IsFlapping(transitionCount int) bool {
	return transitionCount >= FlapThreshold
}

// FormatSummary creates a human-readable alert summary.
func FormatSummary(ruleName, hostname, metricKind, metricPath, op string, threshold, value float64) string {
	metric := metricKind
	if metricPath != "" {
		metric = fmt.Sprintf("%s.%s", metricKind, metricPath)
	}
	return fmt.Sprintf("%s on %s: %s %s %.2f (was %.2f)", ruleName, hostname, metric, op, threshold, value)
}
