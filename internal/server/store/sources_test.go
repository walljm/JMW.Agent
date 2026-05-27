package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/walljm/jmwagent/internal/server/dek"
)

func testStoreWithDEK(t *testing.T) (*Store, *dek.Key) {
	t.Helper()
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "test.db")
	st, err := Open(context.Background(), dbPath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { st.Close() })

	keyPath := filepath.Join(dir, "server.key")
	key, err := dek.LoadOrCreate(keyPath)
	if err != nil {
		t.Fatal(err)
	}
	return st, key
}

func TestCreateAndGetSource(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	src := &Source{
		Name:                "AdGuard DHCP",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{"url":"http://192.168.1.1","username":"admin","password":"s3cr3t"}`,
		PollIntervalSeconds: &poll,
	}

	if err := st.CreateSource(ctx, src, key); err != nil {
		t.Fatal(err)
	}
	if src.ID == "" {
		t.Fatal("expected ID to be assigned")
	}

	// GetSource should mask secrets.
	got, err := st.GetSource(ctx, src.ID)
	if err != nil {
		t.Fatal(err)
	}
	if got.Name != "AdGuard DHCP" {
		t.Fatalf("name = %q", got.Name)
	}
	if got.Kind != "terrain-dhcp" {
		t.Fatalf("kind = %q", got.Kind)
	}
	if !got.Enabled {
		t.Fatal("expected enabled")
	}

	// Verify password is masked.
	var cfg map[string]any
	if err := json.Unmarshal([]byte(got.ConfigJSON), &cfg); err != nil {
		t.Fatal(err)
	}
	if cfg["password"] != "<set>" {
		t.Fatalf("password should be masked, got %v", cfg["password"])
	}
	if cfg["url"] != "http://192.168.1.1" {
		t.Fatalf("url should be preserved, got %v", cfg["url"])
	}

	// GetSourceWithSecrets should decrypt.
	full, err := st.GetSourceWithSecrets(ctx, src.ID, key)
	if err != nil {
		t.Fatal(err)
	}
	var fullCfg map[string]any
	if err := json.Unmarshal([]byte(full.ConfigJSON), &fullCfg); err != nil {
		t.Fatal(err)
	}
	if fullCfg["password"] != "s3cr3t" {
		t.Fatalf("decrypted password = %v", fullCfg["password"])
	}
}

func TestUpdateSourceSecrets(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	src := &Source{
		Name:                "Test Source",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{"url":"http://x","username":"admin","password":"old-pass","token":"old-tok"}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src, key); err != nil {
		t.Fatal(err)
	}

	// Update only the password.
	err := st.UpdateSourceSecrets(ctx, src.ID, `{"password":"new-pass"}`, key)
	if err != nil {
		t.Fatal(err)
	}

	// Verify.
	got, err := st.GetSourceWithSecrets(ctx, src.ID, key)
	if err != nil {
		t.Fatal(err)
	}
	var cfg map[string]any
	if err := json.Unmarshal([]byte(got.ConfigJSON), &cfg); err != nil {
		t.Fatal(err)
	}
	if cfg["password"] != "new-pass" {
		t.Fatalf("password = %v, want new-pass", cfg["password"])
	}
	// Token should remain unchanged.
	if cfg["token"] != "old-tok" {
		t.Fatalf("token = %v, want old-tok", cfg["token"])
	}
	// Non-secret field preserved.
	if cfg["url"] != "http://x" {
		t.Fatalf("url = %v", cfg["url"])
	}
}

func TestListSources(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	for _, name := range []string{"Source A", "Source B"} {
		src := &Source{
			Name:                name,
			Kind:                "terrain-dhcp",
			Enabled:             true,
			ConfigJSON:          `{"url":"http://x","password":"pass"}`,
			PollIntervalSeconds: &poll,
		}
		if err := st.CreateSource(ctx, src, key); err != nil {
			t.Fatal(err)
		}
	}
	// Add a different kind.
	src3 := &Source{
		Name:                "Nmap",
		Kind:                "nmap-scanner",
		Enabled:             true,
		ConfigJSON:          `{"target":"192.168.1.0/24"}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src3, key); err != nil {
		t.Fatal(err)
	}

	// List all.
	all, err := st.ListSources(ctx, "")
	if err != nil {
		t.Fatal(err)
	}
	if len(all) != 3 {
		t.Fatalf("len = %d, want 3", len(all))
	}

	// List by kind.
	dhcp, err := st.ListSources(ctx, "terrain-dhcp")
	if err != nil {
		t.Fatal(err)
	}
	if len(dhcp) != 2 {
		t.Fatalf("dhcp len = %d, want 2", len(dhcp))
	}
}

