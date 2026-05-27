package alerting

import (
	"testing"
	"time"
)

func TestIsInMaintenance_OneTime(t *testing.T) {
	now := time.Date(2025, 1, 15, 14, 30, 0, 0, time.UTC)
	windows := []MaintenanceWindow{
		{
			TargetKind: "all",
			StartsAt:   time.Date(2025, 1, 15, 14, 0, 0, 0, time.UTC),
			EndsAt:     time.Date(2025, 1, 15, 15, 0, 0, 0, time.UTC),
		},
	}
	if !IsInMaintenance(windows, "agent", "a1", now) {
		t.Error("expected in maintenance")
	}
	// After window.
	after := time.Date(2025, 1, 15, 15, 30, 0, 0, time.UTC)
	if IsInMaintenance(windows, "agent", "a1", after) {
		t.Error("expected not in maintenance after window")
	}
}

func TestIsInMaintenance_Daily(t *testing.T) {
	windows := []MaintenanceWindow{
		{
			TargetKind: "all",
			StartsAt:   time.Date(2025, 1, 1, 2, 0, 0, 0, time.UTC), // 2am
			EndsAt:     time.Date(2025, 1, 1, 4, 0, 0, 0, time.UTC), // 4am
			Recurrence: "daily",
		},
	}
	// Different day, same time-of-day range.
	inWindow := time.Date(2025, 6, 15, 3, 0, 0, 0, time.UTC)
	if !IsInMaintenance(windows, "agent", "a1", inWindow) {
		t.Error("expected in daily maintenance window")
	}
	outWindow := time.Date(2025, 6, 15, 5, 0, 0, 0, time.UTC)
	if IsInMaintenance(windows, "agent", "a1", outWindow) {
		t.Error("expected outside daily maintenance window")
	}
}

func TestIsInMaintenance_TargetAgent(t *testing.T) {
	now := time.Date(2025, 1, 15, 14, 30, 0, 0, time.UTC)
	windows := []MaintenanceWindow{
		{
			TargetKind: "agent",
			TargetID:   "a1",
			StartsAt:   time.Date(2025, 1, 15, 14, 0, 0, 0, time.UTC),
			EndsAt:     time.Date(2025, 1, 15, 15, 0, 0, 0, time.UTC),
		},
	}
	if !IsInMaintenance(windows, "agent", "a1", now) {
		t.Error("expected agent a1 in maintenance")
	}
	if IsInMaintenance(windows, "agent", "a2", now) {
		t.Error("expected agent a2 NOT in maintenance")
	}
}

func TestIsFlapping(t *testing.T) {
	if IsFlapping(3) {
		t.Error("3 transitions should not be flapping")
	}
	if !IsFlapping(4) {
		t.Error("4 transitions should be flapping")
	}
}

func TestFormatSummary(t *testing.T) {
	s := FormatSummary("High CPU", "server1", "numeric_snapshot", "cpu_pct", "gt", 90, 95.5)
	if s == "" {
		t.Error("expected non-empty summary")
	}
}
