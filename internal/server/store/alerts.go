package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"time"
)

// AlertRule represents a configured alert.
type AlertRule struct {
	ID              int64
	Name            string
	Enabled         bool
	Metric          string  // cpu_pct | mem_pct | disk_pct | offline_minutes
	Op              string  // gt | lt
	Threshold       float64
	DurationSeconds int
	TargetKind      string // agent | group | all
	TargetID        string
	Severity        string
	ChannelID       *int64
	CreatedAt       time.Time
}

// CreateAlertRule inserts a new rule.
func (s *Store) CreateAlertRule(ctx context.Context, r *AlertRule) error {
	if r.CreatedAt.IsZero() {
		r.CreatedAt = time.Now().UTC()
	}
	res, err := s.DB.ExecContext(ctx,
		`INSERT INTO alert_rules(name, enabled, metric, op, threshold, duration_seconds, target_kind, target_id, severity, channel_id, created_at)
		 VALUES(?,?,?,?,?,?,?,?,?,?,?)`,
		r.Name, boolToInt(r.Enabled), r.Metric, r.Op, r.Threshold, r.DurationSeconds,
		r.TargetKind, r.TargetID, r.Severity, nullInt64(r.ChannelID),
		r.CreatedAt.Format(time.RFC3339))
	if err != nil {
		return err
	}
	r.ID, _ = res.LastInsertId()
	return nil
}

// ListAlertRules returns all rules.
func (s *Store) ListAlertRules(ctx context.Context) ([]*AlertRule, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, name, enabled, metric, op, threshold, duration_seconds,
		        target_kind, COALESCE(target_id,''), severity, channel_id, created_at
		 FROM alert_rules ORDER BY id ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*AlertRule
	for rows.Next() {
		var r AlertRule
		var enabled int
		var chanID sql.NullInt64
		var created string
		if err := rows.Scan(&r.ID, &r.Name, &enabled, &r.Metric, &r.Op, &r.Threshold,
			&r.DurationSeconds, &r.TargetKind, &r.TargetID, &r.Severity, &chanID, &created); err != nil {
			return nil, err
		}
		r.Enabled = enabled != 0
		if chanID.Valid {
			r.ChannelID = &chanID.Int64
		}
		r.CreatedAt, _ = time.Parse(time.RFC3339, created)
		out = append(out, &r)
	}
	return out, rows.Err()
}

// DeleteAlertRule removes a rule.
func (s *Store) DeleteAlertRule(ctx context.Context, id int64) error {
	_, err := s.DB.ExecContext(ctx, `DELETE FROM alert_rules WHERE id = ?`, id)
	return err
}

// AlertFiring is an active or historical firing event.
type AlertFiring struct {
	ID         int64
	RuleID     int64
	AgentID    string
	StartedAt  time.Time
	ResolvedAt *time.Time
	LastValue  float64
	Summary    string
	Notified   bool
}

// OpenFiring returns the open (unresolved) firing for rule+agent if any.
func (s *Store) OpenFiring(ctx context.Context, ruleID int64, agentID string) (*AlertFiring, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, rule_id, COALESCE(agent_id,''), started_at, resolved_at,
		        COALESCE(last_value,0), COALESCE(summary,''), notified
		 FROM alert_firings WHERE rule_id = ? AND COALESCE(agent_id,'') = ? AND resolved_at IS NULL
		 ORDER BY id DESC LIMIT 1`, ruleID, agentID)
	var f AlertFiring
	var started string
	var resolved sql.NullString
	var notified int
	if err := row.Scan(&f.ID, &f.RuleID, &f.AgentID, &started, &resolved,
		&f.LastValue, &f.Summary, &notified); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	f.StartedAt, _ = time.Parse(time.RFC3339, started)
	if resolved.Valid {
		t, _ := time.Parse(time.RFC3339, resolved.String)
		f.ResolvedAt = &t
	}
	f.Notified = notified != 0
	return &f, nil
}

// OpenFiring inserts a new open firing.
func (s *Store) StartFiring(ctx context.Context, f *AlertFiring) error {
	if f.StartedAt.IsZero() {
		f.StartedAt = time.Now().UTC()
	}
	res, err := s.DB.ExecContext(ctx,
		`INSERT INTO alert_firings(rule_id, agent_id, started_at, last_value, summary, notified)
		 VALUES(?,?,?,?,?,?)`,
		f.RuleID, f.AgentID, f.StartedAt.Format(time.RFC3339),
		f.LastValue, f.Summary, boolToInt(f.Notified))
	if err != nil {
		return err
	}
	f.ID, _ = res.LastInsertId()
	return nil
}

// ResolveFiring closes a firing.
func (s *Store) ResolveFiring(ctx context.Context, id int64) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE alert_firings SET resolved_at = ? WHERE id = ?`,
		time.Now().UTC().Format(time.RFC3339), id)
	return err
}

