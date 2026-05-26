//go:build darwin

package discover

import (
	"os/exec"
	"strings"
)

// platformSSID returns the SSID of the Wi-Fi network the given interface
// is connected to, or empty if wired or unable to determine.
func platformSSID(iface string) string {
	// Try networksetup first (works on all macOS versions).
	if iface != "" {
		out, err := exec.Command("networksetup", "-getairportnetwork", iface).Output()
		if err == nil {
			// Output: "Current Wi-Fi Network: MySSID\n"
			s := strings.TrimSpace(string(out))
			if strings.HasPrefix(s, "Current Wi-Fi Network:") {
				return strings.TrimSpace(strings.TrimPrefix(s, "Current Wi-Fi Network:"))
			}
		}
	}

	// Fallback: legacy airport binary (removed in macOS 14.4+).
	out, err := exec.Command(
		"/System/Library/PrivateFrameworks/Apple80211.framework/Versions/Current/Resources/airport",
		"-I",
	).Output()
	if err != nil {
		return ""
	}
	for _, line := range strings.Split(string(out), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "SSID:") {
			return strings.TrimSpace(strings.TrimPrefix(line, "SSID:"))
		}
	}
	return ""
}
