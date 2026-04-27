package store

import (
	"context"
	"encoding/json"
	"time"
)

// Event severity values.
const (
	SeverityInfo     = "info"
	SeverityWarning  = "warning"
	SeverityCritical = "critical"
)

// Event represents a row in the activity log.
type Event struct {
	ID         int64
	Timestamp  time.Time
	Type       string
	Severity   string
	SourceKind string
	SourceID   string
	Summary    string
	Detail     map[string]any
}

// LogEvent writes an event to the activity log.
func (s *Store) LogEvent(ctx context.Context, e *Event) error {
	if e.Timestamp.IsZero() {
		e.Timestamp = time.Now().UTC()
	}
	var detailJSON string
	if len(e.Detail) > 0 {
		b, _ := json.Marshal(e.Detail)
		detailJSON = string(b)
	}
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO events(ts, type, severity, source_kind, source_id, summary, detail_json)
		 VALUES(?,?,?,?,?,?,?)`,
		e.Timestamp.UTC().Format(time.RFC3339), e.Type, e.Severity,
		e.SourceKind, e.SourceID, e.Summary, detailJSON)
	return err
}

// ListEvents returns recent events, newest first, limited.
func (s *Store) ListEvents(ctx context.Context, limit int) ([]*Event, error) {
	if limit <= 0 {
		limit = 100
	}
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, ts, type, severity, COALESCE(source_kind,''), COALESCE(source_id,''),
		        summary, COALESCE(detail_json,'')
		 FROM events
		 ORDER BY ts DESC, id DESC
		 LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Event
	for rows.Next() {
		var e Event
		var ts, detailJSON string
		if err := rows.Scan(&e.ID, &ts, &e.Type, &e.Severity, &e.SourceKind, &e.SourceID,
			&e.Summary, &detailJSON); err != nil {
			return nil, err
		}
		e.Timestamp, _ = time.Parse(time.RFC3339, ts)
		if detailJSON != "" {
			_ = json.Unmarshal([]byte(detailJSON), &e.Detail)
		}
		out = append(out, &e)
	}
	return out, rows.Err()
}
