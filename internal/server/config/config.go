// Package config loads the server's configuration from disk + env.
package config

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"path/filepath"

	"github.com/BurntSushi/toml"
)

// Config is the server configuration.
type Config struct {
	Listen               string    `toml:"listen"`
	DataDir              string    `toml:"data_dir"`
	ReleasesDir          string    `toml:"releases_dir"`
	TLSCertFile          string    `toml:"tls_cert_file"`
	TLSKeyFile           string    `toml:"tls_key_file"`
	LogLevel             string    `toml:"log_level"`
	AgentPSK             string    `toml:"agent_psk"`
	SessionLifetimeHours int       `toml:"session_lifetime_hours"`
	Retention            Retention `toml:"retention"`

	// Deprecated: Legacy terrain config. If present at boot and no terrain
	// Source row exists in the database, the server performs a one-time import
	// into the sources table and logs a migration notice. This field will be
	// removed in a future release.
	Terrain TerrainConfig `toml:"terrain"`

	// Addr is the legacy name for Listen. If both are set, Listen wins.
	Addr string `toml:"addr"`
}

// TerrainConfig holds optional Key Cyber Terrain polling configuration.
// Deprecated: use the Sources table via the admin UI instead. This struct
// exists only for one-time migration into the database.
type TerrainConfig struct {
	URL      string `toml:"url"`
	Token    string `toml:"token"`
	Username string `toml:"username"`
	Password string `toml:"password"`
}

// Retention holds default retention durations. These can be overridden per-tier
// from the admin UI without a restart.
type Retention struct {
	RawMetrics        string `toml:"raw_metrics"`
	Rollup5Min        string `toml:"rollup_5min"`
	RollupHourly      string `toml:"rollup_hourly"`
	RollupDaily       string `toml:"rollup_daily"`
	RemovedContainers string `toml:"removed_containers"`
	StaleObservations string `toml:"stale_observations"`
}

// Defaults returns sensible defaults.
func Defaults() *Config {
	return &Config{
		Listen:               ":8443",
		DataDir:              "./data",
		ReleasesDir:          "./releases",
		LogLevel:             "info",
		SessionLifetimeHours: 24 * 7,
		Retention: Retention{
			RawMetrics:        "48h",
			Rollup5Min:        "7d",
			RollupHourly:      "90d",
			RollupDaily:       "365d",
			RemovedContainers: "7d",
		},
	}
}

// Load reads config from path (TOML), filling defaults for any missing fields.
// If the file does not exist, returns defaults; the caller is expected to write
// it back out via Save() once boot decisions are made.
func Load(path string) (*Config, error) {
	c := Defaults()
	b, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return c, nil
		}
		return nil, err
	}
	if err := toml.Unmarshal(b, c); err != nil {
		return nil, fmt.Errorf("parse config: %w", err)
	}
	// Support legacy "addr" field renamed to "listen".
	if c.Listen == "" && c.Addr != "" {
		c.Listen = c.Addr
	}
	if c.Listen == "" {
		c.Listen = ":8443"
	}
	if c.DataDir == "" {
		c.DataDir = "./data"
	}
	if c.SessionLifetimeHours <= 0 {
		c.SessionLifetimeHours = 24 * 7
	}
	return c, nil
}

// Save writes config to disk in TOML form.
func Save(path string, c *Config) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o600)
	if err != nil {
		return err
	}
	defer f.Close()
	return toml.NewEncoder(f).Encode(c)
}

// EnsureAgentPSK fills c.AgentPSK with a random hex token if empty.
// Returns true if the value was generated.
func EnsureAgentPSK(c *Config) (bool, error) {
	if c.AgentPSK != "" {
		return false, nil
	}
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return false, err
	}
	c.AgentPSK = hex.EncodeToString(b)
	return true, nil
}

// DBPath returns the SQLite database path under DataDir.
func (c *Config) DBPath() string {
	return filepath.Join(c.DataDir, "jmw.db")
}
