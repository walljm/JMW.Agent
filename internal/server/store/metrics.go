package store

import (
	"context"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// InsertSnapshots writes a batch of metric snapshots for an agent.
func (s *Store) InsertSnapshots(ctx context.Context, agentID string, snaps []proto.MetricSnapshot) error {
	if len(snaps) == 0 {
		return nil
	}
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	mainStmt, err := tx.PrepareContext(ctx,
		`INSERT OR REPLACE INTO metric_snapshots
		 (agent_id, ts, cpu_pct, mem_used_bytes, mem_total_bytes, load_1, load_5, load_15, uptime_seconds)
		 VALUES (?,?,?,?,?,?,?,?,?)`)
	if err != nil {
		return err
	}
	defer mainStmt.Close()

	diskStmt, err := tx.PrepareContext(ctx,
		`INSERT OR REPLACE INTO disk_snapshots
		 (agent_id, ts, device, mountpoint, used_bytes, total_bytes, fs_type)
		 VALUES (?,?,?,?,?,?,?)`)
	if err != nil {
		return err
	}
	defer diskStmt.Close()

	ifStmt, err := tx.PrepareContext(ctx,
		`INSERT OR REPLACE INTO interface_snapshots
		 (agent_id, ts, iface, ip, mac, rx_bytes, tx_bytes, rx_packets, tx_packets, is_up)
		 VALUES (?,?,?,?,?,?,?,?,?,?)`)
	if err != nil {
		return err
	}
	defer ifStmt.Close()

	for _, sn := range snaps {
		ts := sn.Timestamp.UTC().Format(time.RFC3339)
		if _, err := mainStmt.ExecContext(ctx, agentID, ts,
			sn.CPUPercent, sn.MemUsedBytes, sn.MemTotalBytes,
			sn.Load1, sn.Load5, sn.Load15, sn.UptimeSeconds); err != nil {
			return err
		}
		for _, d := range sn.Disks {
			if _, err := diskStmt.ExecContext(ctx, agentID, ts,
				d.Device, d.Mountpoint, d.UsedBytes, d.TotalBytes, d.FSType); err != nil {
				return err
			}
		}
		for _, i := range sn.Interfaces {
			up := 0
			if i.IsUp {
				up = 1
			}
			if _, err := ifStmt.ExecContext(ctx, agentID, ts,
				i.Name, i.IP, i.MAC, i.RxBytes, i.TxBytes, i.RxPackets, i.TxPackets, up); err != nil {
				return err
			}
		}
	}

	return tx.Commit()
}

// LatestSnapshot returns the most recent snapshot for an agent (or nil).
func (s *Store) LatestSnapshot(ctx context.Context, agentID string) (*proto.MetricSnapshot, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT ts, cpu_pct, mem_used_bytes, mem_total_bytes, load_1, load_5, load_15, uptime_seconds
		 FROM metric_snapshots WHERE agent_id = ? ORDER BY ts DESC LIMIT 1`, agentID)
	var sn proto.MetricSnapshot
	var ts string
	if err := row.Scan(&ts, &sn.CPUPercent, &sn.MemUsedBytes, &sn.MemTotalBytes,
		&sn.Load1, &sn.Load5, &sn.Load15, &sn.UptimeSeconds); err != nil {
		return nil, err
	}
	sn.Timestamp, _ = time.Parse(time.RFC3339, ts)
	return &sn, nil
}

// SnapshotsSince returns snapshots for an agent since a given time, ordered ascending.
func (s *Store) SnapshotsSince(ctx context.Context, agentID string, since time.Time) ([]proto.MetricSnapshot, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT ts, cpu_pct, mem_used_bytes, mem_total_bytes, load_1, load_5, load_15, uptime_seconds
		 FROM metric_snapshots WHERE agent_id = ? AND ts >= ? ORDER BY ts ASC`,
		agentID, since.UTC().Format(time.RFC3339))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []proto.MetricSnapshot
	for rows.Next() {
		var sn proto.MetricSnapshot
		var ts string
		if err := rows.Scan(&ts, &sn.CPUPercent, &sn.MemUsedBytes, &sn.MemTotalBytes,
			&sn.Load1, &sn.Load5, &sn.Load15, &sn.UptimeSeconds); err != nil {
			return nil, err
		}
		sn.Timestamp, _ = time.Parse(time.RFC3339, ts)
		out = append(out, sn)
	}
	return out, rows.Err()
}
