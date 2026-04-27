// Package collect builds metric snapshots from the local OS.
package collect

import (
	"net"
	"runtime"
	"time"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// Snapshot returns a metric snapshot at the current moment.
func Snapshot() proto.MetricSnapshot {
	sn := proto.MetricSnapshot{
		Timestamp:     time.Now().UTC(),
		UptimeSeconds: uptimeSeconds(),
	}
	if cpu, err := cpuPercent(); err == nil {
		sn.CPUPercent = cpu
	}
	if used, total, err := memInfo(); err == nil {
		sn.MemUsedBytes = used
		sn.MemTotalBytes = total
	}
	l1, l5, l15 := loadAvg()
	sn.Load1 = l1
	sn.Load5 = l5
	sn.Load15 = l15
	sn.Disks = diskUsage()
	sn.Interfaces = interfaceSnapshot()
	return sn
}

// Hostname returns the local hostname (best-effort). Delegates to hostfs so
// containerized agents (HA add-on, NAS docker) report the host's hostname
// rather than the container's generated name (e.g. `local-jmw-agent`).
func Hostname() string { return hostfs.Hostname() }

// OS returns the GOOS string.
func OS() string { return runtime.GOOS }

// Arch returns the GOARCH string.
func Arch() string { return runtime.GOARCH }

func interfaceSnapshot() []proto.InterfaceSnapshot {
	ifs, err := net.Interfaces()
	if err != nil {
		return nil
	}
	out := make([]proto.InterfaceSnapshot, 0, len(ifs))
	for _, ifi := range ifs {
		// Skip loopback and down interfaces with no useful data.
		if ifi.Flags&net.FlagLoopback != 0 {
			continue
		}
		s := proto.InterfaceSnapshot{
			Name: ifi.Name,
			MAC:  ifi.HardwareAddr.String(),
			IsUp: ifi.Flags&net.FlagUp != 0,
		}
		addrs, _ := ifi.Addrs()
		for _, a := range addrs {
			if ipn, ok := a.(*net.IPNet); ok && ipn.IP.To4() != nil {
				s.IP = ipn.IP.String()
				break
			}
		}
		// Per-OS rx/tx populated via ifaceStats hook (see _linux.go / _darwin.go).
		if rx, tx, rxp, txp, ok := ifaceStats(ifi.Name); ok {
			s.RxBytes = rx
			s.TxBytes = tx
			s.RxPackets = rxp
			s.TxPackets = txp
		}
		out = append(out, s)
	}
	return out
}