// MarkFiringNotified sets notified=1.
func (s *Store) MarkFiringNotified(ctx context.Context, id int64) error {
	_, err := s.DB.ExecContext(ctx, `UPDATE alert_firings SET notified = 1 WHERE id = ?`, id)
	return err
}

// AlertStats summarizes alert firings for dashboard KPIs.
type AlertStats struct {
	Open     int // firings with resolved_at IS NULL
	Last24h  int // firings whose started_at is within the last 24h
}

// AlertStats returns aggregate firing counts in a single round trip.
func (s *Store) AlertStats(ctx context.Context) (AlertStats, error) {
	var st AlertStats
	cutoff := time.Now().UTC().Add(-24 * time.Hour).Format(time.RFC3339)
	err := s.DB.QueryRowContext(ctx,
		`SELECT
		   COALESCE(SUM(CASE WHEN resolved_at IS NULL THEN 1 ELSE 0 END), 0),
		   COALESCE(SUM(CASE WHEN started_at >= ? THEN 1 ELSE 0 END), 0)
		 FROM alert_firings`, cutoff).
		Scan(&st.Open, &st.Last24h)
	return st, err
}

// ListFirings returns recent firings (open + resolved).
func (s *Store) ListFirings(ctx context.Context, limit int) ([]*AlertFiring, error) {
	if limit <= 0 {
		limit = 100
	}
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, rule_id, COALESCE(agent_id,''), started_at, resolved_at,
		        COALESCE(last_value,0), COALESCE(summary,''), notified
		 FROM alert_firings ORDER BY started_at DESC LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*AlertFiring
	for rows.Next() {
		var f AlertFiring
		var started string
		var resolved sql.NullString
		var notified int
		if err := rows.Scan(&f.ID, &f.RuleID, &f.AgentID, &started, &resolved,
			&f.LastValue, &f.Summary, &notified); err != nil {
			return nil, err
		}
		f.StartedAt, _ = time.Parse(time.RFC3339, started)
		if resolved.Valid {
			t, _ := time.Parse(time.RFC3339, resolved.String)
			f.ResolvedAt = &t
		}
		f.Notified = notified != 0
		out = append(out, &f)
	}
	return out, rows.Err()
}

// NotificationChannel persists how to deliver alerts.
type NotificationChannel struct {
	ID        int64
	Name      string
	Kind      string // email | webhook
	Config    map[string]any
	Enabled   bool
	CreatedAt time.Time
}

// CreateChannel persists a new channel.
func (s *Store) CreateChannel(ctx context.Context, ch *NotificationChannel) error {
	if ch.CreatedAt.IsZero() {
		ch.CreatedAt = time.Now().UTC()
	}
	cfg, _ := json.Marshal(ch.Config)
	res, err := s.DB.ExecContext(ctx,
		`INSERT INTO notification_channels(name, kind, config_json, enabled, created_at)
		 VALUES(?,?,?,?,?)`,
		ch.Name, ch.Kind, string(cfg), boolToInt(ch.Enabled),
		ch.CreatedAt.Format(time.RFC3339))
	if err != nil {
		return err
	}
	ch.ID, _ = res.LastInsertId()
	return nil
}

// ListChannels returns all channels.
func (s *Store) ListChannels(ctx context.Context) ([]*NotificationChannel, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, name, kind, config_json, enabled, created_at FROM notification_channels ORDER BY id ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*NotificationChannel
	for rows.Next() {
		var ch NotificationChannel
		var cfg, created string
		var enabled int
		if err := rows.Scan(&ch.ID, &ch.Name, &ch.Kind, &cfg, &enabled, &created); err != nil {
			return nil, err
		}
		ch.Enabled = enabled != 0
		_ = json.Unmarshal([]byte(cfg), &ch.Config)
		ch.CreatedAt, _ = time.Parse(time.RFC3339, created)
		out = append(out, &ch)
	}
	return out, rows.Err()
}

// GetChannel returns a single channel.
func (s *Store) GetChannel(ctx context.Context, id int64) (*NotificationChannel, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, name, kind, config_json, enabled, created_at FROM notification_channels WHERE id = ?`, id)
	var ch NotificationChannel
	var cfg, created string
	var enabled int
	if err := row.Scan(&ch.ID, &ch.Name, &ch.Kind, &cfg, &enabled, &created); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	ch.Enabled = enabled != 0
	_ = json.Unmarshal([]byte(cfg), &ch.Config)
	ch.CreatedAt, _ = time.Parse(time.RFC3339, created)
	return &ch, nil
}

// DeleteChannel removes a channel.
func (s *Store) DeleteChannel(ctx context.Context, id int64) error {
	_, err := s.DB.ExecContext(ctx, `DELETE FROM notification_channels WHERE id = ?`, id)
	return err
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

func nullInt64(p *int64) any {
	if p == nil {
		return nil
	}
	return *p
}
