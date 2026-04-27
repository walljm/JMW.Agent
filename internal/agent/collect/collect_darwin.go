//go:build darwin

package collect

import (
	"os/exec"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"
	"unsafe"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

func uptimeSeconds() int64 {
	out, err := exec.Command("sysctl", "-n", "kern.boottime").Output()
	if err != nil {
		return 0
	}
	// e.g. "{ sec = 1700000000, usec = 123 } Mon Nov 14 10:00:00 2023"
	s := string(out)
	i := strings.Index(s, "sec = ")
	if i < 0 {
		return 0
	}
	rest := s[i+6:]
	j := strings.IndexAny(rest, ", ")
	if j < 0 {
		return 0
	}
	boot, _ := strconv.ParseInt(strings.TrimSpace(rest[:j]), 10, 64)
	if boot == 0 {
		return 0
	}
	return time.Now().Unix() - boot
}

func loadAvg() (float64, float64, float64) {
	out, err := exec.Command("sysctl", "-n", "vm.loadavg").Output()
	if err != nil {
		return 0, 0, 0
	}
	s := strings.TrimSpace(string(out))
	s = strings.Trim(s, "{}")
	f := strings.Fields(s)
	if len(f) < 3 {
		return 0, 0, 0
	}
	a, _ := strconv.ParseFloat(f[0], 64)
	b, _ := strconv.ParseFloat(f[1], 64)
	c, _ := strconv.ParseFloat(f[2], 64)
	return a, b, c
}

var (
	lastUser, lastSys, lastIdle, lastNice uint64
)

func cpuPercent() (float64, error) {
	u, sy, id, ni, err := readHostCPU()
	if err != nil {
		return 0, err
	}
	if lastUser == 0 && lastSys == 0 && lastIdle == 0 {
		lastUser, lastSys, lastIdle, lastNice = u, sy, id, ni
		time.Sleep(50 * time.Millisecond)
		u, sy, id, ni, err = readHostCPU()
		if err != nil {
			return 0, err
		}
	}
	dUser := u - lastUser
	dSys := sy - lastSys
	dIdle := id - lastIdle
	dNice := ni - lastNice
	lastUser, lastSys, lastIdle, lastNice = u, sy, id, ni
	totalDelta := dUser + dSys + dIdle + dNice
	if totalDelta == 0 {
		return 0, nil
	}
	busy := dUser + dSys + dNice
	return float64(busy) / float64(totalDelta) * 100.0, nil
}

// readHostCPU returns ticks via host_statistics(HOST_CPU_LOAD_INFO)... but
// since we want a no-cgo build, use top -l1 -n0 instead.
func readHostCPU() (user, sys, idle, nice uint64, err error) {
	out, err := exec.Command("ps", "-A", "-o", "%cpu").Output()
	if err != nil {
		return 0, 0, 0, 0, err
	}
	var sum float64
	for _, line := range strings.Split(string(out), "\n")[1:] {
		v, e := strconv.ParseFloat(strings.TrimSpace(line), 64)
		if e == nil {
			sum += v
		}
	}
	// Treat ps cpu sum as "busy" tick count, idle as (cpus*100 - busy).
	cpus := uint64(runtime.NumCPU())
	busy := uint64(sum)
	if busy > cpus*100 {
		busy = cpus * 100
	}
	idle = cpus*100 - busy
	user = busy
	return user, 0, idle, 0, nil
}

func memInfo() (used, total uint64, err error) {
	out, err := exec.Command("sysctl", "-n", "hw.memsize").Output()
	if err != nil {
		return 0, 0, err
	}
	total, _ = strconv.ParseUint(strings.TrimSpace(string(out)), 10, 64)

	vmOut, err := exec.Command("vm_stat").Output()
	if err != nil {
		return 0, 0, err
	}
	pageSize := uint64(4096)
	var free, active, inactive, wired, compressed uint64
	for _, line := range strings.Split(string(vmOut), "\n") {
		line = strings.TrimSpace(line)
		switch {
		case strings.HasPrefix(line, "Mach Virtual Memory Statistics:"):
			if i := strings.LastIndex(line, "page size of "); i >= 0 {
				ps := strings.TrimSuffix(strings.TrimSpace(strings.TrimPrefix(line[i:], "page size of ")), " bytes)")
				if v, e := strconv.ParseUint(strings.Fields(ps)[0], 10, 64); e == nil && v > 0 {
					pageSize = v
				}
			}
		case strings.HasPrefix(line, "Pages free:"):
			free = parsePagesLine(line)
		case strings.HasPrefix(line, "Pages active:"):
			active = parsePagesLine(line)
		case strings.HasPrefix(line, "Pages inactive:"):
			inactive = parsePagesLine(line)
		case strings.HasPrefix(line, "Pages wired down:"):
			wired = parsePagesLine(line)
		case strings.HasPrefix(line, "Pages occupied by compressor:"):
			compressed = parsePagesLine(line)
		}
	}
	_ = free
	_ = inactive
	used = (active + wired + compressed) * pageSize
	return used, total, nil
}

func parsePagesLine(line string) uint64 {
	i := strings.LastIndex(line, ":")
	if i < 0 {
		return 0
	}
	v := strings.TrimSpace(strings.TrimSuffix(line[i+1:], "."))
	v = strings.ReplaceAll(v, ",", "")
	n, _ := strconv.ParseUint(v, 10, 64)
	return n
}

func diskUsage() []proto.DiskSnapshot {
	mounts := listMounts()
	out := make([]proto.DiskSnapshot, 0, len(mounts))
	for _, m := range mounts {
		var st syscall.Statfs_t
		if err := syscall.Statfs(m.mountpoint, &st); err != nil {
			continue
		}
		total := uint64(st.Blocks) * uint64(st.Bsize)
		free := uint64(st.Bavail) * uint64(st.Bsize)
		out = append(out, proto.DiskSnapshot{
			Device:     m.device,
			Mountpoint: m.mountpoint,
			UsedBytes:  total - free,
			TotalBytes: total,
			FSType:     m.fstype,
		})
	}
	return out
}

type darwinMount struct {
	device, mountpoint, fstype string
}

func listMounts() []darwinMount {
	// getfsstat returns mount entries.
	const mntNoWait = 2
	count, err := syscall.Getfsstat(nil, mntNoWait)
	if err != nil || count <= 0 {
		return nil
	}
	stats := make([]syscall.Statfs_t, count)
	count, err = syscall.Getfsstat(stats, mntNoWait)
	if err != nil {
		return nil
	}
	out := make([]darwinMount, 0, count)
	for i := 0; i < count; i++ {
		s := stats[i]
		dev := byteSliceToString(s.Mntfromname[:])
		mp := byteSliceToString(s.Mntonname[:])
		fs := byteSliceToString(s.Fstypename[:])
		// Skip pseudo-filesystems we don't care about.
		switch fs {
		case "devfs", "autofs", "tmpfs", "fdesc":
			continue
		}
		// Skip system read-only firmlinks (anything starting with /System/Volumes/Update or VM)
		if strings.HasPrefix(mp, "/System/Volumes/VM") {
			continue
		}
		out = append(out, darwinMount{device: dev, mountpoint: mp, fstype: fs})
	}
	return out
}

func byteSliceToString(b []int8) string {
	// Convert []int8 to []byte then trim NUL.
	bb := make([]byte, len(b))
	for i, v := range b {
		bb[i] = byte(v)
	}
	if i := strings.IndexByte(string(bb), 0); i >= 0 {
		return string(bb[:i])
	}
	return string(bb)
}

// Avoid unused import warnings on some toolchains.
var _ unsafe.Pointer

func ifaceStats(name string) (rx, tx, rxp, txp uint64, ok bool) {
	out, err := exec.Command("netstat", "-ibn").Output()
	if err != nil {
		return 0, 0, 0, 0, false
	}
	for _, line := range strings.Split(string(out), "\n") {
		fields := strings.Fields(line)
		if len(fields) < 11 {
			continue
		}
		if fields[0] != name {
			continue
		}
		// Only interpret rows with a Link# address (one row per iface).
		if !strings.HasPrefix(fields[3], "<Link#") {
			continue
		}
		rxp, _ = strconv.ParseUint(fields[4], 10, 64)
		rx, _ = strconv.ParseUint(fields[6], 10, 64)
		txp, _ = strconv.ParseUint(fields[7], 10, 64)
		tx, _ = strconv.ParseUint(fields[9], 10, 64)
		return rx, tx, rxp, txp, true
	}
	return 0, 0, 0, 0, false
}
