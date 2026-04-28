//go:build linux

package discover

import (
	"bufio"
	"os"
	"strconv"
	"strings"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
)

// platformDefaultGateway reads /proc/net/route and returns the gateway
// IPv4 of the default route (Destination=00000000). The Gateway column
// is the IP in little-endian hex (e.g. "0101A8C0" -> 192.168.1.1).
func platformDefaultGateway() string {
	f, err := os.Open(hostfs.Path("/proc/net/route"))
	if err != nil {
		return ""
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	first := true
	for sc.Scan() {
		if first {
			first = false
			continue
		}
		fields := strings.Fields(sc.Text())
		if len(fields) < 4 {
			continue
		}
		if fields[1] != "00000000" {
			continue
		}
		gwHex := fields[2]
		if len(gwHex) != 8 {
			continue
		}
		// Little-endian: bytes are dd cc bb aa for IP a.b.c.d.
		var b [4]byte
		for i := 0; i < 4; i++ {
			x, err := strconv.ParseUint(gwHex[i*2:i*2+2], 16, 8)
			if err != nil {
				return ""
			}
			b[3-i] = byte(x)
		}
		return strconv.Itoa(int(b[0])) + "." + strconv.Itoa(int(b[1])) + "." + strconv.Itoa(int(b[2])) + "." + strconv.Itoa(int(b[3]))
	}
	return ""
}
