//go:build darwin

package collect

import (
	"context"
	"encoding/json"
	"os/exec"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// ----- Filesystems ------------------------------------------------------

func collectFilesystems(ctx context.Context) []proto.FilesystemUsage {
	out, err := runCmd(ctx, "df", "-Pk")
	if err != nil {
		return nil
	}
	var fs []proto.FilesystemUsage
	skipFS := map[string]bool{"devfs": true, "autofs": true, "map": true}
	for i, ln := range strings.Split(out, "\n") {
		if i == 0 || strings.TrimSpace(ln) == "" {
			continue
		}
		f := strings.Fields(ln)
		if len(f) < 6 {
			continue
		}
		mount := f[5]
		// statfs gives us fstype + inode counts that df -P doesn't.
		var st syscall.Statfs_t
		if err := syscall.Statfs(mount, &st); err != nil {
			continue
		}
		fsType := cstrFromInt8(st.Fstypename[:])
		if skipFS[fsType] {
			continue
		}
		bsize := uint64(st.Bsize)
		total := st.Blocks * bsize
		free := st.Bavail * bsize
		if total == 0 {
			continue
		}
		fs = append(fs, proto.FilesystemUsage{
			Mountpoint: mount,
			Device:     f[0],
			FSType:     fsType,
			TotalBytes: total,
			UsedBytes:  total - free,
			FreeBytes:  free,
			InodesUsed: st.Files - st.Ffree,
			InodesFree: st.Ffree,
		})
	}
	return fs
}

func cstrFromInt8(b []int8) string {
	bs := make([]byte, 0, len(b))
	for _, c := range b {
		if c == 0 {
			break
		}
		bs = append(bs, byte(c))
	}
	return string(bs)
}

// ----- Updates ----------------------------------------------------------

// collectUpdates uses softwareupdate, which is slow (often 30-60s). The wrapper
// already runs at the inventory cadence so this is acceptable.
func collectUpdates(ctx context.Context) *proto.UpdateStatus {
	st := &proto.UpdateStatus{Manager: "softwareupdate", CheckedAt: time.Now().UTC()}
	out, err := runCmdSlowDarwin(ctx, "softwareupdate", "-l")
	if err != nil && out == "" {
		return nil
	}
	for _, ln := range strings.Split(out, "\n") {
		ln = strings.TrimSpace(ln)
		if !strings.HasPrefix(ln, "* Label:") {
			continue
		}
		st.Pending++
		if len(st.Updates) < 50 {
			st.Updates = append(st.Updates, proto.PendingUpdate{
				Name:   strings.TrimSpace(strings.TrimPrefix(ln, "* Label:")),
				Source: "softwareupdate",
			})
		}
	}
	// "restart" appears in the title of updates that need reboot.
	st.RebootRequired = strings.Contains(strings.ToLower(out), "restart")
	return st
}

// ----- Services ---------------------------------------------------------

// collectServices flags launchd jobs whose last exit status was non-zero.
// `launchctl list` columns: PID Status Label.
func collectServices(ctx context.Context) []proto.ServiceStatus {
	out, err := runCmd(ctx, "launchctl", "list")
	if err != nil {
		return nil
	}
	var svcs []proto.ServiceStatus
	for i, ln := range strings.Split(out, "\n") {
		if i == 0 || strings.TrimSpace(ln) == "" {
			continue
		}
		f := strings.Fields(ln)
		if len(f) < 3 {
			continue
		}
		// Only report failed (non-zero last exit) units; "-" status = never run / running.
		if f[1] == "-" || f[1] == "0" {
			continue
		}
		exit, _ := strconv.Atoi(f[1])
		svcs = append(svcs, proto.ServiceStatus{
			Name:     f[2],
			State:    "failed",
			ExitCode: exit,
		})
	}
	return svcs
}

// ----- Security ---------------------------------------------------------

func collectSecurity(ctx context.Context) *proto.SecurityPosture {
	p := &proto.SecurityPosture{
		Firewall:       darwinFirewall(ctx),
		DiskEncryption: darwinFileVault(ctx),
	}
	if sip := darwinSIP(ctx); sip != "" {
		// SIP isn't exactly SELinux but it's the closest cross-platform analog;
		// surface it via SELinuxMode so dashboards can display "OS hardening".
		// (Renaming the proto field would be a breaking change.)
		p.SELinuxMode = "sip:" + sip
	}
	return p
}

func darwinFirewall(ctx context.Context) *proto.FirewallStatus {
	out, err := runCmd(ctx, "/usr/libexec/ApplicationFirewall/socketfilterfw", "--getglobalstate")
	if err != nil {
		return nil
	}
	return &proto.FirewallStatus{
		Provider: "alf",
		Enabled:  strings.Contains(out, "enabled"),
	}
}

func darwinFileVault(ctx context.Context) []proto.EncryptedVolume {
	out, err := runCmd(ctx, "fdesetup", "status")
	if err != nil {
		return nil
	}
	status := "off"
	if strings.Contains(out, "FileVault is On") {
		status = "on"
	}
	return []proto.EncryptedVolume{{
		Mountpoint: "/",
		Type:       "filevault",
		Status:     status,
	}}
}

func darwinSIP(ctx context.Context) string {
	out, err := runCmd(ctx, "csrutil", "status")
	if err != nil {
		return ""
	}
	if strings.Contains(out, "enabled") {
		return "enabled"
	}
	if strings.Contains(out, "disabled") {
		return "disabled"
	}
	return ""
}

// ----- GPUs -------------------------------------------------------------

func collectGPUs(ctx context.Context) []proto.GPU {
	out, err := runCmd(ctx, "system_profiler", "-json", "SPDisplaysDataType")
	if err != nil {
		return nil
	}
	var dump struct {
		Displays []struct {
			Model    string `json:"sppci_model"`
			Vendor   string `json:"spdisplays_vendor"`
			Cores    string `json:"sppci_cores"`
			Driver   string `json:"spdisplays_metalfamily"`
			VRAM     string `json:"spdisplays_vram"`
			VRAMShared string `json:"spdisplays_vram_shared"`
		} `json:"SPDisplaysDataType"`
	}
	if err := json.Unmarshal([]byte(out), &dump); err != nil {
		return nil
	}
	gpus := make([]proto.GPU, 0, len(dump.Displays))
	for _, d := range dump.Displays {
		vram := d.VRAM
		if vram == "" {
			vram = d.VRAMShared
		}
		// `system_profiler` returns vendor as a localization key like
		// "sppci_vendor_Apple"; strip the prefix for display.
		vendor := strings.TrimPrefix(d.Vendor, "sppci_vendor_")
		gpus = append(gpus, proto.GPU{
			Vendor:        vendor,
			Model:         d.Model,
			DriverVersion: d.Driver,
			VRAMBytes:     parseHumanSize(vram),
		})
	}
	return gpus
}

// ----- Chassis + battery -----------------------------------------------

func collectChassis(ctx context.Context) *proto.ChassisInfo {
	c := &proto.ChassisInfo{Type: darwinChassisType(ctx)}
	if bat := darwinBattery(ctx); bat != nil {
		c.Battery = bat
	}
	return c
}

func darwinChassisType(ctx context.Context) string {
	// Heuristic: presence of a battery → laptop, else desktop. Mac Pro is server-y
	// but we don't try to distinguish.
	if darwinHasBattery(ctx) {
		return "laptop"
	}
	model, _ := runCmd(ctx, "sysctl", "-n", "hw.model")
	m := strings.ToLower(strings.TrimSpace(model))
	switch {
	case strings.HasPrefix(m, "macbook"):
		return "laptop"
	case strings.HasPrefix(m, "macpro"):
		return "server"
	case strings.HasPrefix(m, "macmini"), strings.HasPrefix(m, "imac"), strings.HasPrefix(m, "mac"):
		return "desktop"
	}
	return ""
}

func darwinHasBattery(ctx context.Context) bool {
	out, err := runCmd(ctx, "pmset", "-g", "batt")
	return err == nil && strings.Contains(out, "InternalBattery")
}

func darwinBattery(ctx context.Context) *proto.Battery {
	if !darwinHasBattery(ctx) {
		return nil
	}
	out, err := runCmd(ctx, "system_profiler", "-json", "SPPowerDataType")
	if err != nil {
		return nil
	}
	var dump struct {
		Power []struct {
			BatteryInfo struct {
				CycleCount    int    `json:"sppower_battery_cycle_count"`
				Charging      string `json:"sppower_battery_is_charging"`
				StateOfCharge int    `json:"sppower_battery_state_of_charge"`
			} `json:"sppower_battery_charge_info"`
			HealthInfo struct {
				CycleCount int    `json:"sppower_battery_cycle_count"`
				Condition  string `json:"sppower_battery_health"`
				MaxCap     string `json:"sppower_battery_health_maximum_capacity"` // "83%"
			} `json:"sppower_battery_health_info"`
		} `json:"SPPowerDataType"`
	}
	if err := json.Unmarshal([]byte(out), &dump); err != nil {
		return nil
	}
	// The Power slice can include non-battery entries; scan for the one that
	// has a charge_info block populated.
	for _, p := range dump.Power {
		if p.BatteryInfo.StateOfCharge == 0 && p.HealthInfo.MaxCap == "" && p.BatteryInfo.CycleCount == 0 {
			continue
		}
		bat := &proto.Battery{
			CycleCount:    p.HealthInfo.CycleCount,
			ChargePercent: float64(p.BatteryInfo.StateOfCharge),
		}
		if bat.CycleCount == 0 {
			bat.CycleCount = p.BatteryInfo.CycleCount
		}
		if cap := strings.TrimSuffix(p.HealthInfo.MaxCap, "%"); cap != "" {
			if v, err := strconv.ParseFloat(cap, 64); err == nil {
				bat.HealthPercent = v
			}
		}
		if strings.EqualFold(p.BatteryInfo.Charging, "true") {
			bat.State = "charging"
		} else {
			bat.State = "discharging"
		}
		return bat
	}
	return nil
}

// ----- Local users ------------------------------------------------------

func collectLocalUsers(ctx context.Context) []proto.LocalUser {
	// `dscl . list /Users` gives all accounts; filter to those with UID >= 500
	// (regular accounts) plus root, then enrich via `dscl . -read /Users/X`.
	out, err := runCmd(ctx, "dscl", ".", "-list", "/Users", "UniqueID")
	if err != nil {
		return nil
	}
	admins := darwinAdminMembers(ctx)
	var users []proto.LocalUser
	for _, ln := range strings.Split(out, "\n") {
		f := strings.Fields(ln)
		if len(f) < 2 {
			continue
		}
		name, uid := f[0], f[1]
		uidNum, _ := strconv.Atoi(uid)
		if uidNum != 0 && uidNum < 500 {
			continue
		}
		if strings.HasPrefix(name, "_") {
			continue
		}
		u := proto.LocalUser{
			Name:    name,
			UID:     uid,
			IsAdmin: admins[name],
		}
		if shell, err := runCmd(ctx, "dscl", ".", "-read", "/Users/"+name, "UserShell"); err == nil {
			u.Shell = firstField(strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(shell), "UserShell:")))
		}
		if home, err := runCmd(ctx, "dscl", ".", "-read", "/Users/"+name, "NFSHomeDirectory"); err == nil {
			u.HomeDir = firstField(strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(home), "NFSHomeDirectory:")))
		}
		users = append(users, u)
	}
	return users
}

