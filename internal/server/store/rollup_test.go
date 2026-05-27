package store

import (
	"context"
	"testing"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

func TestRollup5Min(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	// Create agent for FK.
	_, err := st.DB.ExecContext(ctx,
		`INSERT INTO agents (id, hostname, os, arch, status, registered_at) VALUES ('a1','host1','linux','amd64','approved',?)`,
		time.Now().UTC().Format(time.RFC3339))
	if err != nil {
		t.Fatal(err)
	}

	// Insert raw snapshots at 30-second intervals over 10 minutes.
	base := time.Date(2025, 1, 1, 12, 0, 0, 0, time.UTC)
	for i := range 20 {
		ts := base.Add(time.Duration(i) * 30 * time.Second)
		snap := proto.MetricSnapshot{
			Timestamp:     ts,
			CPUPercent:    float64(50 + i),
			MemUsedBytes:  1000000000 + uint64(i)*100000000,
			MemTotalBytes: 8000000000,
			Load1:         float64(i) * 0.1,
		}
		if err := st.InsertSnapshots(ctx, "a1", []proto.MetricSnapshot{snap}); err != nil {
			t.Fatal(err)
		}
	}

	// Run 5-min rollup.
	from := base
	to := base.Add(11 * time.Minute)
	if err := st.Rollup5Min(ctx, from, to); err != nil {
		t.Fatal("rollup5min:", err)
	}

	// Verify rollup rows exist.
	var count int
	err = st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM metric_rollup_5min WHERE agent_id = 'a1'`).Scan(&count)
	if err != nil {
		t.Fatal(err)
	}
	// 20 samples over 10 minutes = 2 full 5-min buckets.
	if count < 2 {
		t.Errorf("expected at least 2 rollup buckets, got %d", count)
	}

	// Verify aggregation correctness for first bucket.
	var avgCPU, maxCPU float64
	var sampleCount int
	err = st.DB.QueryRowContext(ctx,
		`SELECT cpu_pct_avg, cpu_pct_max, sample_count FROM metric_rollup_5min
		 WHERE agent_id = 'a1' ORDER BY bucket ASC LIMIT 1`).Scan(&avgCPU, &maxCPU, &sampleCount)
	if err != nil {
		t.Fatal(err)
	}
	// First bucket: timestamps 12:00:00 through 12:04:30 = indices 0-9 = CPU 50-59.
	if sampleCount != 10 {
		t.Errorf("first bucket sample_count: got %d, want 10", sampleCount)
	}
	if maxCPU != 59 {
		t.Errorf("first bucket cpu_pct_max: got %f, want 59", maxCPU)
	}
	if avgCPU < 54 || avgCPU > 55 {
		t.Errorf("first bucket cpu_pct_avg: got %f, want ~54.5", avgCPU)
	}
}

func TestRollupHourly(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	_, err := st.DB.ExecContext(ctx,
		`INSERT INTO agents (id, hostname, os, arch, status, registered_at) VALUES ('a1','host1','linux','amd64','approved',?)`,
		time.Now().UTC().Format(time.RFC3339))
	if err != nil {
		t.Fatal(err)
	}

	// Insert directly into 5min rollup table.
	base := time.Date(2025, 1, 1, 12, 0, 0, 0, time.UTC)
	for i := range 12 {
		bucket := base.Add(time.Duration(i) * 5 * time.Minute).Format(time.RFC3339)
		_, err := st.DB.ExecContext(ctx,
			`INSERT INTO metric_rollup_5min (agent_id, bucket, cpu_pct_avg, cpu_pct_max, mem_used_avg, mem_used_max, load_1_avg, sample_count)
			 VALUES ('a1', ?, ?, ?, ?, ?, ?, 10)`,
			bucket, float64(50+i), float64(60+i), 1000000000, 2000000000, float64(i)*0.1)
		if err != nil {
			t.Fatal(err)
		}
	}

	// Run hourly rollup.
	from := base
	to := base.Add(2 * time.Hour)
	if err := st.RollupHourly(ctx, from, to); err != nil {
		t.Fatal("rollup hourly:", err)
	}

	var count int
	err = st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM metric_rollup_hourly WHERE agent_id = 'a1'`).Scan(&count)
	if err != nil {
		t.Fatal(err)
	}
	if count != 1 {
		t.Errorf("expected 1 hourly bucket, got %d", count)
	}
}

func TestInsertTemperatureSnapshots(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	_, err := st.DB.ExecContext(ctx,
		`INSERT INTO agents (id, hostname, os, arch, status, registered_at) VALUES ('a1','host1','linux','amd64','approved',?)`,
		time.Now().UTC().Format(time.RFC3339))
	if err != nil {
		t.Fatal(err)
	}

	ts := time.Date(2025, 1, 1, 12, 0, 0, 0, time.UTC)
	readings := []TempSnapshot{
		{Sensor: "cpu_thermal", Celsius: 65.5},
		{Sensor: "gpu_thermal", Celsius: 72.0},
	}
	if err := st.InsertTemperatureSnapshots(ctx, "a1", ts, readings); err != nil {
		t.Fatal(err)
	}

	var count int
	err = st.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM temperature_snapshots WHERE agent_id = 'a1'`).Scan(&count)
	if err != nil {
		t.Fatal(err)
	}
	if count != 2 {
		t.Errorf("expected 2 temp rows, got %d", count)
	}
}

func TestInsertBatterySnapshot(t *testing.T) {
	st := testStore(t)
	ctx := context.Background()

	_, err := st.DB.ExecContext(ctx,
		`INSERT INTO agents (id, hostname, os, arch, status, registered_at) VALUES ('a1','host1','linux','amd64','approved',?)`,
		time.Now().UTC().Format(time.RFC3339))
	if err != nil {
		t.Fatal(err)
	}

	ts := time.Date(2025, 1, 1, 12, 0, 0, 0, time.UTC)
	if err := st.InsertBatterySnapshot(ctx, "a1", ts, 85.5, "discharging", 92.0); err != nil {
		t.Fatal(err)
	}

	var chargePct float64
	var state string
	err = st.DB.QueryRowContext(ctx,
		`SELECT charge_pct, state FROM battery_snapshots WHERE agent_id = 'a1'`).Scan(&chargePct, &state)
	if err != nil {
		t.Fatal(err)
	}
	if chargePct != 85.5 {
		t.Errorf("charge_pct: got %f, want 85.5", chargePct)
	}
	if state != "discharging" {
		t.Errorf("state: got %q, want discharging", state)
	}
}
