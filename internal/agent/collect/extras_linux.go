//go:build linux

package collect

import (
	"bufio"
	"context"
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// ----- Filesystems ------------------------------------------------------

// collectFilesystems walks /proc/mounts and statfs's each interesting
// mountpoint. We exclude pseudo/virtual filesystems (proc, sysfs, tmpfs,
// cgroup, etc.) so the list reflects real persistent storage.
func collectFilesystems(ctx context.Context) []proto.FilesystemUsage {
	skip := map[string]bool{
		"proc": true, "sysfs": true, "cgroup": true, "cgroup2": true,
		"devtmpfs": true, "devpts": true, "mqueue": true, "hugetlbfs": true,
		"debugfs": true, "tracefs": true, "configfs": true, "fusectl": true,
		"securityfs": true, "pstore": true, "bpf": true, "binfmt_misc": true,
		"autofs": true, "rpc_pipefs": true, "nsfs": true, "ramfs": true,
		"selinuxfs": true, "tmpfs": true, "overlay": true, "squashfs": true,
	}
	f, err := os.Open(hostfs.Path("/proc/mounts"))
	if err != nil {
		return nil
	}
	defer f.Close()

	var out []proto.FilesystemUsage
	seen := make(map[string]bool)
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Fields(sc.Text())
		if len(fields) < 3 {
			continue
		}
		device, mount, fsType := fields[0], fields[1], fields[2]
		if skip[fsType] {
			continue
		}
		if seen[mount] {
			continue
		}
		seen[mount] = true
		var st syscall.Statfs_t
		if err := syscall.Statfs(hostfs.Path(mount), &st); err != nil {
			continue
		}
		bsize := uint64(st.Bsize)
		total := st.Blocks * bsize
		free := st.Bavail * bsize
		if total == 0 {
			continue
		}
		out = append(out, proto.FilesystemUsage{
			Mountpoint: mount,
			Device:     device,
			FSType:     fsType,
			TotalBytes: total,
			UsedBytes:  total - free,
			FreeBytes:  free,
			InodesUsed: st.Files - st.Ffree,
			InodesFree: st.Ffree,
		})
	}
	return out
}

// ----- Updates ----------------------------------------------------------

// collectUpdates probes apt / dnf / yum / zypper for pending updates and the
// /var/run/reboot-required marker. Honors the slow cadence (24h) by tolerating
// long timeouts on the package manager.
func collectUpdates(ctx context.Context) *proto.UpdateStatus {
	st := &proto.UpdateStatus{CheckedAt: time.Now().UTC()}
	st.RebootRequired = fileExists(hostfs.Path("/var/run/reboot-required"))

	switch {
	case binaryExists("apt"):
		st.Manager = "apt"
		// `apt list --upgradable` is the only zero-side-effect read.
		out, _ := runCmdSlow(ctx, "apt", "list", "--upgradable")
		for _, ln := range strings.Split(out, "\n") {
			ln = strings.TrimSpace(ln)
			if ln == "" || strings.HasPrefix(ln, "Listing") {
				continue
			}
			// Format: "name/repo new-version arch [upgradable from: old-version]"
			pkg, rest, ok := strings.Cut(ln, "/")
			if !ok {
				continue
			}
			parts := strings.Fields(rest)
			if len(parts) < 2 {
				continue
			}
			repo, newVer := parts[0], parts[1]
			isSec := strings.Contains(strings.ToLower(repo), "security")
			st.Pending++
			if isSec {
				st.Security++
			}
			if len(st.Updates) < 50 {
				st.Updates = append(st.Updates, proto.PendingUpdate{
					Name: pkg, NewVersion: newVer, Source: repo, Security: isSec,
				})
			}
		}
	case binaryExists("dnf"):
		st.Manager = "dnf"
		out, _ := runCmdSlow(ctx, "dnf", "-q", "check-update")
		for _, ln := range strings.Split(out, "\n") {
			parts := strings.Fields(ln)
			if len(parts) < 3 {
				continue
			}
			st.Pending++
			if len(st.Updates) < 50 {
				st.Updates = append(st.Updates, proto.PendingUpdate{
					Name: parts[0], NewVersion: parts[1], Source: parts[2],
				})
			}
		}
	case binaryExists("zypper"):
		st.Manager = "zypper"
		out, _ := runCmdSlow(ctx, "zypper", "-q", "list-updates")
		for _, ln := range strings.Split(out, "\n") {
			if !strings.HasPrefix(ln, "v |") && !strings.HasPrefix(ln, "  v |") {
				continue
			}
			st.Pending++
		}
	default:
		// No supported manager — skip silently.
		return nil
	}
	return st
}

