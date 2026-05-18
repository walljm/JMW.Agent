//go:build linux

package collect

import (
	"bufio"
	"context"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

func collectHardware(ctx context.Context) proto.HardwareInfo {
	hw := proto.HardwareInfo{}
	hw.CPULogicalCores = numCPU()

	if f, err := os.Open(hostfs.Path("/proc/cpuinfo")); err == nil {
		defer f.Close()
		sc := bufio.NewScanner(f)
		seenPhys := map[string]bool{}
		for sc.Scan() {
			line := sc.Text()
			k, v, ok := splitColon(line)
			if !ok {
				continue
			}
			switch k {
			case "model name":
				if hw.CPUModel == "" {
					hw.CPUModel = v
				}
			case "vendor_id":
				if hw.CPUVendor == "" {
					hw.CPUVendor = v
				}
			case "cpu MHz":
				if hw.CPUMHz == 0 {
					f, _ := strconv.ParseFloat(v, 64)
					hw.CPUMHz = f
				}
			case "physical id":
				seenPhys[v] = true
			case "cpu cores":
				if hw.CPUCores == 0 {
					n, _ := strconv.Atoi(v)
					hw.CPUCores = n
				}
			}
		}
		if hw.CPUCores == 0 {
			hw.CPUCores = len(seenPhys)
		}
	}

	if b, err := os.ReadFile(hostfs.Path("/proc/meminfo")); err == nil {
		for _, line := range strings.Split(string(b), "\n") {
			f := strings.Fields(line)
			if len(f) >= 2 && f[0] == "MemTotal:" {
				kb, _ := strconv.ParseUint(f[1], 10, 64)
				hw.TotalMemBytes = kb * 1024
				break
			}
		}
	}

	hw.SystemVendor = readDmi("/sys/class/dmi/id/sys_vendor")
	hw.SystemModel = readDmi("/sys/class/dmi/id/product_name")
	hw.SystemSerial = readDmi("/sys/class/dmi/id/product_serial")
	hw.BoardVendor = readDmi("/sys/class/dmi/id/board_vendor")
	hw.BoardModel = readDmi("/sys/class/dmi/id/board_name")
	hw.BIOSVendor = readDmi("/sys/class/dmi/id/bios_vendor")
	hw.BIOSVersion = readDmi("/sys/class/dmi/id/bios_version")
	hw.BIOSDate = readDmi("/sys/class/dmi/id/bios_date")

	// ARM SBCs (Raspberry Pi, HA Green, Jetson, ODROID, etc.) have no DMI
	// table but expose the same kind of facts via the device tree. Populate
	// any fields the DMI path didn't fill.
	deviceTreeFallback(&hw)

	hw.Virtualization = detectVirt(ctx)
	hw.Temperatures = collectTemperatures()
	return hw
}

// deviceTreeFallback fills empty hardware fields from /proc/device-tree on
// platforms without DMI/SMBIOS (i.e. ARM/RISC-V SBCs).
func deviceTreeFallback(hw *proto.HardwareInfo) {
	dt := hostfs.Path("/proc/device-tree")
	// /proc/device-tree/model is a NUL-terminated string like
	// "Home Assistant Green" or "Raspberry Pi 4 Model B Rev 1.4".
	if hw.SystemModel == "" {
		if s := readDTString(dt + "/model"); s != "" {
			hw.SystemModel = s
		}
	}
	if hw.SystemSerial == "" {
		if s := readDTString(dt + "/serial-number"); s != "" {
			hw.SystemSerial = s
		}
	}
	// /proc/device-tree/compatible is NUL-separated, most-specific first:
	// e.g. "radxa,cm3\0rockchip,rk3566\0". First token is the board, last is
	// usually the SoC family — both useful as a vendor hint.
	if hw.SystemVendor == "" || hw.BoardVendor == "" {
		toks := readDTStringList(dt + "/compatible")
		if len(toks) > 0 {
			// "vendor,product" → vendor is the comma-prefix of the first token.
			if i := strings.IndexByte(toks[0], ','); i > 0 {
				v := toks[0][:i]
				if hw.SystemVendor == "" {
					hw.SystemVendor = v
				}
				if hw.BoardVendor == "" {
					hw.BoardVendor = v
				}
			}
			if hw.BoardModel == "" {
				hw.BoardModel = toks[0]
			}
		}
	}
	// CPU model: device-tree CPU node's "compatible" (e.g. "arm,cortex-a55").
	if hw.CPUModel == "" {
		if s := readDTString(dt + "/cpus/cpu@0/compatible"); s != "" {
			hw.CPUModel = s
		}
	}
	// True max frequency from cpufreq (in kHz). cpuinfo's "cpu MHz" is empty
	// on ARM and on x86 reflects the current DVFS state, not the spec value.
	if hw.CPUMHz == 0 {
		if b, err := os.ReadFile(hostfs.Path("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq")); err == nil {
			if khz, err := strconv.ParseUint(strings.TrimSpace(string(b)), 10, 64); err == nil {
				hw.CPUMHz = float64(khz) / 1000.0
			}
		}
	}
}

// readDTString reads a single NUL-terminated device-tree property string.
func readDTString(path string) string {
	b, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	// Trim trailing NULs and whitespace.
	return strings.TrimRight(strings.TrimRight(string(b), "\x00"), " \t\r\n")
}

// readDTStringList reads a NUL-separated device-tree string list (e.g.
// /proc/device-tree/compatible).
func readDTStringList(path string) []string {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	parts := strings.Split(strings.TrimRight(string(b), "\x00"), "\x00")
	out := parts[:0]
	for _, p := range parts {
		if p = strings.TrimSpace(p); p != "" {
			out = append(out, p)
		}
	}
	return out
}

// collectTemperatures samples every kernel thermal zone. Returns nil when no
// /sys/class/thermal/thermal_zone* nodes exist (most VMs, containers without
// /sys mounted, x86 servers without lm-sensors style zones).
func collectTemperatures() []proto.TempReading {
	entries, err := os.ReadDir(hostfs.Path("/sys/class/thermal"))
	if err != nil {
		return nil
	}
	var out []proto.TempReading
	for _, e := range entries {
		name := e.Name()
		if !strings.HasPrefix(name, "thermal_zone") {
			continue
		}
		base := hostfs.Path("/sys/class/thermal/" + name)
		raw, err := os.ReadFile(base + "/temp")
		if err != nil {
			continue
		}
		// Kernel reports millidegrees Celsius.
		milli, err := strconv.ParseInt(strings.TrimSpace(string(raw)), 10, 64)
		if err != nil {
			continue
		}
		// Filter implausible values: many SoC zones report 0 or huge sentinels
		// when their sensor is uninitialized.
		c := float64(milli) / 1000.0
		if c <= 0 || c > 150 {
			continue
		}
		t := proto.TempReading{
			Name:    name,
			Type:    strings.TrimSpace(readSafe(base + "/type")),
			Celsius: c,
		}
		out = append(out, t)
	}
	return out
}

func readDmi(path string) string {
	b, err := os.ReadFile(hostfs.Path(path))
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(b))
}