func TestListEnabledSources(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	src1 := &Source{
		Name:                "Enabled",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{"url":"http://x","password":"pass"}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src1, key); err != nil {
		t.Fatal(err)
	}
	src2 := &Source{
		Name:       "Disabled",
		Kind:       "terrain-dhcp",
		Enabled:    false,
		ConfigJSON: `{"url":"http://y","password":"pass"}`,
	}
	if err := st.CreateSource(ctx, src2, key); err != nil {
		t.Fatal(err)
	}

	enabled, err := st.ListEnabledSources(ctx, key)
	if err != nil {
		t.Fatal(err)
	}
	if len(enabled) != 1 {
		t.Fatalf("enabled len = %d, want 1", len(enabled))
	}
	// Password should be decrypted.
	var cfg map[string]any
	if err := json.Unmarshal([]byte(enabled[0].ConfigJSON), &cfg); err != nil {
		t.Fatal(err)
	}
	if cfg["password"] != "pass" {
		t.Fatalf("password = %v, want pass", cfg["password"])
	}
}

func TestRecordSourcePollResult(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	src := &Source{
		Name:                "Poll Test",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{"url":"http://x"}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src, key); err != nil {
		t.Fatal(err)
	}

	// Record success.
	if err := st.RecordSourcePollResult(ctx, src.ID, nil); err != nil {
		t.Fatal(err)
	}
	got, err := st.GetSource(ctx, src.ID)
	if err != nil {
		t.Fatal(err)
	}
	if got.LastSuccessAt == nil {
		t.Fatal("expected last_success_at to be set")
	}
	if got.ConsecutiveErrorCount != 0 {
		t.Fatalf("error count = %d", got.ConsecutiveErrorCount)
	}

	// Record error.
	if err := st.RecordSourcePollResult(ctx, src.ID, sql.ErrConnDone); err != nil {
		t.Fatal(err)
	}
	got, err = st.GetSource(ctx, src.ID)
	if err != nil {
		t.Fatal(err)
	}
	if got.ConsecutiveErrorCount != 1 {
		t.Fatalf("error count = %d, want 1", got.ConsecutiveErrorCount)
	}
	if got.LastErrorMessage == "" {
		t.Fatal("expected error message")
	}
}

func TestHasSourceOfKind(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	has, err := st.HasSourceOfKind(ctx, "terrain-dhcp")
	if err != nil {
		t.Fatal(err)
	}
	if has {
		t.Fatal("expected no sources yet")
	}

	poll := 60
	src := &Source{
		Name:                "X",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src, key); err != nil {
		t.Fatal(err)
	}

	has, err = st.HasSourceOfKind(ctx, "terrain-dhcp")
	if err != nil {
		t.Fatal(err)
	}
	if !has {
		t.Fatal("expected source to exist")
	}
}

func TestDeleteSource(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	src := &Source{
		Name:                "To Delete",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{"url":"http://x","password":"pass"}`,
		PollIntervalSeconds: &poll,
	}
	if err := st.CreateSource(ctx, src, key); err != nil {
		t.Fatal(err)
	}

	if err := st.DeleteSource(ctx, src.ID); err != nil {
		t.Fatal(err)
	}

	_, err := st.GetSource(ctx, src.ID)
	if err != ErrSourceNotFound {
		t.Fatalf("expected ErrSourceNotFound, got %v", err)
	}
}

func TestDeleteSourceNotFound(t *testing.T) {
	st, _ := testStoreWithDEK(t)
	ctx := context.Background()

	err := st.DeleteSource(ctx, "nonexistent-id")
	if err != ErrSourceNotFound {
		t.Fatalf("expected ErrSourceNotFound, got %v", err)
	}
}

func TestGetSourceNotFound(t *testing.T) {
	st, _ := testStoreWithDEK(t)
	ctx := context.Background()

	_, err := st.GetSource(ctx, "nonexistent-id")
	if err != ErrSourceNotFound {
		t.Fatalf("expected ErrSourceNotFound, got %v", err)
	}
}

func TestCreateSourceRejectsSentinel(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	src := &Source{
		Name:                "Bad",
		Kind:                "terrain-dhcp",
		Enabled:             true,
		ConfigJSON:          `{"url":"http://x","password":"<set>"}`,
		PollIntervalSeconds: &poll,
	}
	err := st.CreateSource(ctx, src, key)
	if err == nil {
		t.Fatal("expected error for sentinel value")
	}
}

func TestCreateSourceRejectsInvalidKind(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	poll := 60
	src := &Source{
		Name:                "Bad Kind",
		Kind:                "unknown-kind",
		Enabled:             true,
		ConfigJSON:          `{}`,
		PollIntervalSeconds: &poll,
	}
	err := st.CreateSource(ctx, src, key)
	if err == nil {
		t.Fatal("expected error for unknown kind")
	}
}

func TestUpdateSourceSecretsNotFound(t *testing.T) {
	st, key := testStoreWithDEK(t)
	ctx := context.Background()

	err := st.UpdateSourceSecrets(ctx, "nonexistent", `{"password":"x"}`, key)
	if err != ErrSourceNotFound {
		t.Fatalf("expected ErrSourceNotFound, got %v", err)
	}
}
