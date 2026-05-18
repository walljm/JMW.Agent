//go:build !windows

package updater

import (
	"fmt"
	"os"
	"syscall"
)

// platformApply atomically replaces the running binary with the new file
// and re-execs into the new image. The init manager (systemd, launchd)
// sees no restart because the PID is preserved.
func platformApply(target, newFile string) error {
	if err := os.Rename(newFile, target); err != nil {
		return fmt.Errorf("rename: %w", err)
	}
	// Re-exec with the same args and environment. After Exec succeeds,
	// this process image is gone.
	if err := syscall.Exec(target, os.Args, os.Environ()); err != nil {
		return fmt.Errorf("exec: %w", err)
	}
	return nil
}
