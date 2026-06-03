package store

import (
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/walljm/jmwagent/internal/server/dek"
)

// AlertRule represents a configured alert.
type AlertRule struct {
	ID              int64
	Name            string
	Enabled         bool
	Metric          string // legacy: cpu_pct | mem_pct | disk_pct | offline_minutes
	MetricKind      string // v2: numeric_snapshot | disk_usage | temperature | offline | source_health
	MetricPath      string // v2: sub-path within the kind (e.g., sensor name, device name)
	Op              string // gt | lt
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
		`INSERT INTO alert_rules(name, enabled, metric, metric_kind, metric_path, op, threshold, duration_seconds, target_kind, target_id, severity, channel_id, created_at)
		 VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)`,
		r.Name, boolToInt(r.Enabled), r.Metric, r.MetricKind, r.MetricPath, r.Op, r.Threshold, r.DurationSeconds,
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
		`SELECT id, name, enabled, metric, metric_kind, metric_path, op, threshold, duration_seconds,
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
		if err := rows.Scan(&r.ID, &r.Name, &enabled, &r.Metric, &r.MetricKind, &r.MetricPath, &r.Op, &r.Threshold,
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
	Open    int // firings with resolved_at IS NULL
	Last24h int // firings whose started_at is within the last 24h
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

// ChannelSecretFields maps channel kinds to the config keys that contain secrets.
var ChannelSecretFields = map[string][]string{
	"webhook": {"url"},
	"email":   {"password"},
}

// encryptChannelConfig encrypts secret fields in a channel config map.
// Returns the JSON string with secrets encrypted.
func encryptChannelConfig(cfg map[string]any, kind string, key *dek.Key) (string, error) {
	if key == nil {
		b, err := json.Marshal(cfg)
		return string(b), err
	}
	fields := ChannelSecretFields[kind]
	if len(fields) == 0 {
		b, err := json.Marshal(cfg)
		return string(b), err
	}
	// Work on a copy to avoid mutating the caller's map.
	out := make(map[string]any, len(cfg))
	for k, v := range cfg {
		out[k] = v
	}
	for _, f := range fields {
		val, ok := out[f]
		if !ok {
			continue
		}
		str, isStr := val.(string)
		if !isStr || str == "" || str == secretSentinel {
			continue
		}
		enc, err := key.Encrypt(str)
		if err != nil {
			return "", err
		}
		out[f] = enc
	}
	b, err := json.Marshal(out)
	return string(b), err
}

// decryptChannelConfig decrypts secret fields in a channel config JSON string.
// Returns the parsed config map with secrets decrypted.
func decryptChannelConfig(cfgJSON, kind string, key *dek.Key) map[string]any {
	var m map[string]any
	if err := json.Unmarshal([]byte(cfgJSON), &m); err != nil {
		return nil
	}
	if key == nil {
		return m
	}
	fields := ChannelSecretFields[kind]
	for _, f := range fields {
		val, ok := m[f]
		if !ok {
			continue
		}
		str, isStr := val.(string)
		if !isStr || str == "" {
			continue
		}
		// Legacy plaintext detection: if not valid base64, leave as-is.
		if _, b64Err := base64.StdEncoding.DecodeString(str); b64Err != nil {
			continue
		}
		dec, err := key.Decrypt(str)
		if err != nil {
			slog.Warn("decrypt channel config field failed",
				"field", f, "kind", kind)
			continue
		}
		m[f] = dec
	}
	return m
}

// maskChannelConfig replaces secret fields with the sentinel value for UI display.
func maskChannelConfig(cfg map[string]any, kind string) map[string]any {
	fields := ChannelSecretFields[kind]
	if len(fields) == 0 {
		return cfg
	}
	out := make(map[string]any, len(cfg))
	for k, v := range cfg {
		out[k] = v
	}
	for _, f := range fields {
		if val, ok := out[f]; ok {
			str, isStr := val.(string)
			if isStr && str != "" {
				out[f] = secretSentinel
			}
		}
	}
	return out
}

// CreateChannel persists a new channel. Secret fields are encrypted with the DEK.
func (s *Store) CreateChannel(ctx context.Context, ch *NotificationChannel) error {
	if ch.CreatedAt.IsZero() {
		ch.CreatedAt = time.Now().UTC()
	}
	cfg, err := encryptChannelConfig(ch.Config, ch.Kind, s.DataKey)
	if err != nil {
		return err
	}
	res, err := s.DB.ExecContext(ctx,
		`INSERT INTO notification_channels(name, kind, config_json, enabled, created_at)
		 VALUES(?,?,?,?,?)`,
		ch.Name, ch.Kind, cfg, boolToInt(ch.Enabled),
		ch.CreatedAt.Format(time.RFC3339))
	if err != nil {
		return err
	}
	ch.ID, _ = res.LastInsertId()
	return nil
}

// ListChannels returns all channels with secret fields masked for UI display.
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
		// Decrypt then mask: decrypt so we can tell if a value is set,
		// then mask for UI display.
		decrypted := decryptChannelConfig(cfg, ch.Kind, s.DataKey)
		ch.Config = maskChannelConfig(decrypted, ch.Kind)
		ch.CreatedAt, _ = time.Parse(time.RFC3339, created)
		out = append(out, &ch)
	}
	return out, rows.Err()
}

// GetChannel returns a single channel with secrets decrypted (for notification dispatch).
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
	ch.Config = decryptChannelConfig(cfg, ch.Kind, s.DataKey)
	ch.CreatedAt, _ = time.Parse(time.RFC3339, created)
	return &ch, nil
}

// DeleteChannel removes a channel.
func (s *Store) DeleteChannel(ctx context.Context, id int64) error {
	_, err := s.DB.ExecContext(ctx, `DELETE FROM notification_channels WHERE id = ?`, id)
	return err
}

// RotateChannelSecrets decrypts all channel secrets with decryptKey and
// re-encrypts them with encryptKey. Returns the number of channels processed.
func (s *Store) RotateChannelSecrets(ctx context.Context, decryptKey, encryptKey *dek.Key) (int, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, kind, config_json FROM notification_channels`)
	if err != nil {
		return 0, err
	}
	defer rows.Close()

	type chanRow struct {
		id        int64
		kind, cfg string
	}
	var channels []chanRow
	for rows.Next() {
		var cr chanRow
		if err := rows.Scan(&cr.id, &cr.kind, &cr.cfg); err != nil {
			return 0, err
		}
		channels = append(channels, cr)
	}
	if err := rows.Err(); err != nil {
		return 0, err
	}

	count := 0
	for _, cr := range channels {
		fields := ChannelSecretFields[cr.kind]
		if len(fields) == 0 {
			continue
		}
		// Decrypt with old key.
		decrypted := decryptChannelConfig(cr.cfg, cr.kind, decryptKey)
		if decrypted == nil {
			continue
		}
		// Re-encrypt with new key.
		encrypted, err := encryptChannelConfig(decrypted, cr.kind, encryptKey)
		if err != nil {
			return count, fmt.Errorf("channel %d: encrypt: %w", cr.id, err)
		}
		if _, err := s.DB.ExecContext(ctx,
			`UPDATE notification_channels SET config_json = ? WHERE id = ?`,
			encrypted, cr.id); err != nil {
			return count, fmt.Errorf("channel %d: update: %w", cr.id, err)
		}
		count++
	}
	return count, nil
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

