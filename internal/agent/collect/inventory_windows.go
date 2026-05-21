//go:build windows

// Inventory collectors for Windows. Most facts come from PowerShell
// shell-outs to Get-CimInstance / Get-ItemProperty rendered as compact JSON.
// PowerShell startup is ~150–250ms each; we run on a 24h cadence so the cost
// is negligible. Each helper is fail-soft: if PowerShell or the query fails,
// it returns the empty value and the field stays omitted from the inventory.
package collect

import (
	"context"
	"encoding/json"
	"os"
	"os/exec"
	"reflect"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// runPS runs a PowerShell command with no profile / non-interactive and
// returns trimmed stdout. The hidden-window flag prevents a console flash
// when the agent runs as a session-attached scheduled task during dev.
func runPS(ctx context.Context, script string) (string, error) {
	cctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	cmd := exec.CommandContext(cctx, "powershell.exe",
		"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
		"-Command", script,
	)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	out, err := cmd.Output()
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(out)), nil
}

// runPSJSON runs a PowerShell snippet that already pipes through
// ConvertTo-Json -Compress and unmarshals into out. Handles the
// single-result quirk where ConvertTo-Json emits an object instead of a
// 1-element array: when out is a slice/array pointer and the payload
// starts with `{`, we wrap it as `[...]` before unmarshalling.
func runPSJSON(ctx context.Context, script string, out any) error {
	s, err := runPS(ctx, script)
	if err != nil {
		return err
	}
	if s == "" {
		return nil
	}
	if len(s) > 0 && s[0] == '{' {
		if rv := reflect.ValueOf(out); rv.Kind() == reflect.Ptr {
			switch rv.Elem().Kind() {
			case reflect.Slice, reflect.Array:
				s = "[" + s + "]"
			}
		}
	}
	return json.Unmarshal([]byte(s), out)
}

// ----- hardware -----

func collectHardware(ctx context.Context) proto.HardwareInfo {
	hw := proto.HardwareInfo{}
	hw.CPULogicalCores = numCPU()

	// One round-trip for system + bios + board + cpu + mem facts. Wrapping
	// each Get-CimInstance in @() forces an array even for a single result,
	// so the JSON shape is stable.
	var data struct {
		System struct {
			Manufacturer        string `json:"Manufacturer"`
			Model               string `json:"Model"`
			TotalPhysicalMemory uint64 `json:"TotalPhysicalMemory"`
		} `json:"System"`
		BIOS struct {
			Manufacturer string `json:"Manufacturer"`
			Version      string `json:"SMBIOSBIOSVersion"`
			ReleaseDate  string `json:"ReleaseDate"`
			SerialNumber string `json:"SerialNumber"`
		} `json:"BIOS"`
		Board struct {
			Manufacturer string `json:"Manufacturer"`
			Product      string `json:"Product"`
		} `json:"Board"`
		CPU struct {
			Name          string `json:"Name"`
			Manufacturer  string `json:"Manufacturer"`
			NumberOfCores int    `json:"NumberOfCores"`
			MaxClockSpeed int    `json:"MaxClockSpeed"`
		} `json:"CPU"`
	}
	script := `$o = [ordered]@{
		System = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, TotalPhysicalMemory
		BIOS   = Get-CimInstance Win32_BIOS | Select-Object Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber
		Board  = Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product
		CPU    = Get-CimInstance Win32_Processor | Select-Object -First 1 Name, Manufacturer, NumberOfCores, MaxClockSpeed
	}
	$o | ConvertTo-Json -Compress -Depth 4`
	if err := runPSJSON(ctx, script, &data); err == nil {
		hw.SystemVendor = data.System.Manufacturer
		hw.SystemModel = data.System.Model
		hw.SystemSerial = data.BIOS.SerialNumber
		hw.BoardVendor = data.Board.Manufacturer
		hw.BoardModel = data.Board.Product
		hw.BIOSVendor = data.BIOS.Manufacturer
		hw.BIOSVersion = data.BIOS.Version
		// CIM ReleaseDate is "/Date(1234567890000)/" — ignore for now; raw
		// string would mislead.
		hw.CPUModel = strings.TrimSpace(data.CPU.Name)
		hw.CPUVendor = data.CPU.Manufacturer
		hw.CPUCores = data.CPU.NumberOfCores
		hw.CPUMHz = float64(data.CPU.MaxClockSpeed)
		if data.System.TotalPhysicalMemory > 0 {
			hw.TotalMemBytes = data.System.TotalPhysicalMemory
		}
	}

	// Fall back to GlobalMemoryStatusEx if WMI didn't report total memory.
	if hw.TotalMemBytes == 0 {
		if _, total, err := memInfo(); err == nil {
			hw.TotalMemBytes = total
		}
	}

	hw.Virtualization = detectVirtWindows(ctx)
	return hw
}

