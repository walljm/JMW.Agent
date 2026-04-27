//go:build darwin

package collect

import (
	"bufio"
	"context"
	"os"
	"os/exec"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

func collectHardware(ctx context.Context) proto.HardwareInfo {
	hw := proto.HardwareInfo{}
	hw.CPUModel = trimNL(sysctlString(ctx, "machdep.cpu.brand_string"))
	if hw.CPUModel == "" {
		hw.CPUModel = trimNL(sysctlString(ctx, "machdep.cpu.brand_string"))
	}
	hw.CPUVendor = trimNL(sysctlString(ctx, "machdep.cpu.vendor"))
	if n, err := strconv.Atoi(trimNL(sysctlString(ctx, "hw.physicalcpu"))); err == nil {
		hw.CPUCores = n
	}
	if n, err := strconv.Atoi(trimNL(sysctlString(ctx, "hw.logicalcpu"))); err == nil {
		hw.CPULogicalCores = n
	}
	if hz, err := strconv.ParseFloat(trimNL(sysctlString(ctx, "hw.cpufrequency")), 64); err == nil && hz > 0 {
		hw.CPUMHz = hz / 1_000_000
	}
	if n, err := strconv.ParseUint(trimNL(sysctlString(ctx, "hw.memsize")), 10, 64); err == nil {
		hw.TotalMemBytes = n
	}
	hw.SystemVendor = "Apple Inc."
	hw.SystemModel = trimNL(sysctlString(ctx, "hw.model"))
	// Serial via system_profiler
	if out, err := runCmd(ctx, "system_profiler", "SPHardwareDataType"); err == nil {
		for _, line := range strings.Split(out, "\n") {
			line = strings.TrimSpace(line)
			if strings.HasPrefix(line, "Serial Number (system):") {
				hw.SystemSerial = strings.TrimSpace(strings.TrimPrefix(line, "Serial Number (system):"))
			}
		}
	}
	hw.Virtualization = "none"
	if isVirt := trimNL(sysctlString(ctx, "kern.hv_vmm_present")); isVirt == "1" {
		hw.Virtualization = "vm"
	}
	return hw
}

func sysctlString(ctx context.Context, key string) string {
	out, err := runCmd(ctx, "sysctl", "-n", key)
	if err != nil {
		return ""
	}
	return out
}

func collectOS(ctx context.Context) proto.OSInfo {
	o := proto.OSInfo{Family: "darwin"}
	o.Hostname, _ = os.Hostname()
	o.Timezone, _ = time.Now().Zone()
	o.Kernel = trimNL(sysctlString(ctx, "kern.osrelease"))
	o.Build = trimNL(sysctlString(ctx, "kern.osversion"))
	o.KernelArch = trimNL(sysctlString(ctx, "hw.machine"))
	if out, err := runCmd(ctx, "sw_vers"); err == nil {
		kv := map[string]string{}
		for _, line := range strings.Split(out, "\n") {
			i := strings.IndexByte(line, ':')
			if i < 0 {
				continue
			}
			kv[strings.TrimSpace(line[:i])] = strings.TrimSpace(line[i+1:])
		}
		o.Distro = kv["ProductName"]
		o.Version = kv["ProductVersion"]
	}
	if bt := trimNL(sysctlString(ctx, "kern.boottime")); bt != "" {
		// "{ sec = 1700000000, usec = 123 } ..."
		if i := strings.Index(bt, "sec = "); i >= 0 {
			rest := bt[i+6:]
			if j := strings.IndexAny(rest, ", "); j > 0 {
				if s, err := strconv.ParseInt(strings.TrimSpace(rest[:j]), 10, 64); err == nil {
					o.BootTime = time.Unix(s, 0).UTC()
				}
			}
		}
	}
	if fi, err := os.Stat("/var/db/.AppleSetupDone"); err == nil {
		o.InstallDate = fi.ModTime().UTC()
	}
	return o
}

func collectDisks(ctx context.Context) []proto.DiskDevice {
	out, err := runCmd(ctx, "diskutil", "list", "-plist", "physical")
	_ = out
	if err != nil {
		// Fallback: parse "diskutil list" text.
		return parseDiskutilText(ctx)
	}
	return parseDiskutilText(ctx)
}

func parseDiskutilText(ctx context.Context) []proto.DiskDevice {
	out, err := runCmd(ctx, "diskutil", "list")
	if err != nil {
		return nil
	}
	var disks []proto.DiskDevice
	var cur *proto.DiskDevice
	scanner := bufio.NewScanner(strings.NewReader(out))
	for scanner.Scan() {
		line := scanner.Text()
		if strings.HasPrefix(line, "/dev/disk") {
			// Header: /dev/disk0 (internal, physical):
			fs := strings.Fields(line)
			name := strings.TrimPrefix(fs[0], "/dev/")
			if cur != nil {
				disks = append(disks, *cur)
			}
			cur = &proto.DiskDevice{Name: name, Type: "ssd"}
			continue
		}
		if cur == nil {
			continue
		}
		fs := strings.Fields(line)
		if len(fs) < 4 {
			continue
		}
		// Lines look like:  "   0:      GUID_partition_scheme                        *500.3 GB   disk0"
		// or              :  "   1:                        EFI EFI                     314.6 MB   disk0s1"
		last := fs[len(fs)-1]
		if !strings.HasPrefix(last, "disk") {
			continue
		}
		if last == cur.Name {
			// Whole disk size — first numeric field that ends with B-units.
			for i := len(fs) - 4; i < len(fs)-1; i++ {
				if i < 0 {
					continue
				}
				if v, ok := parseDiskutilSize(fs[i], fs[i+1]); ok {
					cur.SizeBytes = v
					break
				}
			}
		} else {
			p := proto.DiskPartition{Name: last}
			for i := 0; i < len(fs)-1; i++ {
				if v, ok := parseDiskutilSize(fs[i], fs[i+1]); ok {
					p.SizeBytes = v
					break
				}
			}
			cur.Partitions = append(cur.Partitions, p)
		}
	}
	if cur != nil {
		disks = append(disks, *cur)
	}
	// Enrich partitions with mount + fs from `mount` output.
	mountInfo := parseMountCmd(ctx)
	for i := range disks {
		for j := range disks[i].Partitions {
			devPath := "/dev/" + disks[i].Partitions[j].Name
			if m, ok := mountInfo[devPath]; ok {
				disks[i].Partitions[j].Mountpoint = m.mp
				disks[i].Partitions[j].FSType = m.fs
			}
		}
	}
	return disks
}

func parseDiskutilSize(num, unit string) (uint64, bool) {
	num = strings.TrimPrefix(num, "*")
	v, err := strconv.ParseFloat(num, 64)
	if err != nil {
		return 0, false
	}
	mult := uint64(0)
	switch unit {
	case "B":
		mult = 1
	case "KB":
		mult = 1_000
	case "MB":
		mult = 1_000_000
	case "GB":
		mult = 1_000_000_000
	case "TB":
		mult = 1_000_000_000_000
	default:
		return 0, false
	}
	return uint64(v * float64(mult)), true
}

type darwinMountInfo struct{ mp, fs string }

func parseMountCmd(ctx context.Context) map[string]darwinMountInfo {
	out := map[string]darwinMountInfo{}
	s, err := runCmd(ctx, "mount")
	if err != nil {
		return out
	}
	for _, line := range strings.Split(s, "\n") {
		// "/dev/disk1s1 on / (apfs, local, journaled)"
		fs := strings.Fields(line)
		if len(fs) < 4 || fs[1] != "on" {
			continue
		}
		mp := fs[2]
		fsType := strings.TrimPrefix(strings.TrimSuffix(fs[3], ","), "(")
		out[fs[0]] = darwinMountInfo{mp: mp, fs: fsType}
	}
	return out
}

func ifaceEnrich(name string, ni *proto.NetInterface) {
	// macOS doesn't expose link speed in a stable place without ifconfig parsing.
	// Quick best-effort:
	out, err := runCmd(context.Background(), "ifconfig", name)
	if err != nil {
		return
	}
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "media:") {
			// e.g.   "media: autoselect (1000baseT <full-duplex>)"
			if strings.Contains(line, "baseT") {
				ni.Type = "ethernet"
			}
			if strings.Contains(line, "full-duplex") {
				ni.Duplex = "full"
			} else if strings.Contains(line, "half-duplex") {
				ni.Duplex = "half"
			}
			// Speed extraction
			if i := strings.Index(line, "base"); i > 0 {
				// Walk back from "base" to first non-digit to find number
				j := i
				for j > 0 && line[j-1] >= '0' && line[j-1] <= '9' {
					j--
				}
				n, _ := strconv.Atoi(line[j:i])
				if n > 0 {
					ni.LinkSpeedMbps = n
				}
			}
		}
	}
	if ni.Type == "" {
		switch {
		case strings.HasPrefix(name, "en"):
			ni.Type = "ethernet"
		case strings.HasPrefix(name, "lo"):
			ni.Type = "loopback"
		default:
			ni.Type = "virtual"
		}
	}
}

