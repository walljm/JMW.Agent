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

	"github.com/google/uuid"
	"github.com/walljm/jmwagent/internal/server/dek"
)

// Source is a pipeline input source.
type Source struct {
	ID                    string
	Name                  string
	Kind                  string
	Enabled               bool
	AgentID               string
	ConfigJSON            string // plaintext config (secrets decrypted on read)
	PollIntervalSeconds   *int
	LastSuccessAt         *time.Time
	LastErrorAt           *time.Time
	LastErrorMessage      string
	ConsecutiveErrorCount int
	CreatedAt             time.Time
	UpdatedAt             time.Time
}

// SecretFields is a per-kind mapping of field names that are secret-bearing
// inside config_json. The DEK encrypts these on write and decrypts on read.
var SecretFields = map[string][]string{
	"terrain-dhcp": {"password", "token"},
	"terrain-dns":  {"password", "token"},
	"snmp-poller":  {"community", "auth_password", "priv_password"},
	"nmap-scanner": {},
	"agent":        {},
	"user-input":   {},
}

// secretSentinel is the value returned in place of decrypted secrets on read.
const secretSentinel = "<set>"

// ValidSourceKinds returns the set of recognized source kinds.
func ValidSourceKinds() []string {
	kinds := make([]string, 0, len(SecretFields))
	for k := range SecretFields {
		kinds = append(kinds, k)
	}
	return kinds
}

