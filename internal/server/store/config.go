package store

import (
	"context"
	"database/sql"
	"encoding/base64"
	"errors"
	"fmt"
	"log/slog"
	"strconv"
	"time"

	"github.com/walljm/jmwagent/internal/server/dek"
	"github.com/walljm/jmwagent/internal/shared/duration"
)

// GetConfig returns a single config value by key. Returns ("", nil) if the
// key does not exist.
func (s *Store) GetConfig(ctx context.Context, key string) (string, error) {
	var val string
	err := s.DB.QueryRowContext(ctx, `SELECT value FROM config WHERE key = ?`, key).Scan(&val)
	if errors.Is(err, sql.ErrNoRows) {
		return "", nil
	}
	return val, err
}

// SetConfig upserts a config value.
func (s *Store) SetConfig(ctx context.Context, key, value string) error {
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO config (key, value) VALUES (?, ?)
		 ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
		key, value)
	return err
}

// GetAllConfig returns all config entries as a map.
func (s *Store) GetAllConfig(ctx context.Context) (map[string]string, error) {
	rows, err := s.DB.QueryContext(ctx, `SELECT key, value FROM config ORDER BY key`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string]string)
	for rows.Next() {
		var k, v string
		if err := rows.Scan(&k, &v); err != nil {
			return nil, err
		}
		out[k] = v
	}
	return out, rows.Err()
}

// GetAgentIntervals returns the three agent collection intervals from the
// config table. Falls back to sensible defaults if any key is missing or
// unparseable.
func (s *Store) GetAgentIntervals(ctx context.Context) (heartbeat, discovery, inventory time.Duration, err error) {
	heartbeat = 30 * time.Second
	discovery = 5 * time.Minute
	inventory = 24 * time.Hour

	cfg, err := s.GetAllConfig(ctx)
	if err != nil {
		return heartbeat, discovery, inventory, fmt.Errorf("get agent intervals: %w", err)
	}

	if v, ok := cfg["agent.heartbeat_interval_secs"]; ok {
		if d, e := parseDurationCompat(v); e == nil && d > 0 {
			heartbeat = d
		}
	}
	if v, ok := cfg["agent.discovery_interval_secs"]; ok {
		if d, e := parseDurationCompat(v); e == nil && d > 0 {
			discovery = d
		}
	}
	if v, ok := cfg["agent.inventory_interval_secs"]; ok {
		if d, e := parseDurationCompat(v); e == nil && d > 0 {
			inventory = d
		}
	}
	return heartbeat, discovery, inventory, nil
}

// GetSessionLifetime returns the configured session lifetime,
// defaulting to 7 days. Legacy plain-integer values are treated as hours
// (the historical unit for this key) rather than seconds.
func (s *Store) GetSessionLifetime(ctx context.Context) (time.Duration, error) {
	v, err := s.GetConfig(ctx, "auth.session_lifetime_hours")
	if err != nil {
		return 7 * 24 * time.Hour, err
	}
	if v == "" {
		return 7 * 24 * time.Hour, nil
	}
	if n, err := strconv.Atoi(v); err == nil {
		if n <= 0 {
			return 7 * 24 * time.Hour, nil
		}
		return time.Duration(n) * time.Hour, nil
	}
	d, err := parseDurationCompat(v)
	if err != nil || d <= 0 {
		return 7 * 24 * time.Hour, nil
	}
	return d, nil
}

// GetTerrainPollInterval returns the terrain poll interval,
// defaulting to 1 minute.
func (s *Store) GetTerrainPollInterval(ctx context.Context) (time.Duration, error) {
	v, err := s.GetConfig(ctx, "terrain.poll_interval_secs")
	if err != nil {
		return time.Minute, err
	}
	if v == "" {
		return time.Minute, nil
	}
	d, err := parseDurationCompat(v)
	if err != nil || d <= 0 {
		return time.Minute, nil
	}
	return d, nil
}

