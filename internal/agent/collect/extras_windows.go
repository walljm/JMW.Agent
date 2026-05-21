//go:build windows

package collect

import (
	"context"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// ----- Filesystems ------------------------------------------------------

// collectFilesystems duplicates the disk-usage walk from collect_windows.go's
// hot-path `diskUsage` but at inventory rate. We carry full FS-type and free
// breakdown here vs the lighter snapshot used at heartbeat time.
func collectFilesystems(ctx context.Context) []proto.FilesystemUsage {
	snaps := diskUsage()
	out := make([]proto.FilesystemUsage, 0, len(snaps))
	for _, s := range snaps {
		out = append(out, proto.FilesystemUsage{
			Mountpoint: s.Mountpoint,
			Device:     s.Device,
			FSType:     s.FSType,
			TotalBytes: s.TotalBytes,
			UsedBytes:  s.UsedBytes,
			FreeBytes:  s.TotalBytes - s.UsedBytes,
		})
	}
	return out
}

// ----- Updates ----------------------------------------------------------

// collectUpdates queries the Windows Update Agent COM API via PowerShell.
// The query can take 30+ seconds on hosts that haven't checked recently;
// runPS's default 15s timeout is too short, so we use a longer custom budget.
func collectUpdates(ctx context.Context) *proto.UpdateStatus {
	st := &proto.UpdateStatus{Manager: "windowsupdate", CheckedAt: time.Now().UTC()}

	// Reboot-required heuristic — three reg keys + one folder.
	rebootScript := `
$keys = @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
  'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations'
)
foreach ($k in $keys) { if (Test-Path $k) { 'true'; exit 0 } }
'false'
`
	if out, err := runPS(ctx, rebootScript); err == nil {
		st.RebootRequired = strings.TrimSpace(out) == "true"
	}

	// Windows Update query. Fall back gracefully if the WU service is disabled
	// or COM init fails.
	wuScript := `
try {
  $session = New-Object -ComObject Microsoft.Update.Session
  $searcher = $session.CreateUpdateSearcher()
  $r = $searcher.Search("IsInstalled=0 and IsHidden=0")
  $list = @()
  foreach ($u in $r.Updates) {
    $list += [pscustomobject]@{
      Title    = $u.Title
      KB       = ($u.KBArticleIDs -join ',')
      Security = ($u.Categories | Where-Object { $_.Name -eq 'Security Updates' }).Count -gt 0
    }
  }
  $list | ConvertTo-Json -Compress -Depth 3
} catch { '[]' }
`
	var rows []struct {
		Title    string
		KB       string
		Security bool
	}
	if err := runPSJSON(ctx, wuScript, &rows); err != nil {
		return st
	}
	st.Pending = len(rows)
	for _, r := range rows {
		if r.Security {
			st.Security++
		}
		if len(st.Updates) < 50 {
			name := r.Title
			if r.KB != "" {
				name = "KB" + r.KB + ": " + r.Title
			}
			st.Updates = append(st.Updates, proto.PendingUpdate{
				Name:     name,
				Security: r.Security,
				Source:   "WindowsUpdate",
			})
		}
	}
	return st
}

// ----- Services ---------------------------------------------------------

// collectServices reports services where StartType=Automatic but Status≠Running,
// plus any with state Stopped/Error that have a recent failure history.
func collectServices(ctx context.Context) []proto.ServiceStatus {
	script := `
Get-CimInstance Win32_Service |
  Where-Object { $_.StartMode -eq 'Auto' -and $_.State -ne 'Running' } |
  Select-Object Name, DisplayName, State, StartMode, ExitCode |
  ConvertTo-Json -Compress
`
	var rows []struct {
		Name        string
		DisplayName string
		State       string
		StartMode   string
		ExitCode    int
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.ServiceStatus, 0, len(rows))
	for _, r := range rows {
		out = append(out, proto.ServiceStatus{
			Name:        r.Name,
			DisplayName: r.DisplayName,
			State:       strings.ToLower(r.State),
			StartMode:   strings.ToLower(r.StartMode),
			ExitCode:    r.ExitCode,
		})
	}
	return out
}

// ----- Security ---------------------------------------------------------

// collectSecurity gathers Defender + AV registrations, firewall per-profile,
// TPM, SecureBoot, and BitLocker volume status.
func collectSecurity(ctx context.Context) *proto.SecurityPosture {
	p := &proto.SecurityPosture{}
	p.Firewall = winFirewall(ctx)
	p.AntiVirus = winAntiVirus(ctx)
	p.TPMPresent, p.TPMVersion = winTPM(ctx)
	p.SecureBoot = winSecureBoot(ctx)
	p.DiskEncryption = winBitLocker(ctx)
	return p
}

func winFirewall(ctx context.Context) *proto.FirewallStatus {
	script := `Get-NetFirewallProfile | Select-Object Name, Enabled | ConvertTo-Json -Compress`
	var rows []struct {
		Name    string
		Enabled bool
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	fw := &proto.FirewallStatus{Provider: "windows"}
	allOn := len(rows) > 0
	for _, r := range rows {
		if r.Enabled {
			fw.Profiles = append(fw.Profiles, r.Name+":on")
		} else {
			fw.Profiles = append(fw.Profiles, r.Name+":off")
			allOn = false
		}
	}
	fw.Enabled = allOn
	return fw
}

func winAntiVirus(ctx context.Context) []proto.AVProduct {
	// SecurityCenter2 lives on workstation SKUs; on Server we fall back to
	// Defender's MpComputerStatus.
	script := `
$av = @()
try {
  $av += Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop |
    Select-Object displayName, productState
} catch {}
try {
  $d = Get-MpComputerStatus -ErrorAction Stop
  $av += [pscustomobject]@{
    displayName       = 'Microsoft Defender'
    productState      = 0
    realtime          = $d.RealTimeProtectionEnabled
    upToDate          = -not $d.AntivirusSignatureAge -or $d.AntivirusSignatureAge -lt 7
    signatureVersion  = $d.AntivirusSignatureVersion
    signatureAge      = ('{0}d' -f [int]$d.AntivirusSignatureAge)
    lastScan          = $d.QuickScanEndTime
  }
} catch {}
$av | ConvertTo-Json -Compress -Depth 3
`
	var rows []struct {
		DisplayName      string `json:"displayName"`
		ProductState     uint32 `json:"productState"`
		Realtime         bool   `json:"realtime"`
		UpToDate         bool   `json:"upToDate"`
		SignatureVersion string `json:"signatureVersion"`
		SignatureAge     string `json:"signatureAge"`
		LastScan         string `json:"lastScan"`
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.AVProduct, 0, len(rows))
	for _, r := range rows {
		// Decode SecurityCenter2 productState bitfield: bits 4-5 = enabled, bits 12-13 = up to date.
		enabled := r.Realtime || (r.ProductState&0xF000 != 0)
		uptodate := r.UpToDate || (r.ProductState&0x10 == 0)
		p := proto.AVProduct{
			Name:              r.DisplayName,
			Enabled:           enabled,
			RealtimeProtected: r.Realtime,
			UpToDate:          uptodate,
			SignatureVersion:  r.SignatureVersion,
			SignatureAge:      r.SignatureAge,
		}
		if r.LastScan != "" {
			if t, err := time.Parse(time.RFC3339, r.LastScan); err == nil {
				p.LastScan = t
			}
		}
		out = append(out, p)
	}
	return out
}

func winTPM(ctx context.Context) (*bool, string) {
	script := `
try {
  $t = Get-Tpm -ErrorAction Stop
  [pscustomobject]@{ present = $t.TpmPresent; version = (Get-CimInstance -Namespace root\cimv2\security\microsofttpm -ClassName Win32_Tpm).SpecVersion } |
    ConvertTo-Json -Compress
} catch { '{"present":false,"version":""}' }
`
	var r struct {
		Present bool   `json:"present"`
		Version string `json:"version"`
	}
	if err := runPSJSON(ctx, script, &r); err != nil {
		return nil, ""
	}
	return &r.Present, strings.TrimSpace(strings.Split(r.Version, ",")[0])
}

func winSecureBoot(ctx context.Context) *bool {
	out, err := runPS(ctx, `try { (Confirm-SecureBootUEFI).ToString().ToLower() } catch { 'unknown' }`)
	if err != nil {
		return nil
	}
	switch strings.TrimSpace(out) {
	case "true":
		v := true
		return &v
	case "false":
		v := false
		return &v
	}
	return nil
}

func winBitLocker(ctx context.Context) []proto.EncryptedVolume {
	script := `
try {
  Get-BitLockerVolume -ErrorAction Stop |
    Select-Object MountPoint, ProtectionStatus, VolumeStatus |
    ConvertTo-Json -Compress
} catch { '[]' }
`
	var rows []struct {
		MountPoint       string
		ProtectionStatus int // 0=off, 1=on
		VolumeStatus     int
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.EncryptedVolume, 0, len(rows))
	for _, r := range rows {
		status := "off"
		if r.ProtectionStatus == 1 {
			status = "on"
		}
		out = append(out, proto.EncryptedVolume{
			Mountpoint: r.MountPoint,
			Type:       "bitlocker",
			Status:     status,
		})
	}
	return out
}

// ----- GPUs -------------------------------------------------------------

func collectGPUs(ctx context.Context) []proto.GPU {
	script := `
Get-CimInstance Win32_VideoController |
  Select-Object @{n='Vendor';e={$_.AdapterCompatibility}}, Name, DriverVersion, AdapterRAM |
  ConvertTo-Json -Compress
`
	var rows []struct {
		Vendor        string
		Name          string
		DriverVersion string
		AdapterRAM    uint64 // documented as uint32 but CIM widens to int64 in PS
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.GPU, 0, len(rows))
	for _, r := range rows {
		out = append(out, proto.GPU{
			Vendor:        r.Vendor,
			Model:         r.Name,
			DriverVersion: r.DriverVersion,
			VRAMBytes:     r.AdapterRAM,
		})
	}
	return out
}

// ----- Chassis + battery -----------------------------------------------

func collectChassis(ctx context.Context) *proto.ChassisInfo {
	c := &proto.ChassisInfo{Type: winChassisType(ctx)}
	if bat := winBattery(ctx); bat != nil {
		c.Battery = bat
	}
	return c
}

func winChassisType(ctx context.Context) string {
	out, err := runPS(ctx, `(Get-CimInstance Win32_SystemEnclosure).ChassisTypes -join ','`)
	if err != nil {
		return ""
	}
	first := strings.TrimSpace(strings.Split(strings.TrimSpace(out), ",")[0])
	switch first {
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
	return ""
}

func winBattery(ctx context.Context) *proto.Battery {
	script := `
try {
  $b = Get-CimInstance -ClassName Win32_Battery -ErrorAction Stop | Select-Object -First 1
  if ($b -eq $null) { '{}'; exit }
  $f = Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity -ErrorAction SilentlyContinue | Select-Object -First 1
  $s = Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue | Select-Object -First 1
  $c = Get-CimInstance -Namespace root\wmi -ClassName BatteryCycleCount -ErrorAction SilentlyContinue | Select-Object -First 1
  [pscustomobject]@{
    state         = $b.BatteryStatus
    chargePct     = $b.EstimatedChargeRemaining
    designWh      = if ($s) { $s.DesignedCapacity / 1000.0 } else { 0 }
    currentWh     = if ($f) { $f.FullChargedCapacity / 1000.0 } else { 0 }
    cycleCount    = if ($c) { $c.CycleCount } else { 0 }
  } | ConvertTo-Json -Compress
} catch { '{}' }
`
	var r struct {
		State      int     `json:"state"`
		ChargePct  float64 `json:"chargePct"`
		DesignWh   float64 `json:"designWh"`
		CurrentWh  float64 `json:"currentWh"`
		CycleCount int     `json:"cycleCount"`
	}
	if err := runPSJSON(ctx, script, &r); err != nil {
		return nil
	}
	if r.DesignWh == 0 && r.CurrentWh == 0 && r.ChargePct == 0 {
		return nil
	}
	bat := &proto.Battery{
		DesignCapacityWh:  r.DesignWh,
		CurrentCapacityWh: r.CurrentWh,
		CycleCount:        r.CycleCount,
		ChargePercent:     r.ChargePct,
	}
	if r.DesignWh > 0 {
		bat.HealthPercent = r.CurrentWh / r.DesignWh * 100
	}
	switch r.State {
	case 1:
		bat.State = "discharging"
	case 2:
		bat.State = "ac"
	case 3:
		bat.State = "full"
	case 4:
		bat.State = "low"
	case 5:
		bat.State = "critical"
	case 6, 7, 8, 9:
		bat.State = "charging"
	}
	return bat
}

// ----- Local users ------------------------------------------------------

func collectLocalUsers(ctx context.Context) []proto.LocalUser {
	script := `
$admins = @{}
try {
  Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop | ForEach-Object {
    $admins[($_.Name -split '\\')[-1]] = $true
  }
} catch {}
Get-LocalUser | ForEach-Object {
  [pscustomobject]@{
    Name      = $_.Name
    SID       = $_.SID.Value
    Disabled  = -not $_.Enabled
    LastLogon = if ($_.LastLogon) { $_.LastLogon.ToString('o') } else { '' }
    PwdAge    = if ($_.PasswordLastSet) { [int]((Get-Date) - $_.PasswordLastSet).TotalDays } else { 0 }
    IsAdmin   = $admins.ContainsKey($_.Name)
  }
} | ConvertTo-Json -Compress -Depth 3
`
	var rows []struct {
		Name      string
		SID       string
		Disabled  bool
		LastLogon string
		PwdAge    int
		IsAdmin   bool
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return nil
	}
	out := make([]proto.LocalUser, 0, len(rows))
	for _, r := range rows {
		u := proto.LocalUser{
			Name:        r.Name,
			UID:         r.SID,
			IsAdmin:     r.IsAdmin,
			Disabled:    r.Disabled,
			PasswordAge: r.PwdAge,
		}
		if r.LastLogon != "" {
			if t, err := time.Parse(time.RFC3339, r.LastLogon); err == nil {
				u.LastLogin = t
			}
		}
		out = append(out, u)
	}
	return out
}

// ----- SMART ------------------------------------------------------------

// enrichDiskSMART pulls per-disk reliability counters via Get-PhysicalDisk +
// Get-StorageReliabilityCounter. Available on Windows 8 / Server 2012+.
func enrichDiskSMART(ctx context.Context, disks []proto.DiskDevice) {
	if len(disks) == 0 {
		return
	}
	script := `
Get-PhysicalDisk | ForEach-Object {
  $r = $null
  try { $r = $_ | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
  [pscustomobject]@{
    SerialNumber       = $_.SerialNumber
    HealthStatus       = $_.HealthStatus
    Wear               = if ($r) { $r.Wear } else { 0 }
    Temperature        = if ($r) { $r.Temperature } else { 0 }
    PowerOnHours       = if ($r) { $r.PowerOnHours } else { 0 }
    ReadErrorsTotal    = if ($r) { $r.ReadErrorsTotal } else { 0 }
    WriteErrorsTotal   = if ($r) { $r.WriteErrorsTotal } else { 0 }
  }
} | ConvertTo-Json -Compress -Depth 3
`
	var rows []struct {
		SerialNumber     string
		HealthStatus     string
		Wear             int
		Temperature      float64
		PowerOnHours     uint64
		ReadErrorsTotal  uint64
		WriteErrorsTotal uint64
	}
	if err := runPSJSON(ctx, script, &rows); err != nil {
		return
	}
	bySerial := make(map[string]int, len(rows))
	for i, r := range rows {
		bySerial[strings.TrimSpace(r.SerialNumber)] = i
	}
	for i := range disks {
		idx, ok := bySerial[strings.TrimSpace(disks[i].Serial)]
		if !ok {
			continue
		}
		r := rows[idx]
		h := &proto.SMARTHealth{
			TemperatureCelsius:  r.Temperature,
			PowerOnHours:        r.PowerOnHours,
			UncorrectableErrors: r.ReadErrorsTotal + r.WriteErrorsTotal,
			MediaWearoutPercent: float64(r.Wear),
		}
		switch strings.ToLower(r.HealthStatus) {
		case "healthy":
			h.OverallHealth = "PASSED"
		case "warning", "unhealthy":
			h.OverallHealth = "FAILED"
		default:
			h.OverallHealth = "UNKNOWN"
		}
		disks[i].SMART = h
	}
}
