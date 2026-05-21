package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

// SetAgentInventory stores the latest inventory blob for an agent (latest-wins).
// primaryIP is denormalized into the agents row so list views can render it
// without parsing the JSON blob per row.
func (s *Store) SetAgentInventory(ctx context.Context, agentID, inventoryJSON, primaryIP string, collectedAt time.Time) error {
	res, err := s.DB.ExecContext(ctx,
		`UPDATE agents SET inventory_json = ?, inventory_collected_at = ?, primary_ip = ? WHERE id = ?`,
		inventoryJSON, collectedAt.UTC().Format(time.RFC3339), primaryIP, agentID)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

// GetAgentInventory returns the raw inventory JSON (or empty + zero time if none).
func (s *Store) GetAgentInventory(ctx context.Context, agentID string) (string, time.Time, error) {
	var (
		blob sql.NullString
		ts   sql.NullString
	)
	err := s.DB.QueryRowContext(ctx,
		`SELECT inventory_json, inventory_collected_at FROM agents WHERE id = ?`, agentID).
		Scan(&blob, &ts)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return "", time.Time{}, nil
		}
		return "", time.Time{}, err
	}
	var t time.Time
	if ts.Valid {
		t, _ = time.Parse(time.RFC3339, ts.String)
	}
	return blob.String, t, nil
}