// ----- Services ---------------------------------------------------------

// collectServices reports failed systemd units (`systemctl --failed`). Stable
// across distros that ship systemd. Hosts without systemd return empty.
func collectServices(ctx context.Context) []proto.ServiceStatus {
	if !binaryExists("systemctl") {
		return nil
	}
	out, err := runCmd(ctx, "systemctl", "--failed", "--no-legend", "--plain", "--all")
	if err != nil {
		return nil
	}
	var svcs []proto.ServiceStatus
	for _, ln := range strings.Split(out, "\n") {
		fields := strings.Fields(ln)
		if len(fields) < 4 {
			continue
		}
		svcs = append(svcs, proto.ServiceStatus{
			Name:     fields[0],
			State:    fields[2], // ACTIVE
			SubState: fields[3], // SUB
		})
	}
	return svcs
}

// ----- Security ---------------------------------------------------------

// collectSecurity assembles firewall, AV, TPM, SecureBoot, encryption, and
// SELinux/AppArmor mode. Each probe is independent and best-effort.
func collectSecurity(ctx context.Context) *proto.SecurityPosture {
	p := &proto.SecurityPosture{
		Firewall:       linuxFirewall(ctx),
		DiskEncryption: linuxLUKS(ctx),
		SecureBoot:     linuxSecureBoot(),
		SELinuxMode:    readFirstLine(hostfs.Path("/sys/fs/selinux/enforce")),
		AppArmorMode:   linuxAppArmor(),
	}
	// Translate /sys/fs/selinux/enforce ("0"/"1") to a name.
	switch p.SELinuxMode {
	case "1":
		p.SELinuxMode = "enforcing"
	case "0":
		p.SELinuxMode = "permissive"
	}
	if tpmPresent := fileExists(hostfs.Path("/sys/class/tpm/tpm0")); tpmPresent {
		t := true
		p.TPMPresent = &t
		if v := readFirstLine(hostfs.Path("/sys/class/tpm/tpm0/tpm_version_major")); v != "" {
			p.TPMVersion = v + ".0"
		}
	} else {
		f := false
		p.TPMPresent = &f
	}
	return p
}

func linuxFirewall(ctx context.Context) *proto.FirewallStatus {
	switch {
	case binaryExists("ufw"):
		out, _ := runCmd(ctx, "ufw", "status")
		fw := &proto.FirewallStatus{Provider: "ufw"}
		if strings.Contains(out, "Status: active") {
			fw.Enabled = true
		}
		return fw
	case binaryExists("firewall-cmd"):
		out, _ := runCmd(ctx, "firewall-cmd", "--state")
		fw := &proto.FirewallStatus{Provider: "firewalld"}
		if strings.TrimSpace(out) == "running" {
			fw.Enabled = true
		}
		return fw
	case binaryExists("nft"):
		out, _ := runCmd(ctx, "nft", "list", "ruleset")
		return &proto.FirewallStatus{Provider: "nftables", Enabled: strings.TrimSpace(out) != ""}
	case binaryExists("iptables"):
		out, _ := runCmd(ctx, "iptables", "-S")
		// More than just default chains means rules are loaded.
		nonDefault := 0
		for _, ln := range strings.Split(out, "\n") {
			if ln != "" && !strings.HasPrefix(ln, "-P ") {
				nonDefault++
			}
		}
		return &proto.FirewallStatus{Provider: "iptables", Enabled: nonDefault > 0}
	}
	return nil
}

