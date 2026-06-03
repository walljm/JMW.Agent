// Package identity manages the agent's persistent ID.
package identity

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"strings"
)

// EnsureID returns the agent's persistent ID, creating one if missing.
func EnsureID(path string) (string, error) {
	b, err := os.ReadFile(path)
	if err == nil {
		id := strings.TrimSpace(string(b))
		if id != "" {
			return id, nil
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return "", err
	}
	buf := make([]byte, 16)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	id := hex.EncodeToString(buf)
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return "", err
	}
	if err := WriteID(path, id); err != nil {
		return "", err
	}
	return id, nil
}

// WriteID persists id as the agent's current identity.
func WriteID(path, id string) error {
	id = strings.TrimSpace(id)
	if id == "" {
		return errors.New("agent id is empty")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	return os.WriteFile(path, []byte(id+"\n"), 0o600)
}