// CreateSource inserts a new source. Secret fields in configJSON are encrypted.
func (s *Store) CreateSource(ctx context.Context, src *Source, key *dek.Key) error {
	if _, ok := SecretFields[src.Kind]; !ok {
		return fmt.Errorf("unknown source kind %q", src.Kind)
	}
	if err := rejectSentinelValues(src.ConfigJSON, src.Kind); err != nil {
		return err
	}
	if src.ID == "" {
		src.ID = uuid.New().String()
	}
	now := time.Now().UTC()
	src.CreatedAt = now
	src.UpdatedAt = now

	encrypted, err := encryptSecrets(src.ConfigJSON, src.Kind, key)
	if err != nil {
		return err
	}

	_, err = s.DB.ExecContext(ctx,
		`INSERT INTO sources (id, name, kind, enabled, agent_id, config_json, poll_interval_seconds, created_at, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		src.ID, src.Name, src.Kind, boolToInt(src.Enabled), nullStr(src.AgentID),
		encrypted, src.PollIntervalSeconds,
		now.Format(time.RFC3339), now.Format(time.RFC3339))
	return err
}

// GetSource retrieves a source by ID. Secret fields are masked with the sentinel.
func (s *Store) GetSource(ctx context.Context, id string) (*Source, error) {
	src := &Source{}
	var enabled int
	var agentID sql.NullString
	var pollInterval sql.NullInt64
	var lastSuccess, lastError, lastErrMsg sql.NullString
	var createdAt, updatedAt string

	err := s.DB.QueryRowContext(ctx,
		`SELECT id, name, kind, enabled, agent_id, config_json, poll_interval_seconds,
		        last_success_at, last_error_at, last_error_message, consecutive_error_count,
		        created_at, updated_at
		 FROM sources WHERE id = ?`, id).Scan(
		&src.ID, &src.Name, &src.Kind, &enabled, &agentID, &src.ConfigJSON,
		&pollInterval, &lastSuccess, &lastError, &lastErrMsg,
		&src.ConsecutiveErrorCount, &createdAt, &updatedAt)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrSourceNotFound
		}
		return nil, err
	}

	src.Enabled = enabled != 0
	if agentID.Valid {
		src.AgentID = agentID.String
	}
	if pollInterval.Valid {
		v := int(pollInterval.Int64)
		src.PollIntervalSeconds = &v
	}
	if lastSuccess.Valid {
		t, _ := time.Parse(time.RFC3339, lastSuccess.String)
		src.LastSuccessAt = &t
	}
	if lastError.Valid {
		t, _ := time.Parse(time.RFC3339, lastError.String)
		src.LastErrorAt = &t
	}
	if lastErrMsg.Valid {
		src.LastErrorMessage = lastErrMsg.String
	}
	src.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
	src.UpdatedAt, _ = time.Parse(time.RFC3339, updatedAt)

	// Mask secrets for API reads.
	src.ConfigJSON = maskSecrets(src.ConfigJSON, src.Kind)
	return src, nil
}

// GetSourceWithSecrets retrieves a source with secrets decrypted. For adapter use only.
func (s *Store) GetSourceWithSecrets(ctx context.Context, id string, key *dek.Key) (*Source, error) {
	src := &Source{}
	var enabled int
	var agentID sql.NullString
	var pollInterval sql.NullInt64
	var lastSuccess, lastError, lastErrMsg sql.NullString
	var createdAt, updatedAt string
	var rawConfig string

	err := s.DB.QueryRowContext(ctx,
		`SELECT id, name, kind, enabled, agent_id, config_json, poll_interval_seconds,
		        last_success_at, last_error_at, last_error_message, consecutive_error_count,
		        created_at, updated_at
		 FROM sources WHERE id = ?`, id).Scan(
		&src.ID, &src.Name, &src.Kind, &enabled, &agentID, &rawConfig,
		&pollInterval, &lastSuccess, &lastError, &lastErrMsg,
		&src.ConsecutiveErrorCount, &createdAt, &updatedAt)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrSourceNotFound
		}
		return nil, err
	}

	src.Enabled = enabled != 0
	if agentID.Valid {
		src.AgentID = agentID.String
	}
	if pollInterval.Valid {
		v := int(pollInterval.Int64)
		src.PollIntervalSeconds = &v
	}
	if lastSuccess.Valid {
		t, _ := time.Parse(time.RFC3339, lastSuccess.String)
		src.LastSuccessAt = &t
	}
	if lastError.Valid {
		t, _ := time.Parse(time.RFC3339, lastError.String)
		src.LastErrorAt = &t
	}
	if lastErrMsg.Valid {
		src.LastErrorMessage = lastErrMsg.String
	}
	src.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
	src.UpdatedAt, _ = time.Parse(time.RFC3339, updatedAt)

	// Decrypt secrets.
	decrypted, err := decryptSecrets(rawConfig, src.Kind, key)
	if err != nil {
		return nil, err
	}
	src.ConfigJSON = decrypted
	return src, nil
}

// ListSources returns all sources, optionally filtered by kind.
// Secret fields are masked.
func (s *Store) ListSources(ctx context.Context, kind string) ([]*Source, error) {
	query := `SELECT id, name, kind, enabled, agent_id, config_json, poll_interval_seconds,
	                  last_success_at, last_error_at, last_error_message, consecutive_error_count,
	                  created_at, updated_at
	           FROM sources`
	var args []any
	if kind != "" {
		query += ` WHERE kind = ?`
		args = append(args, kind)
	}
	query += ` ORDER BY name`

	rows, err := s.DB.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var result []*Source
	for rows.Next() {
		src := &Source{}
		var enabled int
		var agentID sql.NullString
		var pollInterval sql.NullInt64
		var lastSuccess, lastError, lastErrMsg sql.NullString
		var createdAt, updatedAt string

		if err := rows.Scan(&src.ID, &src.Name, &src.Kind, &enabled, &agentID, &src.ConfigJSON,
			&pollInterval, &lastSuccess, &lastError, &lastErrMsg,
			&src.ConsecutiveErrorCount, &createdAt, &updatedAt); err != nil {
			return nil, err
		}
		src.Enabled = enabled != 0
		if agentID.Valid {
			src.AgentID = agentID.String
		}
		if pollInterval.Valid {
			v := int(pollInterval.Int64)
			src.PollIntervalSeconds = &v
		}
		if lastSuccess.Valid {
			t, _ := time.Parse(time.RFC3339, lastSuccess.String)
			src.LastSuccessAt = &t
		}
		if lastError.Valid {
			t, _ := time.Parse(time.RFC3339, lastError.String)
			src.LastErrorAt = &t
		}
		if lastErrMsg.Valid {
			src.LastErrorMessage = lastErrMsg.String
		}
		src.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
		src.UpdatedAt, _ = time.Parse(time.RFC3339, updatedAt)
		src.ConfigJSON = maskSecrets(src.ConfigJSON, src.Kind)
		result = append(result, src)
	}
	return result, rows.Err()
}

// ListEnabledSources returns sources that are enabled and have a poll interval.
// Secrets are decrypted (for adapter consumption).
func (s *Store) ListEnabledSources(ctx context.Context, key *dek.Key) ([]*Source, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, name, kind, config_json, poll_interval_seconds,
		        last_success_at, consecutive_error_count
		 FROM sources WHERE enabled = 1 AND poll_interval_seconds IS NOT NULL
		 ORDER BY name`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var result []*Source
	for rows.Next() {
		src := &Source{}
		var rawConfig string
		var pollInterval sql.NullInt64
		var lastSuccess sql.NullString

		if err := rows.Scan(&src.ID, &src.Name, &src.Kind, &rawConfig,
			&pollInterval, &lastSuccess, &src.ConsecutiveErrorCount); err != nil {
			return nil, err
		}
		if pollInterval.Valid {
			v := int(pollInterval.Int64)
			src.PollIntervalSeconds = &v
		}
		if lastSuccess.Valid {
			t, _ := time.Parse(time.RFC3339, lastSuccess.String)
			src.LastSuccessAt = &t
		}
		decrypted, err := decryptSecrets(rawConfig, src.Kind, key)
		if err != nil {
			return nil, err
		}
		src.ConfigJSON = decrypted
		src.Enabled = true
		result = append(result, src)
	}
	return result, rows.Err()
}

// DeleteSource removes a source by ID.
func (s *Store) DeleteSource(ctx context.Context, id string) error {
	result, err := s.DB.ExecContext(ctx, `DELETE FROM sources WHERE id = ?`, id)
	if err != nil {
		return err
	}
	n, _ := result.RowsAffected()
	if n == 0 {
		return ErrSourceNotFound
	}
	return nil
}

// UpdateSourceSecrets updates only the secret fields in a source's config_json.
// Non-secret fields are preserved. This is the write-only PATCH endpoint's backend.
func (s *Store) UpdateSourceSecrets(ctx context.Context, id string, secretsJSON string, key *dek.Key) error {
	// Read current stored config (encrypted).
	var rawConfig, kind string
	err := s.DB.QueryRowContext(ctx,
		`SELECT config_json, kind FROM sources WHERE id = ?`, id).Scan(&rawConfig, &kind)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ErrSourceNotFound
		}
		return err
	}

	// Decrypt current config.
	current, err := decryptSecrets(rawConfig, kind, key)
	if err != nil {
		return err
	}

	// Merge incoming secrets into current config.
	merged, err := mergeSecrets(current, secretsJSON, kind)
	if err != nil {
		return err
	}

	// Encrypt and store.
	encrypted, err := encryptSecrets(merged, kind, key)
	if err != nil {
		return err
	}

	now := time.Now().UTC().Format(time.RFC3339)
	_, err = s.DB.ExecContext(ctx,
		`UPDATE sources SET config_json = ?, updated_at = ? WHERE id = ?`,
		encrypted, now, id)
	return err
}