func linuxLUKS(ctx context.Context) []proto.EncryptedVolume {
	if !binaryExists("lsblk") {
		return nil
	}
	out, err := runCmd(ctx, "lsblk", "-J", "-o", "NAME,FSTYPE,MOUNTPOINT,TYPE")
	if err != nil {
		return nil
	}
	var dump struct {
		Blockdevices []struct {
			Name     string `json:"name"`
			FsType   string `json:"fstype"`
			Mount    string `json:"mountpoint"`
			Children []struct {
				Name   string `json:"name"`
				FsType string `json:"fstype"`
				Mount  string `json:"mountpoint"`
			} `json:"children"`
		} `json:"blockdevices"`
	}
	if err := json.Unmarshal([]byte(out), &dump); err != nil {
		return nil
	}
	var vols []proto.EncryptedVolume
	for _, dev := range dump.Blockdevices {
		if dev.FsType == "crypto_LUKS" {
			vols = append(vols, proto.EncryptedVolume{
				Device: "/dev/" + dev.Name,
				Type:   "luks",
				Status: "on",
			})
		}
		for _, ch := range dev.Children {
			if ch.FsType == "crypto_LUKS" {
				vols = append(vols, proto.EncryptedVolume{
					Device:     "/dev/" + ch.Name,
					Mountpoint: ch.Mount,
					Type:       "luks",
					Status:     "on",
				})
			}
		}
	}
	return vols
}

func linuxSecureBoot() *bool {
	// /sys/firmware/efi/efivars/SecureBoot-* — last byte is 0 or 1.
	matches, _ := filepath.Glob(hostfs.Path("/sys/firmware/efi/efivars/SecureBoot-*"))
	if len(matches) == 0 {
		return nil
	}
	data, err := os.ReadFile(matches[0])
	if err != nil || len(data) == 0 {
		return nil
	}
	v := data[len(data)-1] == 1
	return &v
}

func linuxAppArmor() string {
	// `aa-status --enabled` exits 0 if enforcing.
	if !binaryExists("aa-status") {
		return ""
	}
	if err := exec.Command("aa-status", "--enabled").Run(); err == nil {
		return "enforce"
	}
	return ""
}

// ----- GPUs -------------------------------------------------------------

// collectGPUs uses lspci with -mm (machine-readable) for vendor/model. Driver
// version is pulled from /sys/module/{nvidia,amdgpu,i915}/version when present.
func collectGPUs(ctx context.Context) []proto.GPU {
	if !binaryExists("lspci") {
		return nil
	}
	out, err := runCmd(ctx, "lspci", "-mm", "-nn")
	if err != nil {
		return nil
	}
	var gpus []proto.GPU
	for _, ln := range strings.Split(out, "\n") {
		if !strings.Contains(ln, "VGA compatible controller") &&
			!strings.Contains(ln, "3D controller") &&
			!strings.Contains(ln, "Display controller") {
			continue
		}
		// lspci -mm format is space-separated, fields are quoted.
		fields := splitQuoted(ln)
		if len(fields) < 4 {
			continue
		}
		gpus = append(gpus, proto.GPU{
			Vendor: fields[2],
			Model:  fields[3],
		})
	}
	// Add driver versions when available.
	for i := range gpus {
		v := strings.ToLower(gpus[i].Vendor)
		switch {
		case strings.Contains(v, "nvidia"):
			gpus[i].DriverVersion = readFirstLine(hostfs.Path("/sys/module/nvidia/version"))
		case strings.Contains(v, "amd"), strings.Contains(v, "ati"):
			gpus[i].DriverVersion = readFirstLine(hostfs.Path("/sys/module/amdgpu/version"))
		case strings.Contains(v, "intel"):
			gpus[i].DriverVersion = readFirstLine(hostfs.Path("/sys/module/i915/version"))
		}
	}
	return gpus
}

// splitQuoted splits a line on whitespace while keeping "..." groups intact.
func splitQuoted(line string) []string {
	var out []string
	var cur strings.Builder
	inQuote := false
	for _, r := range line {
		switch r {
		case '"':
			inQuote = !inQuote
		case ' ', '\t':
			if inQuote {
				cur.WriteRune(r)
			} else if cur.Len() > 0 {
				out = append(out, cur.String())
				cur.Reset()
			}
		default:
			cur.WriteRune(r)
		}
	}
	if cur.Len() > 0 {
		out = append(out, cur.String())
	}
	return out
}

// ----- Chassis + battery -----------------------------------------------