func detectVirt(ctx context.Context) string {
	// In container-observes-host mode the dockerenv check would always fire
	// because the agent itself is in a container; the host is not.
	if hostfs.Active() {
		if b, err := os.ReadFile(hostfs.Path("/proc/sys/kernel/osrelease")); err == nil {
			s := strings.ToLower(string(b))
			if strings.Contains(s, "microsoft") || strings.Contains(s, "wsl") {
				return "wsl"
			}
		}
		return "none"
	}
	if _, err := exec.LookPath("systemd-detect-virt"); err == nil {
		s, err := runCmd(ctx, "systemd-detect-virt")
		if err == nil {
			s = strings.TrimSpace(s)
			if s != "" {
				return s
			}
		}
	}
	// /proc/1/sched line 1 starts with "init " or similar; check container hints.
	if _, err := os.Stat("/.dockerenv"); err == nil {
		return "docker"
	}
	if b, err := os.ReadFile("/proc/sys/kernel/osrelease"); err == nil {
		s := strings.ToLower(string(b))
		if strings.Contains(s, "microsoft") || strings.Contains(s, "wsl") {
			return "wsl"
		}
	}
	return "none"
}

func splitColon(s string) (k, v string, ok bool) {
	i := strings.IndexByte(s, ':')
	if i < 0 {
		return "", "", false
	}
	return strings.TrimSpace(s[:i]), strings.TrimSpace(s[i+1:]), true
}

