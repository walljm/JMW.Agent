//go:build linux

package discover

import (
	"bufio"
	"os"
	"strings"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
)

// scanARP reads /proc/net/arp.
func scanARP() []Sighting {
	f, err := os.Open(hostfs.Path("/proc/net/arp"))
	if err != nil {
		return nil
	}
	defer f.Close()
	var out []Sighting
	sc := bufio.NewScanner(f)
	first := true
	for sc.Scan() {
		if first {
			first = false
			continue
		}
		fields := strings.Fields(sc.Text())
		if len(fields) < 6 {
			continue
		}
		ip := fields[0]
		mac := fields[3]
		if mac == "00:00:00:00:00:00" {
			continue
		}
		out = append(out, Sighting{IP: ip, MAC: strings.ToLower(mac)})
	}
	return out
}
