// Package updatesig signs and verifies agent self-update artifacts.
package updatesig

import (
	"crypto/ed25519"
	"encoding/base64"
	"errors"
	"fmt"
	"path/filepath"
	"strconv"
	"strings"
)

const (
	Algorithm = "ed25519"
	payloadID = "jmw-agent-update-signature-v1"
)

// Metadata is the signed identity of one update artifact.
type Metadata struct {
	Version  string
	Filename string
	SHA256   string
	Size     int64
}

// Payload returns the canonical bytes signed for an update artifact.
func Payload(meta Metadata) []byte {
	filename := filepath.Base(meta.Filename)
	sha256Hex := strings.ToLower(strings.TrimSpace(meta.SHA256))
	body := payloadID + "\n" +
		"version:" + strings.TrimSpace(meta.Version) + "\n" +
		"filename:" + filename + "\n" +
		"size:" + strconv.FormatInt(meta.Size, 10) + "\n" +
		"sha256:" + sha256Hex + "\n"
	return []byte(body)
}

// Sign returns a base64 Ed25519 signature for meta. privateKeyBase64 may encode
// either a 32-byte seed or a 64-byte Ed25519 private key.
func Sign(meta Metadata, privateKeyBase64 string) (string, error) {
	privateKey, err := DecodePrivateKey(privateKeyBase64)
	if err != nil {
		return "", err
	}
	sig := ed25519.Sign(privateKey, Payload(meta))
	return base64.StdEncoding.EncodeToString(sig), nil
}

// Verify checks a base64 Ed25519 signature for meta.
func Verify(meta Metadata, signatureBase64, publicKeyBase64 string) error {
	publicKey, err := DecodePublicKey(publicKeyBase64)
	if err != nil {
		return err
	}
	sig, err := base64.StdEncoding.DecodeString(strings.TrimSpace(signatureBase64))
	if err != nil {
		return fmt.Errorf("decode update signature: %w", err)
	}
	if len(sig) != ed25519.SignatureSize {
		return fmt.Errorf("decode update signature: got %d bytes, want %d", len(sig), ed25519.SignatureSize)
	}
	if !ed25519.Verify(publicKey, Payload(meta), sig) {
		return errors.New("update signature verification failed")
	}
	return nil
}

// DecodePublicKey decodes a base64 Ed25519 public key.
func DecodePublicKey(publicKeyBase64 string) (ed25519.PublicKey, error) {
	b, err := base64.StdEncoding.DecodeString(strings.TrimSpace(publicKeyBase64))
	if err != nil {
		return nil, fmt.Errorf("decode update public key: %w", err)
	}
	if len(b) != ed25519.PublicKeySize {
		return nil, fmt.Errorf("decode update public key: got %d bytes, want %d", len(b), ed25519.PublicKeySize)
	}
	return ed25519.PublicKey(b), nil
}

// DecodePrivateKey decodes a base64 Ed25519 seed or private key.
func DecodePrivateKey(privateKeyBase64 string) (ed25519.PrivateKey, error) {
	b, err := base64.StdEncoding.DecodeString(strings.TrimSpace(privateKeyBase64))
	if err != nil {
		return nil, fmt.Errorf("decode update private key: %w", err)
	}
	switch len(b) {
	case ed25519.SeedSize:
		return ed25519.NewKeyFromSeed(b), nil
	case ed25519.PrivateKeySize:
		return ed25519.PrivateKey(b), nil
	default:
		return nil, fmt.Errorf("decode update private key: got %d bytes, want %d seed or %d private key", len(b), ed25519.SeedSize, ed25519.PrivateKeySize)
	}
}

// PublicKeyBase64 returns the base64 public key for a base64 private key or seed.
func PublicKeyBase64(privateKeyBase64 string) (string, error) {
	privateKey, err := DecodePrivateKey(privateKeyBase64)
	if err != nil {
		return "", err
	}
	publicKey, ok := privateKey.Public().(ed25519.PublicKey)
	if !ok {
		return "", errors.New("derive update public key: unexpected key type")
	}
	return base64.StdEncoding.EncodeToString(publicKey), nil
}