func collectOS(ctx context.Context) proto.OSInfo {
	o := proto.OSInfo{Family: "linux"}
	o.Hostname = hostHostname()
	o.Timezone, _ = time.Now().Zone()

	if b, err := os.ReadFile(hostfs.Path("/proc/sys/kernel/osrelease")); err == nil {
		o.Kernel = strings.TrimSpace(string(b))
	}
	o.KernelArch = unameMachine()
	if b, err := os.ReadFile(hostfs.Path("/etc/os-release")); err == nil {
		kv := parseKVPairs(string(b))
		o.Distro = kv["NAME"]
		o.Version = kv["VERSION"]
		o.Build = kv["VERSION_ID"]
	}
	if b, err := os.ReadFile(hostfs.Path("/proc/uptime")); err == nil {
		fs := strings.Fields(string(b))
		if len(fs) > 0 {
			s, _ := strconv.ParseFloat(fs[0], 64)
			o.BootTime = time.Now().Add(-time.Duration(s) * time.Second).UTC()
		}
	}
	// Install date: birth time of root filesystem (best-effort: stat /var/log/installer)
	if fi, err := os.Stat(hostfs.Path("/var/log/installer")); err == nil {
		o.InstallDate = fi.ModTime().UTC()
	} else if fi, err := os.Stat(hostfs.Path("/lost+found")); err == nil {
		o.InstallDate = fi.ModTime().UTC()
	}
	return o
}

// hostHostname is retained for call-site clarity; it now delegates to
// hostfs.Hostname() so the container-aware logic lives in one place.
func hostHostname() string { return hostfs.Hostname() }

func parseKVPairs(s string) map[string]string {
	out := map[string]string{}
	for _, line := range strings.Split(s, "\n") {
		i := strings.IndexByte(line, '=')
		if i < 0 {
			continue
		}
		k := strings.TrimSpace(line[:i])
		v := strings.TrimSpace(line[i+1:])
		v = strings.Trim(v, `"`)
		out[k] = v
	}
	return out
}

func unameMachine() string {
	var u syscall.Utsname
	if err := syscall.Uname(&u); err != nil {
		return ""
	}
	b := make([]byte, 0, len(u.Machine))
	for _, c := range u.Machine {
		if c == 0 {
			break
		}
		b = append(b, byte(c))
	}
	return string(b)
}

func collectDisks(ctx context.Context) []proto.DiskDevice {
	entries, err := os.ReadDir(hostfs.Path("/sys/block"))
	if err != nil {
		return nil
	}
	mounts := mountsByDevice()
	var out []proto.DiskDevice
	for _, e := range entries {
		name := e.Name()
		if strings.HasPrefix(name, "loop") || strings.HasPrefix(name, "ram") || strings.HasPrefix(name, "dm-") {
			continue
		}
		base := hostfs.Path("/sys/block/" + name)
		dd := proto.DiskDevice{
			Name:      name,
			Model:     strings.TrimSpace(readSafe(base + "/device/model")),
			Serial:    strings.TrimSpace(readSafe(base + "/device/serial")),
			Removable: readSafe(base+"/removable") == "1\n",
		}
		if sectorsStr := strings.TrimSpace(readSafe(base + "/size")); sectorsStr != "" {
			sectors, _ := strconv.ParseUint(sectorsStr, 10, 64)
			dd.SizeBytes = sectors * 512
		}
		// rotational: 0 = SSD, 1 = HDD
		rot := strings.TrimSpace(readSafe(base + "/queue/rotational"))
		switch {
		case strings.HasPrefix(name, "nvme"):
			dd.Type = "nvme"
		case rot == "0":
			dd.Type = "ssd"
		case rot == "1":
			dd.Type = "hdd"
		default:
			dd.Type = "unknown"
		}
		// partitions
		partEntries, _ := os.ReadDir(base)
		for _, pe := range partEntries {
			if !pe.IsDir() {
				continue
			}
			pn := pe.Name()
			if !strings.HasPrefix(pn, name) {
				continue
			}
			p := proto.DiskPartition{Name: pn}
			if sStr := strings.TrimSpace(readSafe(filepath.Join(base, pn, "size"))); sStr != "" {
				sectors, _ := strconv.ParseUint(sStr, 10, 64)
				p.SizeBytes = sectors * 512
			}
			devPath := "/dev/" + pn
			if m, ok := mounts[devPath]; ok {
				p.Mountpoint = m.mp
				p.FSType = m.fs
			}
			dd.Partitions = append(dd.Partitions, p)
		}
		out = append(out, dd)
	}
	return out
}

type mountInfo struct{ mp, fs string }

func mountsByDevice() map[string]mountInfo {
	out := map[string]mountInfo{}
	f, err := os.Open(hostfs.Path("/proc/mounts"))
	if err != nil {
		return out
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fs := strings.Fields(sc.Text())
		if len(fs) < 3 {
			continue
		}
		out[fs[0]] = mountInfo{mp: fs[1], fs: fs[2]}
	}
	return out
}

