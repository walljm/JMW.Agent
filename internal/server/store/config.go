package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strconv"
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
func (s *Store) GetAgentIntervals(ctx context.Context) (heartbeat, discovery, inventory int, err error) {
	heartbeat, discovery, inventory = 30, 300, 86400

	cfg, err := s.GetAllConfig(ctx)
	if err != nil {
		return heartbeat, discovery, inventory, fmt.Errorf("get agent intervals: %w", err)
	}

	if v, ok := cfg["agent.heartbeat_interval_secs"]; ok {
		if n, e := strconv.Atoi(v); e == nil && n > 0 {
			heartbeat = n
		}
	}
	if v, ok := cfg["agent.discovery_interval_secs"]; ok {
		if n, e := strconv.Atoi(v); e == nil && n > 0 {
			discovery = n
		}
	}
	if v, ok := cfg["agent.inventory_interval_secs"]; ok {
		if n, e := strconv.Atoi(v); e == nil && n > 0 {
			inventory = n
		}
	}
	return heartbeat, discovery, inventory, nil
}

// GetSessionLifetimeHours returns the configured session lifetime in hours,
// defaulting to 168 (7 days).
func (s *Store) GetSessionLifetimeHours(ctx context.Context) (int, error) {
	v, err := s.GetConfig(ctx, "auth.session_lifetime_hours")
	if err != nil {
		return 168, err
	}
	if v == "" {
		return 168, nil
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return 168, nil
	}
	return n, nil
}

// GetTerrainPollInterval returns the terrain poll interval in seconds,
// defaulting to 60.
func (s *Store) GetTerrainPollInterval(ctx context.Context) (int, error) {
	v, err := s.GetConfig(ctx, "terrain.poll_interval_secs")
	if err != nil {
		return 60, err
	}
	if v == "" {
		return 60, nil
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return 60, nil
	}
	return n, nil
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
