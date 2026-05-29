package store

import (
	"context"
	"fmt"
	"time"
)

// RollupBucket5Min returns the 5-minute bucket key for a given time.
func RollupBucket5Min(t time.Time) string {
	t = t.UTC().Truncate(5 * time.Minute)
	return t.Format(time.RFC3339)
}

// RollupBucketHourly returns the hourly bucket key for a given time.
func RollupBucketHourly(t time.Time) string {
	t = t.UTC().Truncate(time.Hour)
	return t.Format(time.RFC3339)
}

// RollupBucketDaily returns the daily bucket key for a given time.
func RollupBucketDaily(t time.Time) string {
	y, m, d := t.UTC().Date()
	return time.Date(y, m, d, 0, 0, 0, 0, time.UTC).Format(time.RFC3339)
}

// Rollup5Min aggregates raw metric_snapshots into metric_rollup_5min for the
// given time range. It should be called periodically (e.g., every 5 minutes).
func (s *Store) Rollup5Min(ctx context.Context, from, to time.Time) error {
	_, err := s.DB.ExecContext(ctx, `
		INSERT OR REPLACE INTO metric_rollup_5min (agent_id, bucket, cpu_pct_avg, cpu_pct_max, mem_used_avg, mem_used_max, load_1_avg, sample_count)
		SELECT
			agent_id,
			strftime('%Y-%m-%dT%H:', ts) || printf('%02d', (CAST(strftime('%M', ts) AS INTEGER) / 5) * 5) || ':00Z' AS bucket,
			AVG(cpu_pct),
			MAX(cpu_pct),
			AVG(mem_used_bytes),
			MAX(mem_used_bytes),
			AVG(load_1),
			COUNT(*)
		FROM metric_snapshots
		WHERE ts >= ? AND ts < ?
		GROUP BY agent_id, bucket`,
		from.UTC().Format(time.RFC3339), to.UTC().Format(time.RFC3339))
	return err
}

// RollupHourly aggregates 5-min rollups into metric_rollup_hourly.
func (s *Store) RollupHourly(ctx context.Context, from, to time.Time) error {
	_, err := s.DB.ExecContext(ctx, `
		INSERT OR REPLACE INTO metric_rollup_hourly (agent_id, bucket, cpu_pct_avg, cpu_pct_max, mem_used_avg, mem_used_max, load_1_avg, sample_count)
		SELECT
			agent_id,
			strftime('%Y-%m-%dT%H:00:00Z', bucket) AS hour_bucket,
			SUM(cpu_pct_avg * sample_count) / SUM(sample_count),
			MAX(cpu_pct_max),
			SUM(mem_used_avg * sample_count) / SUM(sample_count),
			MAX(mem_used_max),
			SUM(load_1_avg * sample_count) / SUM(sample_count),
			SUM(sample_count)
		FROM metric_rollup_5min
		WHERE bucket >= ? AND bucket < ?
		GROUP BY agent_id, hour_bucket`,
		from.UTC().Format(time.RFC3339), to.UTC().Format(time.RFC3339))
	return err
}

// RollupDaily aggregates hourly rollups into metric_rollup_daily.
func (s *Store) RollupDaily(ctx context.Context, from, to time.Time) error {
	_, err := s.DB.ExecContext(ctx, `
		INSERT OR REPLACE INTO metric_rollup_daily (agent_id, bucket, cpu_pct_avg, cpu_pct_max, mem_used_avg, mem_used_max, load_1_avg, sample_count)
		SELECT
			agent_id,
			strftime('%Y-%m-%dT00:00:00Z', bucket) AS day_bucket,
			SUM(cpu_pct_avg * sample_count) / SUM(sample_count),
			MAX(cpu_pct_max),
			SUM(mem_used_avg * sample_count) / SUM(sample_count),
			MAX(mem_used_max),
			SUM(load_1_avg * sample_count) / SUM(sample_count),
			SUM(sample_count)
		FROM metric_rollup_hourly
		WHERE bucket >= ? AND bucket < ?
		GROUP BY agent_id, day_bucket`,
		from.UTC().Format(time.RFC3339), to.UTC().Format(time.RFC3339))
	return err
}