// parseDurationCompat parses a duration string, accepting both the new
// human-friendly format ("1d 3h 5m") and legacy plain integers (treated
// as seconds for agent/terrain intervals, or hours for session lifetime
// based on the calling context — callers that stored hours should have
// been migrated by 0027).
func parseDurationCompat(s string) (time.Duration, error) {
	if n, err := strconv.Atoi(s); err == nil {
		return time.Duration(n) * time.Second, nil
	}
	if d, err := time.ParseDuration(s); err == nil {
		return d, nil
	}
	return duration.Parse(s)
}

// terrainSecretKeys are config keys whose values are encrypted with the DEK.
var terrainSecretKeys = map[string]bool{
	"terrain.token":    true,
	"terrain.password": true,
}

// SetConfigEncrypted upserts a config value, encrypting it if the key is a
// known secret key and a DEK is available.
func (s *Store) SetConfigEncrypted(ctx context.Context, key, value string) error {
	if terrainSecretKeys[key] && value != "" && s.DataKey != nil {
		enc, err := s.DataKey.Encrypt(value)
		if err != nil {
			return fmt.Errorf("encrypt config %s: %w", key, err)
		}
		value = enc
	}
	return s.SetConfig(ctx, key, value)
}

// getConfigDecrypted reads a config value, decrypting it if the key is a
// known secret key and a DEK is available. Legacy plaintext values are
// returned as-is (graceful backwards compatibility).
func (s *Store) getConfigDecrypted(ctx context.Context, key string) (string, error) {
	val, err := s.GetConfig(ctx, key)
	if err != nil || val == "" {
		return val, err
	}
	if !terrainSecretKeys[key] || s.DataKey == nil {
		return val, nil
	}
	// Try to decrypt. If the value isn't valid base64, it's legacy plaintext.
	if _, b64Err := base64.StdEncoding.DecodeString(val); b64Err != nil {
		return val, nil // legacy plaintext
	}
	dec, err := s.DataKey.Decrypt(val)
	if err != nil {
		// Valid base64 but decrypt failed — log warning, return as-is.
		slog.Warn("decrypt config value failed (possible key mismatch)",
			"key", key)
		return val, nil
	}
	return dec, nil
}

// TerrainConfig holds the terrain poller connection settings from the DB.
type TerrainConfig struct {
	URL      string
	Token    string
	Username string
	Password string
}

// GetTerrainConfig reads terrain connection settings from the config table.
// Secret fields (token, password) are decrypted using the DEK.
func (s *Store) GetTerrainConfig(ctx context.Context) (TerrainConfig, error) {
	cfg, err := s.GetAllConfig(ctx)
	if err != nil {
		return TerrainConfig{}, err
	}
	tc := TerrainConfig{
		URL:      cfg["terrain.url"],
		Username: cfg["terrain.username"],
	}

	// Decrypt secret fields individually.
	tc.Token, err = s.getConfigDecrypted(ctx, "terrain.token")
	if err != nil {
		return TerrainConfig{}, err
	}
	tc.Password, err = s.getConfigDecrypted(ctx, "terrain.password")
	if err != nil {
		return TerrainConfig{}, err
	}
	return tc, nil
}

// RotateConfigSecrets decrypts all secret config values with decryptKey and
// re-encrypts them with the store's current DataKey. Returns the number of
// keys re-encrypted.
func (s *Store) RotateConfigSecrets(ctx context.Context, decryptKey, encryptKey *dek.Key) (int, error) {
	count := 0
	for key := range terrainSecretKeys {
		val, err := s.GetConfig(ctx, key)
		if err != nil || val == "" {
			continue
		}
		// Decrypt with old key.
		if _, b64Err := base64.StdEncoding.DecodeString(val); b64Err != nil {
			continue // legacy plaintext, will be encrypted on next write
		}
		if decryptKey == nil {
			continue
		}
		dec, err := decryptKey.Decrypt(val)
		if err != nil {
			slog.Warn("rotate config secret: decrypt failed", "key", key)
			continue
		}
		// Re-encrypt with new key.
		if encryptKey == nil {
			continue
		}
		enc, err := encryptKey.Encrypt(dec)
		if err != nil {
			return count, fmt.Errorf("rotate config %s: encrypt: %w", key, err)
		}
		if err := s.SetConfig(ctx, key, enc); err != nil {
			return count, fmt.Errorf("rotate config %s: store: %w", key, err)
		}
		count++
	}
	return count, nil
}