// collectChassis maps DMI chassis-type to a friendly name and collects battery
// stats from /sys/class/power_supply/BAT*.
func collectChassis(ctx context.Context) *proto.ChassisInfo {
	c := &proto.ChassisInfo{Type: linuxChassisType()}
	if bat := linuxBattery(); bat != nil {
		c.Battery = bat
	}
	return c
}

func linuxChassisType() string {
	// /sys/class/dmi/id/chassis_type — SMBIOS chassis type code.
	switch readFirstLine(hostfs.Path("/sys/class/dmi/id/chassis_type")) {
	case "3", "4", "5", "6", "7", "15", "24":
		return "desktop"
	case "8", "9", "10", "11", "12", "14", "18", "21":
		return "laptop"
	case "13":
		return "all-in-one"
	case "30", "31", "32":
		return "tablet"
	case "17", "23", "25", "26", "27", "28":
		return "server"
	}
	// Fallback: device-tree presence implies SBC.
	if fileExists(hostfs.Path("/proc/device-tree/model")) {
		return "sbc"
	}
	// Virt detection from existing hardware collector would be authoritative,
	// but we can sniff /sys/class/dmi/id/sys_vendor for common hypervisors.
	v := strings.ToLower(readFirstLine(hostfs.Path("/sys/class/dmi/id/sys_vendor")))
	if strings.Contains(v, "qemu") || strings.Contains(v, "vmware") ||
		strings.Contains(v, "innotek") || strings.Contains(v, "microsoft") {
		return "vm"
	}
	return ""
}

func linuxBattery() *proto.Battery {
	matches, _ := filepath.Glob(hostfs.Path("/sys/class/power_supply/BAT*"))
	if len(matches) == 0 {
		return nil
	}
	dir := matches[0]
	read := func(name string) string { return readFirstLine(filepath.Join(dir, name)) }
	parseUWh := func(s string) float64 {
		v, _ := strconv.ParseFloat(strings.TrimSpace(s), 64)
		return v / 1_000_000.0 // µWh -> Wh
	}
	bat := &proto.Battery{State: strings.ToLower(read("status"))}
	bat.DesignCapacityWh = parseUWh(read("energy_full_design"))
	bat.CurrentCapacityWh = parseUWh(read("energy_full"))
	if bat.DesignCapacityWh > 0 {
		bat.HealthPercent = bat.CurrentCapacityWh / bat.DesignCapacityWh * 100
	}
	if v, err := strconv.Atoi(strings.TrimSpace(read("cycle_count"))); err == nil {
		bat.CycleCount = v
	}
	if v, err := strconv.ParseFloat(strings.TrimSpace(read("capacity")), 64); err == nil {
		bat.ChargePercent = v
	}
	return bat
}

// ----- Local users ------------------------------------------------------

// collectLocalUsers reads /etc/passwd and cross-references /etc/shadow (if
// readable) for password age and disabled state. Admin = membership in sudo
// or wheel group from /etc/group.
func collectLocalUsers(ctx context.Context) []proto.LocalUser {
	pwf, err := os.Open(hostfs.Path("/etc/passwd"))
	if err != nil {
		return nil
	}
	defer pwf.Close()

	admins := linuxAdminMembers()
	var users []proto.LocalUser
	sc := bufio.NewScanner(pwf)
	for sc.Scan() {
		f := strings.Split(sc.Text(), ":")
		if len(f) < 7 {
			continue
		}
		// Skip system accounts (UID < 1000) except root, to keep payload manageable.
		uid, _ := strconv.Atoi(f[2])
		if uid != 0 && uid < 1000 {
			continue
		}
		u := proto.LocalUser{
			Name:    f[0],
			UID:     f[2],
			GID:     f[3],
			HomeDir: f[5],
			Shell:   f[6],
			IsAdmin: admins[f[0]],
		}
		users = append(users, u)
	}
	return users
}

func linuxAdminMembers() map[string]bool {
	out := make(map[string]bool)
	f, err := os.Open(hostfs.Path("/etc/group"))
	if err != nil {
		return out
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Split(sc.Text(), ":")
		if len(fields) < 4 {
			continue
		}
		if fields[0] != "sudo" && fields[0] != "wheel" && fields[0] != "admin" {
			continue
		}
		for _, m := range strings.Split(fields[3], ",") {
			if m = strings.TrimSpace(m); m != "" {
				out[m] = true
			}
		}
	}
	return out
}

