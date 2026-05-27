package dek

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadOrCreate_NewKey(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "server.key")

	k, err := LoadOrCreate(path)
	if err != nil {
		t.Fatal(err)
	}

	// File should exist with correct size.
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if info.Size() != keySize {
		t.Fatalf("key file size = %d, want %d", info.Size(), keySize)
	}
	if info.Mode().Perm() != 0o600 {
		t.Fatalf("key file mode = %o, want 0600", info.Mode().Perm())
	}

	// Key should encrypt and decrypt.
	secret := "hunter2"
	enc, err := k.Encrypt(secret)
	if err != nil {
		t.Fatal(err)
	}
	if enc == secret {
		t.Fatal("encrypted value should differ from plaintext")
	}
	dec, err := k.Decrypt(enc)
	if err != nil {
		t.Fatal(err)
	}
	if dec != secret {
		t.Fatalf("decrypted = %q, want %q", dec, secret)
	}
}

func TestLoadOrCreate_ExistingKey(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "server.key")

	k1, err := LoadOrCreate(path)
	if err != nil {
		t.Fatal(err)
	}

	secret := "my-password"
	enc, err := k1.Encrypt(secret)
	if err != nil {
		t.Fatal(err)
	}

	// Load again — should produce same key that can decrypt.
	k2, err := LoadOrCreate(path)
	if err != nil {
		t.Fatal(err)
	}
	dec, err := k2.Decrypt(enc)
	if err != nil {
		t.Fatal(err)
	}
	if dec != secret {
		t.Fatalf("second key decrypted = %q, want %q", dec, secret)
	}
}

func TestLoadOrCreate_BadSize(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "server.key")

	// Write a file with wrong size.
	if err := os.WriteFile(path, []byte("short"), 0o600); err != nil {
		t.Fatal(err)
	}
	_, err := LoadOrCreate(path)
	if err == nil {
		t.Fatal("expected error for wrong key size")
	}
}

func TestDecrypt_InvalidData(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "server.key")

	k, err := LoadOrCreate(path)
	if err != nil {
		t.Fatal(err)
	}

	// Garbage base64 that isn't a valid ciphertext.
	_, err = k.Decrypt("dGhpcyBpcyBub3QgdmFsaWQ=")
	if err == nil {
		t.Fatal("expected error decrypting garbage")
	}
}

func TestEncrypt_DifferentCiphertexts(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "server.key")

	k, err := LoadOrCreate(path)
	if err != nil {
		t.Fatal(err)
	}

	// Same plaintext should produce different ciphertexts (random nonce).
	enc1, _ := k.Encrypt("same")
	enc2, _ := k.Encrypt("same")
	if enc1 == enc2 {
		t.Fatal("two encryptions of same plaintext should differ (random nonce)")
	}
}