// firstField returns the first whitespace-delimited token in s, used to clean
// up `dscl` outputs that occasionally return "primary alt" pairs.
func firstField(s string) string {
	for _, f := range strings.Fields(s) {
		return f
	}
	return ""
}

func darwinAdminMembers(ctx context.Context) map[string]bool {
	out := make(map[string]bool)
	r, err := runCmd(ctx, "dscl", ".", "-read", "/Groups/admin", "GroupMembership")
	if err != nil {
		return out
	}
	r = strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(r), "GroupMembership:"))
	for _, m := range strings.Fields(r) {
		out[m] = true
	}
	return out
}

// ----- SMART ------------------------------------------------------------

// enrichDiskSMART uses `smartctl -j -a` if installed; macOS doesn't ship it
// by default, but it's commonly present from Homebrew.
func enrichDiskSMART(ctx context.Context, disks []proto.DiskDevice) {
	if _, err := exec.LookPath("smartctl"); err != nil {
		return
	}
	for i := range disks {
		dev := "/dev/" + disks[i].Name
		out, err := runCmdSlowDarwin(ctx, "smartctl", "-j", "-a", dev)
		if err != nil && out == "" {
			continue
		}
		var s struct {
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
			NVMeLog         struct {
				PercentageUsed int `json:"percentage_used"`
				AvailableSpare int `json:"available_spare"`
			} `json:"nvme_smart_health_information_log"`
		}
		if err := json.Unmarshal([]byte(out), &s); err != nil {
			continue
		}
		h := &proto.SMARTHealth{
			TemperatureCelsius: float64(s.Temperature.Current),
			PowerOnHours:       s.PowerOnTime.Hours,
			PowerCycleCount:    s.PowerCycleCount,
			PercentageUsed:     float64(s.NVMeLog.PercentageUsed),
			AvailableSparePct:  float64(s.NVMeLog.AvailableSpare),
		}
		if s.SmartStatus.Passed {
			h.OverallHealth = "PASSED"
		} else {
			h.OverallHealth = "FAILED"
		}
		disks[i].SMART = h
	}
}

// runCmdSlowDarwin: longer-budget version of runCmd for the few collectors
// (softwareupdate, smartctl) that legitimately can take a minute.
func runCmdSlowDarwin(ctx context.Context, name string, args ...string) (string, error) {
	c, cancel := context.WithTimeout(ctx, 90*time.Second)
	defer cancel()
	out, err := exec.CommandContext(c, name, args...).CombinedOutput()
	return string(out), err
}