// detectVirtWindows asks Win32_ComputerSystem for the Model field; Hyper-V
// returns "Virtual Machine", VMware returns "VMware Virtual Platform", etc.
// Returns "none" when nothing matches a known hypervisor signature.
func detectVirtWindows(ctx context.Context) string {
	out, err := runPS(ctx,
		`(Get-CimInstance Win32_ComputerSystem).Model`)
	if err != nil {
		return ""
	}
	model := strings.ToLower(strings.TrimSpace(out))
	switch {
	case strings.Contains(model, "vmware"):
		return "vmware"
	case strings.Contains(model, "virtualbox"):
		return "virtualbox"
	case strings.Contains(model, "hyperv"), strings.Contains(model, "virtual machine"):
		return "hyperv"
	case strings.Contains(model, "kvm"), strings.Contains(model, "qemu"):
		return "kvm"
	case strings.Contains(model, "xen"):
		return "xen"
	case model == "":
		return ""
	}
	return "none"
}

// ----- OS -----

func collectOS(ctx context.Context) proto.OSInfo {
	o := proto.OSInfo{Family: "windows"}
	o.Hostname, _ = os.Hostname()
	o.Timezone, _ = time.Now().Zone()
	o.KernelArch = runtime.GOARCH

	var data struct {
		Caption        string `json:"Caption"`
		Version        string `json:"Version"`
		BuildNumber    string `json:"BuildNumber"`
		InstallDate    string `json:"InstallDate"`
		LastBootUpTime string `json:"LastBootUpTime"`
		OSArchitecture string `json:"OSArchitecture"`
	}
	script := `Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, ` +
		`@{n='InstallDate';e={$_.InstallDate.ToString('o')}}, ` +
		`@{n='LastBootUpTime';e={$_.LastBootUpTime.ToString('o')}}, ` +
		`OSArchitecture | ConvertTo-Json -Compress`
	if err := runPSJSON(ctx, script, &data); err == nil {
		o.Distro = strings.TrimSpace(data.Caption)
		o.Version = data.Version
		o.Build = data.BuildNumber
		// Kernel on Windows = NT version (e.g. "10.0.22631"); same as Version.
		o.Kernel = data.Version
		if t, err := time.Parse(time.RFC3339, data.InstallDate); err == nil {
			o.InstallDate = t.UTC()
		}
		if t, err := time.Parse(time.RFC3339, data.LastBootUpTime); err == nil {
			o.BootTime = t.UTC()
		}
	}
	return o
}

// ----- disks -----

func collectDisks(ctx context.Context) []proto.DiskDevice {
	var rows []struct {
		FriendlyName string `json:"FriendlyName"`
		SerialNumber string `json:"SerialNumber"`
		Size         uint64 `json:"Size"`
		MediaType    string `json:"MediaType"`
		BusType      string `json:"BusType"`
		DeviceID     string `json:"DeviceId"`
	}
	script := `Get-PhysicalDisk | Select-Object FriendlyName, SerialNumber, Size, MediaType, BusType, DeviceId | ConvertTo-Json -Compress`
	if err := runPSJSON(ctx, script, &rows); err != nil {
		// Get-PhysicalDisk requires the Storage module which is present on
		// modern Windows but missing on Windows Server Core minimal SKUs.
		// Fall back to Win32_DiskDrive which is universal but lacks SSD type.
		return diskFallback(ctx)
	}
	out := make([]proto.DiskDevice, 0, len(rows))
	for _, r := range rows {
		dt := strings.ToLower(r.MediaType)
		switch dt {
		case "ssd":
			dt = "ssd"
		case "hdd":
			dt = "hdd"
		case "scm":
			dt = "nvme"
		default:
			if strings.EqualFold(r.BusType, "NVMe") {
				dt = "nvme"
			} else if dt == "" || dt == "unspecified" {
				dt = "unknown"
			}
		}
		out = append(out, proto.DiskDevice{
			Name:      r.DeviceID,
			Model:     strings.TrimSpace(r.FriendlyName),
			Serial:    strings.TrimSpace(r.SerialNumber),
			SizeBytes: r.Size,
			Type:      dt,
		})
	}
	return out
}