func readSafe(p string) string {
	b, err := os.ReadFile(p)
	if err != nil {
		return ""
	}
	return string(b)
}

func ifaceEnrich(name string, ni *proto.NetInterface) {
	if speed := strings.TrimSpace(readSafe(hostfs.Path("/sys/class/net/" + name + "/speed"))); speed != "" {
		n, _ := strconv.Atoi(speed)
		if n > 0 {
			ni.LinkSpeedMbps = n
		}
	}
	if dup := strings.TrimSpace(readSafe(hostfs.Path("/sys/class/net/" + name + "/duplex"))); dup != "" {
		ni.Duplex = dup
	}
	if ni.Type == "" {
		// type 1 = ethernet, 32 = infiniband, etc; check for wireless dir
		if _, err := os.Stat(hostfs.Path("/sys/class/net/" + name + "/wireless")); err == nil {
			ni.Type = "wifi"
		} else if _, err := os.Stat(hostfs.Path("/sys/class/net/" + name + "/device")); err == nil {
			ni.Type = "ethernet"
		} else {
			ni.Type = "virtual"
		}
	}
}

func defaultGateways(ctx context.Context) (gw4, gw6 string) {
	if out, err := runCmd(ctx, "ip", "-4", "route", "show", "default"); err == nil {
		fs := strings.Fields(out)
		for i, t := range fs {
			if t == "via" && i+1 < len(fs) {
				gw4 = fs[i+1]
				break
			}
		}
	}
	if out, err := runCmd(ctx, "ip", "-6", "route", "show", "default"); err == nil {
		fs := strings.Fields(out)
		for i, t := range fs {
			if t == "via" && i+1 < len(fs) {
				gw6 = fs[i+1]
				break
			}
		}
	}
	return gw4, gw6
}

func collectRoutes(ctx context.Context) []proto.RouteEntry {
	var out []proto.RouteEntry
	parse := func(family, raw string) {
		for _, line := range strings.Split(raw, "\n") {
			line = strings.TrimSpace(line)
			if line == "" {
				continue
			}
			fs := strings.Fields(line)
			r := proto.RouteEntry{Family: family}
			r.Destination = fs[0]
			for i := 1; i < len(fs); i++ {
				switch fs[i] {
				case "via":
					if i+1 < len(fs) {
						r.Gateway = fs[i+1]
						i++
					}
				case "dev":
					if i+1 < len(fs) {
						r.Iface = fs[i+1]
						i++
					}
				case "metric":
					if i+1 < len(fs) {
						n, _ := strconv.Atoi(fs[i+1])
						r.Metric = n
						i++
					}
				}
			}
			out = append(out, r)
		}
	}
	if s, err := runCmd(ctx, "ip", "-4", "route"); err == nil {
		parse("ipv4", s)
	}
	if s, err := runCmd(ctx, "ip", "-6", "route"); err == nil {
		parse("ipv6", s)
	}
	return out
}

func collectUsers(ctx context.Context) []proto.UserSession {
	out := []proto.UserSession{}
	s, err := runCmd(ctx, "who")
	if err != nil {
		return nil
	}
	for _, line := range strings.Split(s, "\n") {
		fs := strings.Fields(line)
		if len(fs) < 2 {
			continue
		}
		u := proto.UserSession{User: fs[0], TTY: fs[1]}
		// who output: user tty date time [host]
		if len(fs) >= 5 {
			ts := fs[2] + " " + fs[3]
			if t, err := time.ParseInLocation("2006-01-02 15:04", ts, time.Local); err == nil {
				u.LoginAt = t.UTC()
			}
		}
		if len(fs) >= 6 {
			u.Host = strings.Trim(fs[len(fs)-1], "()")
		}
		out = append(out, u)
	}
	return out
}