func defaultGateways(ctx context.Context) (gw4, gw6 string) {
	if out, err := runCmd(ctx, "route", "-n", "get", "default"); err == nil {
		for _, line := range strings.Split(out, "\n") {
			line = strings.TrimSpace(line)
			if strings.HasPrefix(line, "gateway:") {
				gw4 = strings.TrimSpace(strings.TrimPrefix(line, "gateway:"))
			}
		}
	}
	if out, err := runCmd(ctx, "route", "-n", "get", "-inet6", "default"); err == nil {
		for _, line := range strings.Split(out, "\n") {
			line = strings.TrimSpace(line)
			if strings.HasPrefix(line, "gateway:") {
				gw6 = strings.TrimSpace(strings.TrimPrefix(line, "gateway:"))
			}
		}
	}
	return gw4, gw6
}

func collectRoutes(ctx context.Context) []proto.RouteEntry {
	var out []proto.RouteEntry
	parse := func(family, raw string) {
		section := false
		for _, line := range strings.Split(raw, "\n") {
			if strings.HasPrefix(line, "Destination") {
				section = true
				continue
			}
			if !section {
				continue
			}
			fs := strings.Fields(line)
			if len(fs) < 4 {
				continue
			}
			out = append(out, proto.RouteEntry{
				Family: family, Destination: fs[0], Gateway: fs[1], Iface: fs[len(fs)-1],
			})
		}
	}
	if s, err := runCmd(ctx, "netstat", "-rn", "-f", "inet"); err == nil {
		parse("ipv4", s)
	}
	if s, err := runCmd(ctx, "netstat", "-rn", "-f", "inet6"); err == nil {
		parse("ipv6", s)
	}
	return out
}