// RecordSourcePollResult updates the poll-state fields after a poll attempt.
func (s *Store) RecordSourcePollResult(ctx context.Context, id string, pollErr error) error {
	now := time.Now().UTC().Format(time.RFC3339)
	if pollErr == nil {
		_, err := s.DB.ExecContext(ctx,
			`UPDATE sources SET last_success_at = ?, consecutive_error_count = 0, updated_at = ? WHERE id = ?`,
			now, now, id)
		return err
	}
	_, err := s.DB.ExecContext(ctx,
		`UPDATE sources SET last_error_at = ?, last_error_message = ?,
		        consecutive_error_count = consecutive_error_count + 1, updated_at = ?
		 WHERE id = ?`,
		now, pollErr.Error(), now, id)
	return err
}

// HasSourceOfKind returns true if at least one source of the given kind exists.
func (s *Store) HasSourceOfKind(ctx context.Context, kind string) (bool, error) {
	var count int
	err := s.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM sources WHERE kind = ?`, kind).Scan(&count)
	return count > 0, err
}

// EnsureAgentSource returns the source ID for an agent, creating one if needed.
// Agent sources have kind="agent" and are keyed by agent_id.
func (s *Store) EnsureAgentSource(ctx context.Context, agentID, hostname string) (string, error) {
	var id string
	err := s.DB.QueryRowContext(ctx,
		`SELECT id FROM sources WHERE kind = 'agent' AND agent_id = ?`, agentID).Scan(&id)
	if err == nil {
		return id, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return "", err
	}
	// Create a new source for this agent.
	now := time.Now().UTC().Format(time.RFC3339)
	id = uuid.New().String()
	_, err = s.DB.ExecContext(ctx,
		`INSERT INTO sources (id, name, kind, enabled, agent_id, config_json, created_at, updated_at)
		 VALUES (?, ?, 'agent', 1, ?, '{}', ?, ?)`,
		id, "agent:"+hostname, agentID, now, now)
	if err != nil {
		return "", err
	}
	return id, nil
}

// EnsureTerrainSource returns the source ID for the terrain poller, creating
// one if needed. There is at most one terrain source (kind="terrain-dhcp").
func (s *Store) EnsureTerrainSource(ctx context.Context) (string, error) {
	var id string
	err := s.DB.QueryRowContext(ctx,
		`SELECT id FROM sources WHERE kind = 'terrain-dhcp' LIMIT 1`).Scan(&id)
	if err == nil {
		return id, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return "", err
	}
	now := time.Now().UTC().Format(time.RFC3339)
	id = uuid.New().String()
	_, err = s.DB.ExecContext(ctx,
		`INSERT INTO sources (id, name, kind, enabled, config_json, created_at, updated_at)
		 VALUES (?, 'Terrain DHCP', 'terrain-dhcp', 1, '{}', ?, ?)`,
		id, now, now)
	if err != nil {
		return "", err
	}
	return id, nil
}

// RotateSecrets decrypts all source secrets with decryptKey and re-encrypts
// them with encryptKey. Returns the number of sources processed.
func (s *Store) RotateSecrets(ctx context.Context, decryptKey, encryptKey *dek.Key) (int, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT id, kind, config_json FROM sources`)
	if err != nil {
		return 0, err
	}
	defer rows.Close()

	type sourceRow struct {
		id, kind, configJSON string
	}
	var sources []sourceRow
	for rows.Next() {
		var sr sourceRow
		if err := rows.Scan(&sr.id, &sr.kind, &sr.configJSON); err != nil {
			return 0, err
		}
		sources = append(sources, sr)
	}
	if err := rows.Err(); err != nil {
		return 0, err
	}

	count := 0
	for _, sr := range sources {
		fields := SecretFields[sr.kind]
		if len(fields) == 0 {
			continue
		}
		// Decrypt with old key.
		decrypted, err := decryptSecrets(sr.configJSON, sr.kind, decryptKey)
		if err != nil {
			return count, fmt.Errorf("source %s: decrypt: %w", sr.id, err)
		}
		// Re-encrypt with new key.
		encrypted, err := encryptSecrets(decrypted, sr.kind, encryptKey)
		if err != nil {
			return count, fmt.Errorf("source %s: encrypt: %w", sr.id, err)
		}
		now := time.Now().UTC().Format(time.RFC3339)
		if _, err := s.DB.ExecContext(ctx,
			`UPDATE sources SET config_json = ?, updated_at = ? WHERE id = ?`,
			encrypted, now, sr.id); err != nil {
			return count, fmt.Errorf("source %s: update: %w", sr.id, err)
		}
		count++
	}
	return count, nil
}