func collectListening(ctx context.Context) []proto.ListeningPort {
	// When the agent runs inside a container with the host filesystem
	// bind-mounted (JMW_HOST_ROOT set) but without --network=host, `ss`
	// only sees the container's own netns. Parse PID 1's /proc/net/*
	// files through the host-root prefix instead — PID 1 lives in the
	// host network namespace, so its socket tables reflect host sockets.
	// Process name/PID resolution is skipped in this path; it would
	// require walking /host/proc/*/fd/* to match socket inodes.
	if hostfs.Active() {
		if out := readHostListening(); out != nil {
			return out
		}
	}
	// Prefer ss; fallback to /proc/net/tcp parsing skipped for brevity.
	out := []proto.ListeningPort{}
	s, err := runCmd(ctx, "ss", "-tulnp")
	if err != nil {
		return nil
	}
	for _, line := range strings.Split(s, "\n") {
		fs := strings.Fields(line)
		if len(fs) < 5 {
			continue
		}
		proto1 := fs[0]
		if proto1 != "tcp" && proto1 != "udp" {
			continue
		}
		// State filter (UNCONN for udp, LISTEN for tcp)
		state := fs[1]
		if proto1 == "tcp" && state != "LISTEN" {
			continue
		}
		local := fs[4]
		addr, port := splitHostPort(local)
		lp := proto.ListeningPort{Proto: proto1, Address: addr, Port: port}
		if strings.Contains(addr, ":") && !strings.Contains(addr, ".") {
			lp.Proto = proto1 + "6"
		}
		// process column: users:(("name",pid=123,fd=4))
		for _, f := range fs {
			if strings.HasPrefix(f, "users:") {
				name, pid := parseSSProc(f)
				lp.ProcessName = name
				lp.PID = pid
			}
		}
		out = append(out, lp)
	}
	return out
}

// readHostListening parses PID 1's socket tables in the host filesystem
// (reached via JMW_HOST_ROOT) and returns LISTEN tcp + bound udp endpoints.
// Returns nil if none of the files could be read.
func readHostListening() []proto.ListeningPort {
	files := []struct {
		path string
		// proto reported on the wire ("tcp", "tcp6", "udp", "udp6")
		proto string
		// state value indicating "listening" for this protocol
		// (TCP=0A, UDP=07 — UDP_LISTEN doesn't exist; bound unconnected
		// UDP sockets report TCP_CLOSE which is state 7)
		listenState string
		v6          bool
	}{
		{"/proc/1/net/tcp", "tcp", "0A", false},
		{"/proc/1/net/tcp6", "tcp6", "0A", true},
		{"/proc/1/net/udp", "udp", "07", false},
		{"/proc/1/net/udp6", "udp6", "07", true},
	}
	out := []proto.ListeningPort{}
	any := false
	for _, f := range files {
		b, err := os.ReadFile(hostfs.Path(f.path))
		if err != nil {
			continue
		}
		any = true
		for i, line := range strings.Split(string(b), "\n") {
			if i == 0 {
				continue // header
			}
			fs := strings.Fields(line)
			if len(fs) < 4 {
				continue
			}
			if !strings.EqualFold(fs[3], f.listenState) {
				continue
			}
			addr, port := parseProcNetAddr(fs[1], f.v6)
			if port == 0 {
				continue
			}
			out = append(out, proto.ListeningPort{
				Proto:   f.proto,
				Address: addr,
				Port:    port,
			})
		}
	}
	if !any {
		return nil
	}
	return out
}

// parseProcNetAddr decodes a "HEXIP:HEXPORT" field from /proc/net/{tcp,udp}{,6}.
// IPv4 addresses are little-endian 32-bit; IPv6 addresses are 32 hex chars,
// 4 bytes per word, each word little-endian.
func parseProcNetAddr(s string, v6 bool) (string, int) {
	colon := strings.LastIndexByte(s, ':')
	if colon < 0 {
		return "", 0
	}
	hexAddr := s[:colon]
	hexPort := s[colon+1:]
	port64, err := strconv.ParseUint(hexPort, 16, 16)
	if err != nil {
		return "", 0
	}
	port := int(port64)
	if v6 {
		if len(hexAddr) != 32 {
			return "", port
		}
		b := make([]byte, 16)
		for w := 0; w < 4; w++ {
			word, err := strconv.ParseUint(hexAddr[w*8:(w+1)*8], 16, 32)
			if err != nil {
				return "", port
			}
			// Each 32-bit word is stored little-endian.
			b[w*4+0] = byte(word)
			b[w*4+1] = byte(word >> 8)
			b[w*4+2] = byte(word >> 16)
			b[w*4+3] = byte(word >> 24)
		}
		ip := net.IP(b)
		if ip.IsUnspecified() {
			return "::", port
		}
		// Render v4-mapped (::ffff:a.b.c.d) addresses as their v4 form.
		if v4 := ip.To4(); v4 != nil {
			return v4.String(), port
		}
		return ip.String(), port
	}
	if len(hexAddr) != 8 {
		return "", port
	}
	v, err := strconv.ParseUint(hexAddr, 16, 32)
	if err != nil {
		return "", port
	}
	ip := net.IPv4(byte(v), byte(v>>8), byte(v>>16), byte(v>>24))
	return ip.String(), port
}

