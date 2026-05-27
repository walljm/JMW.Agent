// Package dek manages the data encryption key used to protect secrets at rest.
package dek

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"os"
)

const keySize = 32 // AES-256

// Key wraps the raw encryption key and provides encrypt/decrypt operations.
type Key struct {
	raw  []byte
	aead cipher.AEAD
}

// LoadOrCreate loads the DEK from path, or generates a new one if the file
// does not exist. The file is created with mode 0600.
func LoadOrCreate(path string) (*Key, error) {
	raw, err := os.ReadFile(path)
	if err == nil {
		if len(raw) != keySize {
			return nil, fmt.Errorf("dek: key file %s has %d bytes, want %d", path, len(raw), keySize)
		}
		return newKey(raw)
	}
	if !errors.Is(err, os.ErrNotExist) {
		return nil, fmt.Errorf("dek: read %s: %w", path, err)
	}

	// Generate new key.
	raw = make([]byte, keySize)
	if _, err := io.ReadFull(rand.Reader, raw); err != nil {
		return nil, fmt.Errorf("dek: generate: %w", err)
	}
	if err := os.WriteFile(path, raw, 0o600); err != nil {
		return nil, fmt.Errorf("dek: write %s: %w", path, err)
	}
	return newKey(raw)
}

func newKey(raw []byte) (*Key, error) {
	block, err := aes.NewCipher(raw)
	if err != nil {
		return nil, fmt.Errorf("dek: new cipher: %w", err)
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("dek: new gcm: %w", err)
	}
	return &Key{raw: raw, aead: aead}, nil
}

// Encrypt encrypts plaintext and returns a base64-encoded ciphertext string
// suitable for storage in a JSON field.
func (k *Key) Encrypt(plaintext string) (string, error) {
	nonce := make([]byte, k.aead.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return "", fmt.Errorf("dek: nonce: %w", err)
	}
	sealed := k.aead.Seal(nonce, nonce, []byte(plaintext), nil)
	return base64.StdEncoding.EncodeToString(sealed), nil
}

// Decrypt decodes a base64-encoded ciphertext string and returns the plaintext.
func (k *Key) Decrypt(encoded string) (string, error) {
	data, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		return "", fmt.Errorf("dek: base64 decode: %w", err)
	}
	nonceSize := k.aead.NonceSize()
	if len(data) < nonceSize {
		return "", errors.New("dek: ciphertext too short")
	}
	nonce, ciphertext := data[:nonceSize], data[nonceSize:]
	plaintext, err := k.aead.Open(nil, nonce, ciphertext, nil)
	if err != nil {
		return "", fmt.Errorf("dek: decrypt: %w", err)
	}
	return string(plaintext), nil
}
