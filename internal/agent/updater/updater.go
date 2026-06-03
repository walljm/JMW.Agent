// Package updater applies binary self-updates announced by the server's
// heartbeat response. The flow is the same on every platform:
//
//  1. Download the new binary to a temp file in the same directory as the
//     running executable. Same-fs guarantees os.Rename is atomic.
//  2. Verify SHA-256 against the value the server advertised in the
//     heartbeat. Reject and remove on mismatch.
//  3. Verify the Ed25519 signature against the agent's configured public key.
//  4. Chmod 0755 (Unix) and hand off to platform-specific Apply().
//
// On Linux and macOS the platform code performs an os.Rename + syscall.Exec
// so the running process replaces itself in place with the same PID; init
// systems (systemd, launchd) see no restart event.
//
// On Windows os.Rename of a running .exe is not permitted. The platform
// code instead writes the new binary next to the current one with a .new
// suffix, then exits with code 0. The Scheduled Task wrapper installed by
// deploy/windows/install-agent.ps1 promotes the .new file on next launch.
package updater

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path"
	"path/filepath"
	"sync"

	"github.com/walljm/jmwagent/internal/agent/transport"
	"github.com/walljm/jmwagent/internal/shared/proto"
	"github.com/walljm/jmwagent/internal/shared/updatesig"
)

// inFlight guards against concurrent update attempts. A second heartbeat
// arriving while a download is in progress should be a no-op.
var inFlight sync.Mutex

// Apply downloads, verifies, and installs the update described by info,
// then re-execs (Unix) or exits cleanly (Windows). Returns nil only when
// nothing needed to happen; otherwise it never returns on success because
// the process is replaced.
func Apply(ctx context.Context, cli *transport.Client, info *proto.UpdateInfo, updatePublicKey string) error {
	if info == nil || info.URL == "" || info.SHA256 == "" {
		return errors.New("updater: empty update info")
	}
	if updatePublicKey == "" {
		return errors.New("updater: update_public_key is required for signed updates")
	}
	if info.Signature == "" || info.SignatureAlgorithm != updatesig.Algorithm {
		return fmt.Errorf("updater: unsupported update signature algorithm %q", info.SignatureAlgorithm)
	}
	if !inFlight.TryLock() {
		return errors.New("updater: another update is already in progress")
	}
	defer inFlight.Unlock()

	exePath, err := os.Executable()
	if err != nil {
		return fmt.Errorf("updater: locate self: %w", err)
	}
	// Resolve symlinks so we replace the real file, not a symlink target.
	if resolved, err := filepath.EvalSymlinks(exePath); err == nil {
		exePath = resolved
	}
	dir := filepath.Dir(exePath)

	tmp, err := os.CreateTemp(dir, ".jmw-agent-update-*")
	if err != nil {
		return fmt.Errorf("updater: temp file: %w", err)
	}
	tmpPath := tmp.Name()
	cleanup := func() { _ = os.Remove(tmpPath) }

	body, _, err := cli.Download(ctx, info.URL)
	if err != nil {
		_ = tmp.Close()
		cleanup()
		return fmt.Errorf("updater: download: %w", err)
	}
	defer body.Close()

	h := sha256.New()
	written, err := io.Copy(tmp, io.TeeReader(body, h))
	if err != nil {
		_ = tmp.Close()
		cleanup()
		return fmt.Errorf("updater: write: %w", err)
	}
	if err := tmp.Close(); err != nil {
		cleanup()
		return fmt.Errorf("updater: close: %w", err)
	}

	got := hex.EncodeToString(h.Sum(nil))
	if got != info.SHA256 {
		cleanup()
		return fmt.Errorf("updater: sha256 mismatch: got %s want %s", got, info.SHA256)
	}
	if info.Size > 0 && written != info.Size {
		cleanup()
		return fmt.Errorf("updater: size mismatch: got %d want %d", written, info.Size)
	}
	meta := updatesig.Metadata{
		Version:  info.Version,
		Filename: path.Base(info.URL),
		SHA256:   got,
		Size:     written,
	}
	if err := updatesig.Verify(meta, info.Signature, updatePublicKey); err != nil {
		cleanup()
		return fmt.Errorf("updater: signature: %w", err)
	}

	if err := os.Chmod(tmpPath, 0o755); err != nil {
		cleanup()
		return fmt.Errorf("updater: chmod: %w", err)
	}

	slog.Info("update verified, applying", "version", info.Version, "path", exePath)
	if err := platformApply(exePath, tmpPath); err != nil {
		cleanup()
		return fmt.Errorf("updater: apply: %w", err)
	}
	// Unreachable on success (process re-execed or exited).
	return nil
}
