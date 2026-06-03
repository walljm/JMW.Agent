package releases

import (
	"os"
	"path/filepath"
	"testing"
)

func TestScanLoadsSignatureSidecar(t *testing.T) {
	releaseDir := filepath.Join(t.TempDir(), "v1.2.3")
	if err := os.MkdirAll(releaseDir, 0o755); err != nil {
		t.Fatal(err)
	}
	binaryPath := filepath.Join(releaseDir, "jmw-agent-linux-amd64")
	if err := os.WriteFile(binaryPath, []byte("agent-binary"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(binaryPath+".sig", []byte("signature\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	manager := New(filepath.Dir(releaseDir))
	if err := manager.Scan(); err != nil {
		t.Fatal(err)
	}
	entry, ok := manager.Latest("linux", "amd64")
	if !ok {
		t.Fatal("expected linux/amd64 release")
	}
	if entry.Signature != "signature" {
		t.Fatalf("signature mismatch: got %q", entry.Signature)
	}
}
