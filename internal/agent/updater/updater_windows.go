//go:build windows

package updater

import (
	"fmt"
	"log/slog"
	"os"
)

// platformApply stages the new binary next to the running .exe with a
// `.new` suffix and exits with code 0. The Scheduled Task wrapper
// (see deploy/windows/install-agent.ps1) detects the staged file on the
// next launch, moves it into place, and starts the new image.
//
// We cannot os.Rename over the running .exe on Windows because the file
// is held open by the OS, so we hand off to the wrapper instead.
func platformApply(target, newFile string) error {
	staged := target + ".new"
	// Replace any leftover .new from a prior aborted attempt.
	_ = os.Remove(staged)
	if err := os.Rename(newFile, staged); err != nil {
		return fmt.Errorf("stage: %w", err)
	}
	slog.Info("update staged, exiting for wrapper swap", "staged", staged, "target", target)
	os.Exit(0)
	return nil
}