func diskFallback(ctx context.Context) []proto.DiskDevice {
	var rows []struct {
		Caption      string `json:"Caption"`
		SerialNumber string `json:"SerialNumber"`
		Size         uint64 `json:"Size"`
		Index        int    `json:"Index"`
	}
	if err := runPSJSON(ctx,
		`Get-CimInstance Win32_DiskDrive | Select-Object Caption, SerialNumber, Size, Index | ConvertTo-Json -Compress`,
		&rows); err != nil {
		return nil
	}
	out := make([]proto.DiskDevice, 0, len(rows))
	for _, r := range rows {
		out = append(out, proto.DiskDevice{
			Name:      "PhysicalDrive" + strconv.Itoa(r.Index),
			Model:     strings.TrimSpace(r.Caption),
			Serial:    strings.TrimSpace(r.SerialNumber),
			SizeBytes: r.Size,
			Type:      "unknown",
		})
	}
	return out
}

// ----- routes -----

func collectRoutes(ctx context.Context) []proto.RouteEntry {
	var rows []struct {
		AddressFamily     string `json:"AddressFamily"`
		DestinationPrefix string `json:"DestinationPrefix"`
		NextHop           string `json:"NextHop"`
		InterfaceAlias    string `json:"InterfaceAlias"`
		RouteMetric       int    `json:"RouteMetric"`
	}
	script := `Get-NetRoute -ErrorAction SilentlyContinue | Select-Object @{n='AddressFamily';e={$_.AddressFamily.ToString()}}, DestinationPrefix, NextHop, InterfaceAlias, RouteMetric | ConvertTo-Json -Compress`
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.RouteEntry, 0, len(rows))
	for _, r := range rows {
		fam := "ipv4"
		if strings.Contains(strings.ToLower(r.AddressFamily), "v6") {
			fam = "ipv6"
		}
		out = append(out, proto.RouteEntry{
			Destination: r.DestinationPrefix,
			Gateway:     r.NextHop,
			Iface:       r.InterfaceAlias,
			Family:      fam,
			Metric:      r.RouteMetric,
		})
	}
	return out
}

// ----- users -----

func collectUsers(ctx context.Context) []proto.UserSession {
	// `query user` is the most reliable cross-SKU way; quser.exe is the same
	// binary. Output is a fixed-width table — parse defensively.
	out, err := runPS(ctx, `try { (quser 2>&1) -join "`+"`"+`n" } catch { '' }`)
	if err != nil || out == "" {
		return nil
	}
	var users []proto.UserSession
	lines := strings.Split(out, "\n")
	if len(lines) < 2 {
		return nil
	}
	for _, line := range lines[1:] {
		fields := strings.Fields(strings.TrimPrefix(strings.TrimRight(line, "\r"), ">"))
		if len(fields) < 2 {
			continue
		}
		// quser format (variable column count when SESSIONNAME is empty):
		// USERNAME [SESSIONNAME] ID STATE IDLE-TIME LOGON-TIME
		users = append(users, proto.UserSession{
			User: fields[0],
			TTY:  fields[1],
		})
	}
	return users
}

// ----- listening ports -----

func collectListening(ctx context.Context) []proto.ListeningPort {
	var rows []struct {
		LocalAddress  string `json:"LocalAddress"`
		LocalPort     int    `json:"LocalPort"`
		OwningProcess int    `json:"OwningProcess"`
	}
	script := `Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Select-Object LocalAddress, LocalPort, OwningProcess | ConvertTo-Json -Compress`
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.ListeningPort, 0, len(rows))
	for _, r := range rows {
		out = append(out, proto.ListeningPort{
			Proto:   "tcp",
			Address: r.LocalAddress,
			Port:    r.LocalPort,
			PID:     r.OwningProcess,
		})
	}
	return out
}