// --- helpers ---

func encryptSecrets(configJSON, kind string, key *dek.Key) (string, error) {
	if key == nil {
		return configJSON, nil
	}
	fields := SecretFields[kind]
	if len(fields) == 0 {
		return configJSON, nil
	}

	var m map[string]any
	if err := json.Unmarshal([]byte(configJSON), &m); err != nil {
		return configJSON, nil // not valid JSON, store as-is
	}

	for _, f := range fields {
		val, ok := m[f]
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
		m[f] = enc
	}

	b, err := json.Marshal(m)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

func decryptSecrets(configJSON, kind string, key *dek.Key) (string, error) {
	if key == nil {
		return configJSON, nil
	}
	fields := SecretFields[kind]
	if len(fields) == 0 {
		return configJSON, nil
	}

	var m map[string]any
	if err := json.Unmarshal([]byte(configJSON), &m); err != nil {
		return configJSON, nil
	}

	for _, f := range fields {
		val, ok := m[f]
		if !ok {
			continue
		}
		str, isStr := val.(string)
		if !isStr || str == "" {
			continue
		}
		// Distinguish legacy plaintext from genuine corruption:
		// if it's valid base64 but fails to decrypt, that's corruption.
		if _, b64Err := base64.StdEncoding.DecodeString(str); b64Err != nil {
			// Not base64 — legacy plaintext value, leave as-is.
			continue
		}
		dec, err := key.Decrypt(str)
		if err != nil {
			// Valid base64 but decrypt failed — log warning, leave value as-is
			// rather than returning corrupt data to adapters.
			slog.Warn("decrypt secret field failed (possible key mismatch)",
				"field", f, "kind", kind)
			continue
		}
		m[f] = dec
	}

	b, err := json.Marshal(m)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

func maskSecrets(configJSON, kind string) string {
	fields := SecretFields[kind]
	if len(fields) == 0 {
		return configJSON
	}

	var m map[string]any
	if err := json.Unmarshal([]byte(configJSON), &m); err != nil {
		return configJSON
	}

	for _, f := range fields {
		if val, ok := m[f]; ok {
			str, isStr := val.(string)
			if isStr && str != "" {
				m[f] = secretSentinel
			}
		}
	}

	b, _ := json.Marshal(m)
	return string(b)
}

func mergeSecrets(currentJSON, incomingJSON, kind string) (string, error) {
	fields := SecretFields[kind]
	if len(fields) == 0 {
		return currentJSON, nil
	}

	var current, incoming map[string]any
	if err := json.Unmarshal([]byte(currentJSON), &current); err != nil {
		return "", err
	}
	if err := json.Unmarshal([]byte(incomingJSON), &incoming); err != nil {
		return "", err
	}

	// Only copy secret fields from incoming into current.
	for _, f := range fields {
		if val, ok := incoming[f]; ok {
			str, isStr := val.(string)
			if isStr && str != "" && str != secretSentinel {
				current[f] = str
			}
		}
	}

	b, err := json.Marshal(current)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

func nullStr(s string) sql.NullString {
	if s == "" {
		return sql.NullString{}
	}
	return sql.NullString{String: s, Valid: true}
}

// ErrSourceNotFound is returned when a source ID doesn't exist.
var ErrSourceNotFound = errors.New("source not found")

// rejectSentinelValues returns an error if any secret field contains the sentinel.
func rejectSentinelValues(configJSON, kind string) error {
	fields := SecretFields[kind]
	if len(fields) == 0 {
		return nil
	}
	var m map[string]any
	if err := json.Unmarshal([]byte(configJSON), &m); err != nil {
		return nil
	}
	for _, f := range fields {
		if val, ok := m[f]; ok {
			if str, isStr := val.(string); isStr && str == secretSentinel {
				return fmt.Errorf("secret field %q cannot be the sentinel value", f)
			}
		}
	}
	return nil
}
