package updatesig

import (
	"crypto/ed25519"
	"encoding/base64"
	"testing"
)

func TestSignVerify(t *testing.T) {
	publicKey, privateKey, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	meta := Metadata{
		Version:  "v2.3.4",
		Filename: "jmw-agent-linux-amd64",
		SHA256:   "ABCDEF",
		Size:     12345,
	}

	sig, err := Sign(meta, base64.StdEncoding.EncodeToString(privateKey))
	if err != nil {
		t.Fatal(err)
	}
	if err := Verify(meta, sig, base64.StdEncoding.EncodeToString(publicKey)); err != nil {
		t.Fatalf("verify: %v", err)
	}
}

func TestVerifyRejectsTamperedMetadata(t *testing.T) {
	publicKey, privateKey, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	meta := Metadata{
		Version:  "v2.3.4",
		Filename: "jmw-agent-linux-amd64",
		SHA256:   "abcdef",
		Size:     12345,
	}
	sig, err := Sign(meta, base64.StdEncoding.EncodeToString(privateKey))
	if err != nil {
		t.Fatal(err)
	}

	meta.Size++
	if err := Verify(meta, sig, base64.StdEncoding.EncodeToString(publicKey)); err == nil {
		t.Fatal("expected tampered metadata to fail verification")
	}
}

func TestPublicKeyBase64FromSeed(t *testing.T) {
	publicKey, privateKey, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	seed := privateKey.Seed()
	got, err := PublicKeyBase64(base64.StdEncoding.EncodeToString(seed))
	if err != nil {
		t.Fatal(err)
	}
	want := base64.StdEncoding.EncodeToString(publicKey)
	if got != want {
		t.Fatalf("public key mismatch: got %q want %q", got, want)
	}
}
