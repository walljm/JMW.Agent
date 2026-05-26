//go:build linux

package discover

import (
	"os/exec"
	"strings"
)

// platformSSID returns the SSID of the Wi-Fi network the given interface
// is connected to, or empty if wired or unable to determine.
func platformSSID(iface string) string {
	if iface == "" {
		return ""
	}
	// iwgetid is the standard way on Linux; available on systems with
	// wireless-tools or iw installed.
	out, err := exec.Command("iwgetid", iface, "--raw").Output()
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(out))
}
