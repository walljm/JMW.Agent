//go:build darwin

package discover

import (
	"os/exec"
	"regexp"
	"strings"
)

var darwinGatewayRe = regexp.MustCompile(`gateway:\s+(\S+)`)

// platformDefaultGateway runs `route -n get default` and parses the
// "gateway:" line. Returns "" on failure.
func platformDefaultGateway() string {
	out, err := exec.Command("route", "-n", "get", "default").Output()
	if err != nil {
		return ""
	}
	m := darwinGatewayRe.FindStringSubmatch(string(out))
	if len(m) != 2 {
		return ""
	}
	gw := strings.TrimSpace(m[1])
	if gw == "" {
		return ""
	}
	return gw
}
