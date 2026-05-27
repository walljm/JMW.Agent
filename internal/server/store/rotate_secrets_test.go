package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/walljm/jmwagent/internal/server/dek"
)

func TestRotateSecrets(t *testing.T) {
	st, oldKey := testStoreWithDEK(t)
	ctx := context.Background()

	// Create a source with secrets encrypted under oldKey.
	poll := 60
	src := &Source{
		Name:                "DHCP Poller",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{"url":"http://192.168.1.1","username":"admin","password":"secret123","token":"tok456"}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src, oldKey); err != nil {
		t.Fatal(err)
	}

	// Verify secrets are readable with old key.
	before, err := st.GetSourceWithSecrets(ctx, src.ID, oldKey)
	if err != nil {
		t.Fatal(err)
	}
	var beforeCfg map[string]any
	if err := json.Unmarshal([]byte(before.ConfigJSON), &beforeCfg); err != nil {
		t.Fatal(err)
	}
	if beforeCfg["password"] != "secret123" {
		t.Fatalf("before rotation: password = %v", beforeCfg["password"])
	}
	if beforeCfg["token"] != "tok456" {
		t.Fatalf("before rotation: token = %v", beforeCfg["token"])
	}

	// Generate a new key.
	dir := t.TempDir()
	newKeyPath := filepath.Join(dir, "new.key")
	newKey, err := dek.LoadOrCreate(newKeyPath)
	if err != nil {
		t.Fatal(err)
	}

	// Rotate: decrypt with old, encrypt with new.
	count, err := st.RotateSecrets(ctx, oldKey, newKey)
	if err != nil {
		t.Fatal(err)
	}
	if count != 1 {
		t.Fatalf("rotated count = %d, want 1", count)
	}

	// Old key should no longer decrypt properly (base64 decodes but AES fails).
	// GetSourceWithSecrets with old key should return non-plaintext values or leave as-is.
	afterOld, err := st.GetSourceWithSecrets(ctx, src.ID, oldKey)
	if err != nil {
		t.Fatal(err)
	}
	var afterOldCfg map[string]any
	if err := json.Unmarshal([]byte(afterOld.ConfigJSON), &afterOldCfg); err != nil {
		t.Fatal(err)
	}
	// With wrong key, decrypt should fail — the decryptSecrets function logs a
	// warning and leaves the encrypted value as-is (not plaintext "secret123").
	if afterOldCfg["password"] == "secret123" {
		t.Fatal("old key should no longer decrypt to plaintext after rotation")
	}

	// New key should correctly decrypt.
	afterNew, err := st.GetSourceWithSecrets(ctx, src.ID, newKey)
	if err != nil {
		t.Fatal(err)
	}
	var afterNewCfg map[string]any
	if err := json.Unmarshal([]byte(afterNew.ConfigJSON), &afterNewCfg); err != nil {
		t.Fatal(err)
	}
	if afterNewCfg["password"] != "secret123" {
		t.Fatalf("after rotation with new key: password = %v", afterNewCfg["password"])
	}
	if afterNewCfg["token"] != "tok456" {
		t.Fatalf("after rotation with new key: token = %v", afterNewCfg["token"])
	}
	// Non-secret field should be unaffected.
	if afterNewCfg["url"] != "http://192.168.1.1" {
		t.Fatalf("url = %v", afterNewCfg["url"])
	}
}

func TestRotateSecrets_SkipsKindsWithoutSecrets(t *testing.T) {
	st, oldKey := testStoreWithDEK(t)
	ctx := context.Background()

	// "agent" kind has no secret fields.
	poll := 60
	src := &Source{
		Name:                "Agent Source",
		Kind:                "agent",
		Enabled:             true,
		ConfigJSON:          `{}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src, oldKey); err != nil {
		t.Fatal(err)
	}

	dir := t.TempDir()
	newKeyPath := filepath.Join(dir, "new.key")
	newKey, err := dek.LoadOrCreate(newKeyPath)
	if err != nil {
		t.Fatal(err)
	}

	count, err := st.RotateSecrets(ctx, oldKey, newKey)
	if err != nil {
		t.Fatal(err)
	}
	// Agent kind has no secrets → should not be counted.
	if count != 0 {
		t.Fatalf("count = %d, want 0 (agent kind has no secret fields)", count)
	}
}

func TestRotateSecrets_MultipleSources(t *testing.T) {
	st, oldKey := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	sources := []struct {
		name string
		kind string
		cfg  string
	}{
		{"DHCP 1", "terrain-dhcp", `{"url":"http://a","password":"p1"}`},
		{"DHCP 2", "terrain-dhcp", `{"url":"http://b","password":"p2","token":"t2"}`},
		{"SNMP", "snmp-poller", `{"target":"192.168.1.0/24","community":"public"}`},
		{"Nmap", "nmap-scanner", `{"target":"10.0.0.0/8"}`},
	}
	for _, s := range sources {
		src := &Source{
			Name: s.name, Kind: s.kind, Enabled: true,
			ConfigJSON: s.cfg, PollIntervalSeconds: &poll,
		}
		if err := st.CreateSource(ctx, src, oldKey); err != nil {
			t.Fatal(err)
		}
	}

	dir := t.TempDir()
	newKey, err := dek.LoadOrCreate(filepath.Join(dir, "new.key"))
	if err != nil {
		t.Fatal(err)
	}

	count, err := st.RotateSecrets(ctx, oldKey, newKey)
	if err != nil {
		t.Fatal(err)
	}
	// DHCP 1, DHCP 2, SNMP have secret fields → 3 rotated.
	// nmap-scanner has empty SecretFields → skipped.
	if count != 3 {
		t.Fatalf("count = %d, want 3", count)
	}
}