// ----- processes -----

func collectProcesses(ctx context.Context) []proto.ProcessSummary {
	var rows []struct {
		Id   int     `json:"Id"`
		Name string  `json:"ProcessName"`
		WS   uint64  `json:"WorkingSet64"`
		CPU  float64 `json:"CPU"`
	}
	// Top 25 by working set; CPU column is total seconds since process start
	// (PerformanceCounter would be needed for instantaneous %).
	script := `Get-Process | Sort-Object -Property WorkingSet64 -Descending | Select-Object -First 25 Id, ProcessName, WorkingSet64, CPU | ConvertTo-Json -Compress`
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.ProcessSummary, 0, len(rows))
	for _, r := range rows {
		out = append(out, proto.ProcessSummary{
			PID:      r.Id,
			Name:     r.Name,
			MemBytes: r.WS,
			// CPU column from Get-Process is total CPU-seconds since process
			// start, not %; leaving CPUPercent zero to avoid misleading values.
		})
	}
	return out
}

// ----- reboot history -----

func collectReboots(ctx context.Context) []proto.BootRecord {
	// Event 6005 = "Event Log service started" (a reliable boot marker
	// across desktop and server SKUs). 12 entries = ~last few months.
	script := `Get-WinEvent -FilterHashtable @{LogName='System'; Id=6005} -MaxEvents 12 -ErrorAction SilentlyContinue | Select-Object @{n='TimeCreated';e={$_.TimeCreated.ToString('o')}} | ConvertTo-Json -Compress`
	var rows []struct {
		TimeCreated string `json:"TimeCreated"`
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.BootRecord, 0, len(rows))
	for _, r := range rows {
		if t, err := time.Parse(time.RFC3339, r.TimeCreated); err == nil {
			out = append(out, proto.BootRecord{BootedAt: t.UTC()})
		}
	}
	return out
}

// ----- packages -----

func collectPackages(ctx context.Context) *proto.PackageInventory {
	// Walks both 64-bit and 32-bit Uninstall registry keys. PowerShell here
	// is much more reliable than golang.org/x/sys/windows/registry which
	// requires hand-walking the same paths.
	script := `$paths = @(
		'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
		'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
	)
	Get-ItemProperty $paths -ErrorAction SilentlyContinue |
		Where-Object { $_.DisplayName } |
		Select-Object @{n='Name';e={$_.DisplayName}}, @{n='Version';e={$_.DisplayVersion}} |
		ConvertTo-Json -Compress`
	var rows []struct {
		Name    string `json:"Name"`
		Version string `json:"Version"`
	}
	if err := runPSJSON(ctx, script, &rows); err != nil || len(rows) == 0 {
		return nil
	}
	pkgs := make([]proto.Package, 0, len(rows))
	for _, r := range rows {
		pkgs = append(pkgs, proto.Package{Name: r.Name, Version: r.Version})
	}
	return &proto.PackageInventory{
		Manager:  "windows",
		Count:    len(pkgs),
		Packages: pkgs,
	}
}

// ----- network helpers required by inventory.go's collectNetwork -----

// ifaceEnrich on Windows: net.Interfaces() already populates name + MAC + MTU
// + flags from the OS. There's no extra static config worth adding without
// a Get-NetAdapter shell-out per interface — defer until we need it.
func ifaceEnrich(name string, ni *proto.NetInterface) { _ = name; _ = ni }

func defaultGateways(ctx context.Context) (string, string) {
	out, err := runPS(ctx,
		`(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | `+
			`Sort-Object -Property RouteMetric | Select-Object -First 1).NextHop`)
	if err != nil {
		out = ""
	}
	v4 := strings.TrimSpace(out)
	out6, err := runPS(ctx,
		`(Get-NetRoute -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | `+
			`Sort-Object -Property RouteMetric | Select-Object -First 1).NextHop`)
	if err != nil {
		out6 = ""
	}
	return v4, strings.TrimSpace(out6)
}

// numCPU on Windows just delegates to the runtime; matches Linux's helper
// of the same name.
func numCPU() int { return runtime.NumCPU() }