// ----- SMART ------------------------------------------------------------

// enrichDiskSMART runs `smartctl -j -a <dev>` for each disk and copies the
// curated fields into d.SMART. Silently no-ops if smartctl isn't installed
// or the agent lacks privileges.
func enrichDiskSMART(ctx context.Context, disks []proto.DiskDevice) {
	if !binaryExists("smartctl") {
		return
	}
	for i := range disks {
		dev := "/dev/" + disks[i].Name
		out, err := runCmdSlow(ctx, "smartctl", "-j", "-a", dev)
		if err != nil && out == "" {
			continue
		}
		var s smartctlOut
		if err := json.Unmarshal([]byte(out), &s); err != nil {
			continue
		}
		h := &proto.SMARTHealth{
			TemperatureCelsius: float64(s.Temperature.Current),
			PowerOnHours:       s.PowerOnTime.Hours,
			PowerCycleCount:    s.PowerCycleCount,
		}
		if s.SmartStatus.Passed {
			h.OverallHealth = "PASSED"
		} else {
			h.OverallHealth = "FAILED"
		}
		// NVMe-specific fields.
		if s.NVMeLog.PercentageUsed > 0 || s.NVMeLog.AvailableSpare > 0 {
			h.PercentageUsed = float64(s.NVMeLog.PercentageUsed)
			h.AvailableSparePct = float64(s.NVMeLog.AvailableSpare)
			// Data units are 512KB per smartctl; convert to GB.
			h.DataUnitsReadGB = float64(s.NVMeLog.DataUnitsRead) * 512 * 1000 / 1e9
			h.DataUnitsWrittenGB = float64(s.NVMeLog.DataUnitsWritten) * 512 * 1000 / 1e9
		}
		// SATA-specific attributes.
		for _, a := range s.AtaSmart.Table {
			switch a.ID {
			case 5:
				h.ReallocatedSectors = a.Raw.Value
			case 197:
				h.PendingSectors = a.Raw.Value
			case 198:
				h.UncorrectableErrors = a.Raw.Value
			case 231, 233: // SSD wear-leveling / life remaining
				if a.Value > 0 && a.Value <= 100 {
					h.MediaWearoutPercent = float64(100 - a.Value)
				}
			}
		}
		disks[i].SMART = h
	}
}

type smartctlOut struct {
	SmartStatus struct {
		Passed bool `json:"passed"`
	} `json:"smart_status"`
	Temperature struct {
		Current int `json:"current"`
	} `json:"temperature"`
	PowerOnTime struct {
		Hours uint64 `json:"hours"`
	} `json:"power_on_time"`
	PowerCycleCount uint64 `json:"power_cycle_count"`
	AtaSmart        struct {
		Table []struct {
			ID    int    `json:"id"`
			Name  string `json:"name"`
			Value int    `json:"value"`
			Raw   struct {
				Value uint64 `json:"value"`
			} `json:"raw"`
		} `json:"table"`
	} `json:"ata_smart_attributes"`
	NVMeLog struct {
		PercentageUsed   int    `json:"percentage_used"`
		AvailableSpare   int    `json:"available_spare"`
		DataUnitsRead    uint64 `json:"data_units_read"`
		DataUnitsWritten uint64 `json:"data_units_written"`
	} `json:"nvme_smart_health_information_log"`
}

// ----- shared helpers ---------------------------------------------------

func binaryExists(name string) bool {
	_, err := exec.LookPath(name)
	return err == nil
}

func fileExists(p string) bool {
	_, err := os.Stat(p)
	return err == nil
}

// runCmdSlow is for inventory-tier package-manager calls that can legitimately
// take 30+ seconds (apt list, dnf check-update). Separate from runCmd to make
// the cost obvious at the call site.
func runCmdSlow(ctx context.Context, name string, args ...string) (string, error) {
	c, cancel := context.WithTimeout(ctx, 60*time.Second)
	defer cancel()
	out, err := exec.CommandContext(c, name, args...).CombinedOutput()
	return string(out), err
}
