//go:build linux

package collect

import (
	"bufio"
	"os"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

func uptimeSeconds() int64 {
	b, err := os.ReadFile(hostfs.Path("/proc/uptime"))
	if err != nil {
		return 0
	}
	parts := strings.Fields(string(b))
	if len(parts) == 0 {
		return 0
	}
	f, _ := strconv.ParseFloat(parts[0], 64)
	return int64(f)
}

func loadAvg() (float64, float64, float64) {
	b, err := os.ReadFile(hostfs.Path("/proc/loadavg"))
	if err != nil {
		return 0, 0, 0
	}
	p := strings.Fields(string(b))
	if len(p) < 3 {
		return 0, 0, 0
	}
	a, _ := strconv.ParseFloat(p[0], 64)
	c, _ := strconv.ParseFloat(p[1], 64)
	d, _ := strconv.ParseFloat(p[2], 64)
	return a, c, d
}

var (
	lastCPUTotal uint64
	lastCPUIdle  uint64
)

func cpuPercent() (float64, error) {
	total, idle, err := readCPUStat()
	if err != nil {
		return 0, err
	}
	if lastCPUTotal == 0 {
		lastCPUTotal, lastCPUIdle = total, idle
		time.Sleep(50 * time.Millisecond)
		total, idle, err = readCPUStat()
		if err != nil {
			return 0, err
		}
	}
	dt := total - lastCPUTotal
	di := idle - lastCPUIdle
	lastCPUTotal, lastCPUIdle = total, idle
	if dt == 0 {
		return 0, nil
	}
	return float64(dt-di) / float64(dt) * 100.0, nil
}

func readCPUStat() (total, idle uint64, err error) {
	f, err := os.Open(hostfs.Path("/proc/stat"))
	if err != nil {
		return 0, 0, err
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	if !sc.Scan() {
		return 0, 0, sc.Err()
	}
	fields := strings.Fields(sc.Text())
	if len(fields) < 5 || fields[0] != "cpu" {
		return 0, 0, nil
	}
	for i, v := range fields[1:] {
		n, _ := strconv.ParseUint(v, 10, 64)
		total += n
		if i == 3 { // idle
			idle = n
		}
	}
	return total, idle, nil
}

func memInfo() (used, total uint64, err error) {
	f, err := os.Open(hostfs.Path("/proc/meminfo"))
	if err != nil {
		return 0, 0, err
	}
	defer f.Close()
	var memTotal, memAvail uint64
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := sc.Text()
		f := strings.Fields(line)
		if len(f) < 2 {
			continue
		}
		switch f[0] {
		case "MemTotal:":
			memTotal, _ = strconv.ParseUint(f[1], 10, 64)
		case "MemAvailable:":
			memAvail, _ = strconv.ParseUint(f[1], 10, 64)
		}
	}
	if memTotal == 0 {
		return 0, 0, nil
	}
	memTotal *= 1024
	memAvail *= 1024
	used = memTotal - memAvail
	return used, memTotal, nil
}

func diskUsage() []proto.DiskSnapshot {
	mounts := readMounts()
	out := make([]proto.DiskSnapshot, 0, len(mounts))
	for _, m := range mounts {
		var st syscall.Statfs_t
		// Statfs the mountpoint via the host-root prefix when running in
		// container-observes-host mode; the reported Mountpoint field is the
		// host's truth (unprefixed).
		if err := syscall.Statfs(hostfs.Path(m.mountpoint), &st); err != nil {
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

type mountEntry struct {
	device, mountpoint, fstype string
}

func readMounts() []mountEntry {
	skipFS := map[string]bool{
		"proc": true, "sysfs": true, "devpts": true, "tmpfs": true,
		"devtmpfs": true, "cgroup": true, "cgroup2": true, "pstore": true,
		"debugfs": true, "tracefs": true, "mqueue": true, "hugetlbfs": true,
		"securityfs": true, "fusectl": true, "binfmt_misc": true,
		"autofs": true, "rpc_pipefs": true, "configfs": true, "bpf": true,
	}
	f, err := os.Open(hostfs.Path("/proc/mounts"))
	if err != nil {
		return nil
	}
	defer f.Close()
	var out []mountEntry
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Fields(sc.Text())
		if len(fields) < 3 {
			continue
		}
		if skipFS[fields[2]] {
			continue
		}
		out = append(out, mountEntry{device: fields[0], mountpoint: fields[1], fstype: fields[2]})
	}
	return out
}

func ifaceStats(name string) (rx, tx, rxp, txp uint64, ok bool) {
	b, err := os.ReadFile(hostfs.Path("/proc/net/dev"))
	if err != nil {
		return 0, 0, 0, 0, false
	}
	for _, line := range strings.Split(string(b), "\n") {
		i := strings.Index(line, ":")
		if i < 0 {
			continue
		}
		n := strings.TrimSpace(line[:i])
		if n != name {
			continue
		}
		fields := strings.Fields(line[i+1:])
		if len(fields) < 16 {
			return 0, 0, 0, 0, false
		}
		rx, _ = strconv.ParseUint(fields[0], 10, 64)
		rxp, _ = strconv.ParseUint(fields[1], 10, 64)
		tx, _ = strconv.ParseUint(fields[8], 10, 64)
		txp, _ = strconv.ParseUint(fields[9], 10, 64)
		return rx, tx, rxp, txp, true
	}
	return 0, 0, 0, 0, false
}
