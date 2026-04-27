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
	Addr         string `toml:"addr"`
	DataDir      string `toml:"data_dir"`
	TLSCertFile  string `toml:"tls_cert_file"`
	TLSKeyFile   string `toml:"tls_key_file"`
	AgentPSK     string `toml:"agent_psk"`
	SessionLifetimeHours int `toml:"session_lifetime_hours"`
}

// Defaults returns sensible defaults.
func Defaults() *Config {
	return &Config{
		Addr:                 ":8443",
		DataDir:              "./data",
		SessionLifetimeHours: 24 * 7,
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
	if c.Addr == "" {
		c.Addr = ":8443"
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
