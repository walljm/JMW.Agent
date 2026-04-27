// Package agentcfg loads agent-side config.
package agentcfg

import (
	"errors"
	"os"
	"path/filepath"

	"github.com/BurntSushi/toml"
)

// Config is the agent configuration.
type Config struct {
	ServerURL              string `toml:"server_url"`
	PSK                    string `toml:"psk"`
	PinnedSHA              string `toml:"pinned_sha"`
	IDFile                 string `toml:"id_file"`
	IntervalSecs           int    `toml:"interval_secs"`
	InventoryIntervalSecs  int    `toml:"inventory_interval_secs"`
	IncludePackages        bool   `toml:"include_packages"`
}

// Defaults returns a config with reasonable defaults.
func Defaults() *Config {
	return &Config{
		IDFile:                "./agent.id",
		IntervalSecs:          30,
		InventoryIntervalSecs: 86400, // 24h
		IncludePackages:       false,
	}
}

// Load reads from path; returns defaults if file missing.
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
		return nil, err
	}
	if c.IntervalSecs <= 0 {
		c.IntervalSecs = 30
	}
	if c.InventoryIntervalSecs <= 0 {
		c.InventoryIntervalSecs = 86400
	}
	if c.IDFile == "" {
		c.IDFile = "./agent.id"
	}
	return c, nil
}

// Save writes the config back to disk.
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