func collectUsers(ctx context.Context) []proto.UserSession {
	s, err := runCmd(ctx, "who")
	if err != nil {
		return nil
	}
	var out []proto.UserSession
	for _, line := range strings.Split(s, "\n") {
		fs := strings.Fields(line)
		if len(fs) < 2 {
			continue
		}
		u := proto.UserSession{User: fs[0], TTY: fs[1]}
		out = append(out, u)
	}
	return out
}

func collectListening(ctx context.Context) []proto.ListeningPort {
	s, err := runCmd(ctx, "lsof", "-nP", "-iTCP", "-sTCP:LISTEN")
	tcp := parseLsof(s, "tcp")
	if err != nil {
		tcp = nil
	}
	s2, err := runCmd(ctx, "lsof", "-nP", "-iUDP")
	udp := parseLsof(s2, "udp")
	if err != nil {
		udp = nil
	}
	return append(tcp, udp...)
}

func parseLsof(s, defaultProto string) []proto.ListeningPort {
	var out []proto.ListeningPort
	for i, line := range strings.Split(s, "\n") {
		if i == 0 || line == "" {
			continue
		}
		fs := strings.Fields(line)
		if len(fs) < 9 {
			continue
		}
		// COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE NAME
		name := fs[0]
		pid, _ := strconv.Atoi(fs[1])
		nodeProto := strings.ToLower(fs[7])
		bind := fs[8]
		// strip "(LISTEN)" trailers etc
		if i := strings.Index(bind, " "); i > 0 {
			bind = bind[:i]
		}
		host, port := splitHostPortDarwin(bind)
		p := defaultProto
		if nodeProto == "tcp" || nodeProto == "udp" {
			p = nodeProto
		}
		if strings.Contains(host, ":") && !strings.Contains(host, ".") {
			p += "6"
		}
		out = append(out, proto.ListeningPort{
			Proto: p, Address: host, Port: port,
			ProcessName: name, PID: pid,
		})
	}
	return out
}

