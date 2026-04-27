package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"time"
)

// AgentStatus values.
const (
	AgentStatusPending      = "pending"
	AgentStatusApproved     = "approved"
	AgentStatusDeregistered = "deregistered"
)

// Agent is a registered agent record.
type Agent struct {
	ID                string
	Hostname          string
	OS                string
	Arch              string
	Version           string
	Status            string
	ApprovedAt        *time.Time
	ApprovedBy        string
	RegisteredAt      time.Time
	LastHeartbeatAt   *time.Time
	EnabledSubsystems []string
	Notes             string
	GroupID           *int64
	PrimaryIP         string
}

// CreateAgent inserts a new pending agent.
func (s *Store) CreateAgent(ctx context.Context, a *Agent) error {
	subs, _ := json.Marshal(a.EnabledSubsystems)
	now := time.Now().UTC().Format(time.RFC3339)
	if a.RegisteredAt.IsZero() {
		a.RegisteredAt, _ = time.Parse(time.RFC3339, now)
	}
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO agents(id, hostname, os, arch, version, status, registered_at, enabled_subsystems)
		 VALUES(?,?,?,?,?,?,?,?)`,
		a.ID, a.Hostname, a.OS, a.Arch, a.Version, a.Status,
		a.RegisteredAt.UTC().Format(time.RFC3339), string(subs))
	return err
}

// GetAgent returns one agent by ID, or (nil, nil) if not found.
func (s *Store) GetAgent(ctx context.Context, id string) (*Agent, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, hostname, os, arch, COALESCE(version,''), status,
		        approved_at, COALESCE(approved_by,''), registered_at, last_heartbeat_at,
		        enabled_subsystems, COALESCE(notes,''), group_id, COALESCE(primary_ip,'')
		 FROM agents WHERE id = ?`, id)
	return scanAgent(row)
}

// ListAgents returns agents matching the given status (or all if status is "").
func (s *Store) ListAgents(ctx context.Context, status string) ([]*Agent, error) {
	q := `SELECT id, hostname, os, arch, COALESCE(version,''), status,
	             approved_at, COALESCE(approved_by,''), registered_at, last_heartbeat_at,
	             enabled_subsystems, COALESCE(notes,''), group_id, COALESCE(primary_ip,'')
	      FROM agents`
	args := []any{}
	if status != "" {
		q += ` WHERE status = ?`
		args = append(args, status)
	}
	q += ` ORDER BY hostname ASC`
	rows, err := s.DB.QueryContext(ctx, q, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Agent
	for rows.Next() {
		a, err := scanAgent(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, a)
	}
	return out, rows.Err()
}

// ApproveAgent marks an agent approved.
func (s *Store) ApproveAgent(ctx context.Context, id, approvedBy string) error {
	now := time.Now().UTC().Format(time.RFC3339)
	res, err := s.DB.ExecContext(ctx,
		`UPDATE agents SET status = ?, approved_at = ?, approved_by = ?
		 WHERE id = ? AND status = ?`,
		AgentStatusApproved, now, approvedBy, id, AgentStatusPending)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return errors.New("agent not found or not pending")
	}
	return nil
}

// DeregisterAgent marks an agent as deregistered.
func (s *Store) DeregisterAgent(ctx context.Context, id string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE agents SET status = ? WHERE id = ?`,
		AgentStatusDeregistered, id)
	return err
}

// TouchAgentHeartbeat updates last heartbeat + version + subsystems.
func (s *Store) TouchAgentHeartbeat(ctx context.Context, id, version string, subs []string) error {
	subsJSON, _ := json.Marshal(subs)
	now := time.Now().UTC().Format(time.RFC3339)
	res, err := s.DB.ExecContext(ctx,
		`UPDATE agents SET last_heartbeat_at = ?, version = ?, enabled_subsystems = ? WHERE id = ?`,
		now, version, string(subsJSON), id)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

// scannable is anything that has a Scan method.
type scannable interface {
	Scan(dest ...any) error
}

func scanAgent(row scannable) (*Agent, error) {
	var a Agent
	var approvedAt, lastHB sql.NullString
	var registeredAt, subs string
	var groupID sql.NullInt64
	if err := row.Scan(&a.ID, &a.Hostname, &a.OS, &a.Arch, &a.Version, &a.Status,
		&approvedAt, &a.ApprovedBy, &registeredAt, &lastHB,
		&subs, &a.Notes, &groupID, &a.PrimaryIP); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	a.RegisteredAt, _ = time.Parse(time.RFC3339, registeredAt)
	if approvedAt.Valid {
		t, _ := time.Parse(time.RFC3339, approvedAt.String)
		a.ApprovedAt = &t
	}
	if lastHB.Valid {
		t, _ := time.Parse(time.RFC3339, lastHB.String)
		a.LastHeartbeatAt = &t
	}
	_ = json.Unmarshal([]byte(subs), &a.EnabledSubsystems)
	if groupID.Valid {
		a.GroupID = &groupID.Int64
	}
	return &a, nil
}