func splitHostPort(s string) (string, int) {
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

func parseSSProc(s string) (name string, pid int) {
	// users:(("sshd",pid=1234,fd=3),("foo",pid=5,fd=6))
	open := strings.Index(s, `(("`)
	if open < 0 {
		return "", 0
	}
	rest := s[open+3:]
	close := strings.Index(rest, `"`)
	if close < 0 {
		return "", 0
	}
	name = rest[:close]
	pidIdx := strings.Index(rest, "pid=")
	if pidIdx < 0 {
		return name, 0
	}
	tail := rest[pidIdx+4:]
	end := strings.IndexAny(tail, ",)")
	if end < 0 {
		return name, 0
	}
	pid, _ = strconv.Atoi(tail[:end])
	return name, pid
}

func collectProcesses(ctx context.Context) []proto.ProcessSummary {
	s, err := runCmd(ctx, "ps", "-eo", "pid,user,pcpu,pmem,rss,comm,args", "--no-headers")
	if err != nil {
		return nil
	}
	var out []proto.ProcessSummary
	for _, line := range strings.Split(s, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		// Up to 6 splits; the rest is args
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
	// Top 25 by CPU.
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
	s, err := runCmd(ctx, "last", "-x", "-F", "reboot")
	if err != nil {
		return nil
	}
	var out []proto.BootRecord
	for _, line := range strings.Split(s, "\n") {
		if !strings.HasPrefix(line, "reboot") {
			continue
		}
		// "reboot   system boot  6.8.0-49-generic Mon Apr 27 10:33:42 2026 - still running"
		idx := strings.Index(line, "system boot")
		if idx < 0 {
			continue
		}
		rest := strings.TrimSpace(line[idx+len("system boot"):])
		fs := strings.Fields(rest)
		if len(fs) < 7 {
			continue
		}
		kernel := fs[0]
		ts := strings.Join(fs[1:6], " ")
		t, err := time.Parse("Mon Jan 2 15:04:05 2006", ts)
		if err != nil {
			continue
		}
		out = append(out, proto.BootRecord{BootedAt: t.UTC(), Kernel: kernel})
		if len(out) >= 20 {
			break
		}
	}
	return out
}

func collectPackages(ctx context.Context) *proto.PackageInventory {
	if _, err := exec.LookPath("dpkg-query"); err == nil {
		s, err := runCmd(ctx, "dpkg-query", "-W", "-f=${Package}\t${Version}\t${Architecture}\n")
		if err == nil {
			pi := &proto.PackageInventory{Manager: "dpkg"}
			for _, line := range strings.Split(s, "\n") {
				fs := strings.Split(line, "\t")
				if len(fs) < 2 || fs[0] == "" {
					continue
				}
				p := proto.Package{Name: fs[0], Version: fs[1]}
				if len(fs) >= 3 {
					p.Arch = fs[2]
				}
				pi.Packages = append(pi.Packages, p)
			}
			pi.Count = len(pi.Packages)
			return pi
		}
	}
	if _, err := exec.LookPath("rpm"); err == nil {
		s, err := runCmd(ctx, "rpm", "-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{ARCH}\n")
		if err == nil {
			pi := &proto.PackageInventory{Manager: "rpm"}
			for _, line := range strings.Split(s, "\n") {
				fs := strings.Split(line, "\t")
				if len(fs) < 2 || fs[0] == "" {
					continue
				}
				p := proto.Package{Name: fs[0], Version: fs[1]}
				if len(fs) >= 3 {
					p.Arch = fs[2]
				}
				pi.Packages = append(pi.Packages, p)
			}
			pi.Count = len(pi.Packages)
			return pi
		}
	}
	return nil
}

func numCPU() int { return cpuLogicalCount() }

func cpuLogicalCount() int {
	if b, err := os.ReadFile(hostfs.Path("/proc/cpuinfo")); err == nil {
		n := 0
		for _, line := range strings.Split(string(b), "\n") {
			if strings.HasPrefix(line, "processor") {
				n++
			}
		}
		if n > 0 {
			return n
		}
	}
	return 0
}

// Avoid unused-import flags when build tags shrink usage.
var _ = net.Interfaces
