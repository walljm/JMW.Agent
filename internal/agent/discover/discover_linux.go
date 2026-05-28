//go:build linux

package discover

import (
	"bufio"
	"os"
	"strconv"
	"strings"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
)

// scanARP reads /proc/net/arp.
//
// The kernel format is whitespace-separated:
//
//	IP address  HW type  Flags  HW address  Mask  Device
//
// We capture the device column so neighbors seen on a container/VM bridge
// (docker0, br-<id>, virbr*, ...) can be classified at the source instead
// of being reported as anonymous locally-administered MACs.
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
		flags, _ := strconv.ParseUint(strings.TrimPrefix(fields[2], "0x"), 16, 32)
		mac := fields[3]
		iface := fields[5]
		// ATF_COM (0x2) is only set for NUD_VALID states (REACHABLE, STALE,
		// DELAY, PROBE, PERMANENT). FAILED and INCOMPLETE entries may retain
		// the old MAC in the kernel ha buffer but have flags=0x0 — skip them.
		if flags&0x2 == 0 || mac == "00:00:00:00:00:00" {
			continue
		}
		s := Sighting{IP: ip, MAC: strings.ToLower(mac)}
		if vendor, kind := classifyBridge(iface); kind != "" {
			s.Vendor = vendor
			s.Kind = kind
		}
		out = append(out, s)
	}
	return out
}