// MaintenanceWindow is a time-based suppression window for alerts.
type MaintenanceWindow struct {
	ID         int64
	Name       string
	TargetKind string
	TargetID   string
	StartsAt   time.Time
	EndsAt     time.Time
	Recurrence string
	CreatedAt  time.Time
	CreatedBy  string
}

// CreateMaintenanceWindow inserts a new window.
func (s *Store) CreateMaintenanceWindow(ctx context.Context, mw *MaintenanceWindow) error {
	if mw.CreatedAt.IsZero() {
		mw.CreatedAt = time.Now().UTC()
	}
	res, err := s.DB.ExecContext(ctx,
		`INSERT INTO maintenance_windows (name, target_kind, target_id, starts_at, ends_at, recurrence, created_at, created_by)
		 VALUES (?,?,?,?,?,?,?,?)`,
		mw.Name, mw.TargetKind, mw.TargetID,
		mw.StartsAt.Format(time.RFC3339), mw.EndsAt.Format(time.RFC3339),
		mw.Recurrence, mw.CreatedAt.Format(time.RFC3339), mw.CreatedBy)
	if err != nil {
		return err
	}
	mw.ID, _ = res.LastInsertId()
	return nil
}

// ListMaintenanceWindows returns all windows, ordered by start time.
func (s *Store) ListMaintenanceWindows(ctx context.Context) ([]*MaintenanceWindow, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, name, target_kind, target_id, starts_at, ends_at, recurrence, created_at, created_by
		 FROM maintenance_windows ORDER BY starts_at ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*MaintenanceWindow
	for rows.Next() {
		var mw MaintenanceWindow
		var startsAt, endsAt, createdAt string
		if err := rows.Scan(&mw.ID, &mw.Name, &mw.TargetKind, &mw.TargetID,
			&startsAt, &endsAt, &mw.Recurrence, &createdAt, &mw.CreatedBy); err != nil {
			return nil, err
		}
		mw.StartsAt, _ = time.Parse(time.RFC3339, startsAt)
		mw.EndsAt, _ = time.Parse(time.RFC3339, endsAt)
		mw.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
		out = append(out, &mw)
	}
	return out, rows.Err()
}

// DeleteMaintenanceWindow removes a window by ID.
func (s *Store) DeleteMaintenanceWindow(ctx context.Context, id int64) error {
	_, err := s.DB.ExecContext(ctx, `DELETE FROM maintenance_windows WHERE id = ?`, id)
	return err
}
