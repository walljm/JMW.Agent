package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strconv"
	"time"

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

// TerrainConfig holds the terrain poller connection settings from the DB.
type TerrainConfig struct {
	URL      string
	Token    string
	Username string
	Password string
}

// GetTerrainConfig reads terrain connection settings from the config table.
func (s *Store) GetTerrainConfig(ctx context.Context) (TerrainConfig, error) {
	cfg, err := s.GetAllConfig(ctx)
	if err != nil {
		return TerrainConfig{}, err
	}
	return TerrainConfig{
		URL:      cfg["terrain.url"],
		Token:    cfg["terrain.token"],
		Username: cfg["terrain.username"],
		Password: cfg["terrain.password"],
	}, nil
}
