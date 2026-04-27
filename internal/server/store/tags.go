package store

import (
	"context"
	"sort"
	"strings"
	"unicode"
)

// Tag target kinds.
const (
	TagTargetAgent  = "agent"
	TagTargetDevice = "device"
)

// NormalizeTag lowercases and trims a tag name. Returns "" for tags that are
// empty or contain only invalid characters. Allowed characters: letters,
// digits, '-', '_', '.', '/', ':'. Spaces are collapsed to '-'.
func NormalizeTag(raw string) string {
	raw = strings.TrimSpace(strings.ToLower(raw))
	if raw == "" {
		return ""
	}
	var b strings.Builder
	b.Grow(len(raw))
	for _, r := range raw {
		switch {
		case unicode.IsLetter(r) || unicode.IsDigit(r):
			b.WriteRune(r)
		case r == '-' || r == '_' || r == '.' || r == '/' || r == ':':
			b.WriteRune(r)
		case unicode.IsSpace(r):
			b.WriteByte('-')
		}
	}
	out := strings.Trim(b.String(), "-_./:")
	if len(out) > 64 {
		out = out[:64]
	}
	return out
}

// ParseTagInput parses a comma- or whitespace-separated tag string into a
// deduplicated, normalized, sorted list. Useful for form input.
func ParseTagInput(s string) []string {
	fields := strings.FieldsFunc(s, func(r rune) bool {
		return r == ',' || r == ';' || r == '\n' || r == '\r' || r == '\t'
	})
	seen := make(map[string]struct{}, len(fields))
	out := make([]string, 0, len(fields))
	for _, f := range fields {
		t := NormalizeTag(f)
		if t == "" {
			continue
		}
		if _, dup := seen[t]; dup {
			continue
		}
		seen[t] = struct{}{}
		out = append(out, t)
	}
	sort.Strings(out)
	return out
}

// SetTagsForTarget replaces all tag assignments for (kind, id) with the
// provided set. Tag rows are created on demand. Caller-supplied names are
// normalized; empty/invalid entries are dropped.
func (s *Store) SetTagsForTarget(ctx context.Context, kind, id string, tags []string) error {
	clean := make([]string, 0, len(tags))
	seen := make(map[string]struct{}, len(tags))
	for _, t := range tags {
		n := NormalizeTag(t)
		if n == "" {
			continue
		}
		if _, dup := seen[n]; dup {
			continue
		}
		seen[n] = struct{}{}
		clean = append(clean, n)
	}

	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if _, err := tx.ExecContext(ctx,
		`DELETE FROM tag_assignments WHERE target_kind = ? AND target_id = ?`,
		kind, id); err != nil {
		return err
	}
	for _, name := range clean {
		if _, err := tx.ExecContext(ctx,
			`INSERT OR IGNORE INTO tags(name) VALUES(?)`, name); err != nil {
			return err
		}
		var tagID int64
		if err := tx.QueryRowContext(ctx,
			`SELECT id FROM tags WHERE name = ?`, name).Scan(&tagID); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx,
			`INSERT OR IGNORE INTO tag_assignments(tag_id, target_kind, target_id) VALUES(?,?,?)`,
			tagID, kind, id); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ListTagsForTarget returns the sorted set of tag names assigned to (kind, id).
func (s *Store) ListTagsForTarget(ctx context.Context, kind, id string) ([]string, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT t.name FROM tags t
		 JOIN tag_assignments a ON a.tag_id = t.id
		 WHERE a.target_kind = ? AND a.target_id = ?
		 ORDER BY t.name`, kind, id)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []string
	for rows.Next() {
		var n string
		if err := rows.Scan(&n); err != nil {
			return nil, err
		}
		out = append(out, n)
	}
	return out, rows.Err()
}

// ListTagsForTargets returns a map[target_id][]tagName for all target_ids of
// the given kind. Useful for list views to avoid N+1 queries.
func (s *Store) ListTagsForTargets(ctx context.Context, kind string) (map[string][]string, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT a.target_id, t.name FROM tags t
		 JOIN tag_assignments a ON a.tag_id = t.id
		 WHERE a.target_kind = ?
		 ORDER BY a.target_id, t.name`, kind)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string][]string)
	for rows.Next() {
		var id, name string
		if err := rows.Scan(&id, &name); err != nil {
			return nil, err
		}
		out[id] = append(out[id], name)
	}
	return out, rows.Err()
}

// UpdateAgentNotes sets the free-form description/notes field on an agent.
func (s *Store) UpdateAgentNotes(ctx context.Context, id, notes string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE agents SET notes = ? WHERE id = ?`, notes, id)
	return err
}

// UpdateDeviceNotes sets the free-form description/notes field on a device.
func (s *Store) UpdateDeviceNotes(ctx context.Context, id, notes string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE devices SET notes = ? WHERE id = ?`, notes, id)
	return err
}