func splitHostPortDarwin(s string) (string, int) {
	i := strings.LastIndexByte(s, ':')
	if i < 0 {
		return s, 0
	}
	addr := s[:i]
	addr = strings.TrimPrefix(addr, "[")
	addr = strings.TrimSuffix(addr, "]")
	if addr == "*" {
		addr = "0.0.0.0"
	}
	p, _ := strconv.Atoi(s[i+1:])
	return addr, p
}

func collectProcesses(ctx context.Context) []proto.ProcessSummary {
	s, err := runCmd(ctx, "ps", "-Ao", "pid,user,pcpu,pmem,rss,comm,args")
	if err != nil {
		return nil
	}
	var out []proto.ProcessSummary
	for i, line := range strings.Split(s, "\n") {
		if i == 0 || strings.TrimSpace(line) == "" {
			continue
		}
		fs := strings.Fields(line)
		if len(fs) < 7 {
			continue
		}
		pid, _ := strconv.Atoi(fs[0])
		cpu, _ := strconv.ParseFloat(fs[2], 64)
		mem, _ := strconv.ParseFloat(fs[3], 64)
		rss, _ := strconv.ParseUint(fs[4], 10, 64)
		args := strings.Join(fs[6:], " ")
		out = append(out, proto.ProcessSummary{
			PID: pid, User: fs[1],
			CPUPercent: cpu, MemPercent: mem, MemBytes: rss * 1024,
			Name: fs[5], Cmd: args,
		})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].CPUPercent != out[j].CPUPercent {
			return out[i].CPUPercent > out[j].CPUPercent
		}
		return out[i].MemBytes > out[j].MemBytes
	})
	if len(out) > 25 {
		out = out[:25]
	}
	return out
}

func collectReboots(ctx context.Context) []proto.BootRecord {
	s, err := runCmd(ctx, "last", "reboot")
	if err != nil {
		return nil
	}
	var out []proto.BootRecord
	for _, line := range strings.Split(s, "\n") {
		if !strings.HasPrefix(line, "reboot") {
			continue
		}
		fs := strings.Fields(line)
		// "reboot    ~                         Sun Apr 20 09:12"
		if len(fs) < 6 {
			continue
		}
		// last 4 fields = date
		ts := strings.Join(fs[len(fs)-4:], " ")
		t, err := time.ParseInLocation("Mon Jan 2 15:04", ts, time.Local)
		if err != nil {
			continue
		}
		// Year defaults to 0; assume current year.
		t = time.Date(time.Now().Year(), t.Month(), t.Day(), t.Hour(), t.Minute(), 0, 0, t.Location())
		out = append(out, proto.BootRecord{BootedAt: t.UTC()})
		if len(out) >= 20 {
			break
		}
	}
	return out
}

func collectPackages(ctx context.Context) *proto.PackageInventory {
	if _, err := exec.LookPath("brew"); err != nil {
		return nil
	}
	s, err := runCmd(ctx, "brew", "list", "--versions")
	if err != nil {
		return nil
	}
	pi := &proto.PackageInventory{Manager: "brew"}
	for _, line := range strings.Split(s, "\n") {
		fs := strings.Fields(line)
		if len(fs) < 1 {
			continue
		}
		p := proto.Package{Name: fs[0]}
		if len(fs) >= 2 {
			p.Version = fs[1]
		}
		pi.Packages = append(pi.Packages, p)
	}
	pi.Count = len(pi.Packages)
	return pi
}