// PruneRawSnapshots deletes raw snapshot rows older than the given duration
// from every per-poll snapshot table. Rollups cover historical queries for
// metrics; the other snapshot tables are point-in-time views and only need
// the recent window to render dashboards.
func (s *Store) PruneRawSnapshots(ctx context.Context, olderThan time.Duration) error {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	tables := []string{
		"metric_snapshots",
		"disk_snapshots",
		"interface_snapshots",
		"temperature_snapshots",
		"battery_snapshots",
		"process_snapshots",
	}
	for _, t := range tables {
		if _, err := s.DB.ExecContext(ctx,
			`DELETE FROM `+t+` WHERE ts < ?`, cutoff); err != nil {
			return fmt.Errorf("prune %s: %w", t, err)
		}
	}
	return nil
}

// PruneRollup5Min deletes 5-minute rollup rows older than the given duration.
func (s *Store) PruneRollup5Min(ctx context.Context, olderThan time.Duration) error {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx, `DELETE FROM metric_rollup_5min WHERE bucket < ?`, cutoff)
	return err
}

// PruneRollupHourly deletes hourly rollup rows older than the given duration.
func (s *Store) PruneRollupHourly(ctx context.Context, olderThan time.Duration) error {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx, `DELETE FROM metric_rollup_hourly WHERE bucket < ?`, cutoff)
	return err
}

// PruneRollupDaily deletes daily rollup rows older than the given duration.
func (s *Store) PruneRollupDaily(ctx context.Context, olderThan time.Duration) error {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx, `DELETE FROM metric_rollup_daily WHERE bucket < ?`, cutoff)
	return err
}

// PruneRemovedContainers deletes container rows for containers that are no
// longer running and whose last_seen_at is older than the given duration.
func (s *Store) PruneRemovedContainers(ctx context.Context, olderThan time.Duration) error {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx,
		`DELETE FROM containers WHERE state != 'running' AND last_seen_at < ?`, cutoff)
	return err
}

// PruneStaleObservations deletes entity observation rows older than the given
// duration. Observations are timestamped sighting records from any source
// (dhcp-lease, arp-scan, etc.); the entity model uses the observations table.
func (s *Store) PruneStaleObservations(ctx context.Context, olderThan time.Duration) error {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx,
		`DELETE FROM observations WHERE observed_at < ?`, cutoff)
	return err
}

// InsertTemperatureSnapshots writes temperature readings for an agent.
func (s *Store) InsertTemperatureSnapshots(ctx context.Context, agentID string, ts time.Time, readings []TempSnapshot) error {
	if len(readings) == 0 {
		return nil
	}
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	stmt, err := tx.PrepareContext(ctx,
		`INSERT OR REPLACE INTO temperature_snapshots (agent_id, ts, sensor, celsius) VALUES (?,?,?,?)`)
	if err != nil {
		return err
	}
	defer stmt.Close()
	tsStr := ts.UTC().Format(time.RFC3339)
	for _, r := range readings {
		if _, err := stmt.ExecContext(ctx, agentID, tsStr, r.Sensor, r.Celsius); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// TempSnapshot is a single temperature reading for storage.
type TempSnapshot struct {
	Sensor  string
	Celsius float64
}

// InsertBatterySnapshot writes a battery reading for an agent.
func (s *Store) InsertBatterySnapshot(ctx context.Context, agentID string, ts time.Time, chargePct float64, state string, healthPct float64) error {
	tsStr := ts.UTC().Format(time.RFC3339)
	_, err := s.DB.ExecContext(ctx,
		`INSERT OR REPLACE INTO battery_snapshots (agent_id, ts, charge_pct, state, health_pct) VALUES (?,?,?,?,?)`,
		agentID, tsStr, chargePct, state, healthPct)
	return err
}
