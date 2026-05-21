//go:build windows

// Per-snapshot system metrics for Windows. All facts here are sampled every
// heartbeat (default 30s), so we only use cheap syscall paths — no PowerShell
// shell-out (that lives in inventory_windows.go and runs every 24h).
package collect

import (
	"sync"
	"sync/atomic"
	"syscall"
	"unsafe"

	"github.com/walljm/jmwagent/internal/shared/proto"
	"golang.org/x/sys/windows"
)

// kernel32 hosts the few entry points that golang.org/x/sys/windows still
// doesn't wrap directly (GetTickCount64, GetSystemTimes, GlobalMemoryStatusEx).
// Everything else uses the typed wrappers from x/sys/windows.
var (
	kernel32                 = windows.NewLazySystemDLL("kernel32.dll")
	procGetTickCount64       = kernel32.NewProc("GetTickCount64")
	procGetSystemTimes       = kernel32.NewProc("GetSystemTimes")
	procGlobalMemoryStatusEx = kernel32.NewProc("GlobalMemoryStatusEx")
)

// uptimeSeconds returns ms-since-boot / 1000. GetTickCount64 doesn't roll over
// for ~584 million years, so no wraparound handling needed.
func uptimeSeconds() int64 {
	r1, _, _ := procGetTickCount64.Call()
	return int64(r1) / 1000
}

// loadAvg has no Windows equivalent. Return zeros — the field is omitempty.
func loadAvg() (float64, float64, float64) { return 0, 0, 0 }

// cpuPercent computes CPU% from GetSystemTimes deltas across calls. The first
// call seeds the baseline and returns 0 (no history yet), matching the
// Linux/Darwin behavior on cold start.
type cpuTimes struct {
	idle, kernel, user uint64
}

var (
	cpuMu   sync.Mutex
	lastCPU cpuTimes
	hasLast atomic.Bool
)

func cpuPercent() (float64, error) {
	now, err := readSystemTimes()
	if err != nil {
		return 0, err
	}
	cpuMu.Lock()
	defer cpuMu.Unlock()
	if !hasLast.Load() {
		lastCPU = now
		hasLast.Store(true)
		return 0, nil
	}
	dIdle := now.idle - lastCPU.idle
	// Note: GetSystemTimes returns kernel time *including* idle. To get true
	// kernel busy time, subtract idle.
	dKernel := (now.kernel - lastCPU.kernel) - dIdle
	dUser := now.user - lastCPU.user
	total := dIdle + dKernel + dUser
	lastCPU = now
	if total == 0 {
		return 0, nil
	}
	busy := dKernel + dUser
	return float64(busy) * 100.0 / float64(total), nil
}

func readSystemTimes() (cpuTimes, error) {
	var idle, kernel, user windows.Filetime
	r1, _, e1 := procGetSystemTimes.Call(
		uintptr(unsafe.Pointer(&idle)),
		uintptr(unsafe.Pointer(&kernel)),
		uintptr(unsafe.Pointer(&user)),
	)
	if r1 == 0 {
		if e1 == nil {
			e1 = syscall.EINVAL
		}
		return cpuTimes{}, e1
	}
	return cpuTimes{
		idle:   filetimeToUint64(idle),
		kernel: filetimeToUint64(kernel),
		user:   filetimeToUint64(user),
	}, nil
}

func filetimeToUint64(ft windows.Filetime) uint64 {
	return uint64(ft.HighDateTime)<<32 | uint64(ft.LowDateTime)
}

// memoryStatusEx mirrors the Win32 MEMORYSTATUSEX struct. dwLength must be
// set to sizeof(MEMORYSTATUSEX) before the call or it fails with ERROR_INVALID_PARAMETER.
type memoryStatusEx struct {
	dwLength                uint32
	dwMemoryLoad            uint32
	ullTotalPhys            uint64
	ullAvailPhys            uint64
	ullTotalPageFile        uint64
	ullAvailPageFile        uint64
	ullTotalVirtual         uint64
	ullAvailVirtual         uint64
	ullAvailExtendedVirtual uint64
}

func memInfo() (used uint64, total uint64, err error) {
	var m memoryStatusEx
	m.dwLength = uint32(unsafe.Sizeof(m))
	r1, _, e1 := procGlobalMemoryStatusEx.Call(uintptr(unsafe.Pointer(&m)))
	if r1 == 0 {
		if e1 == nil {
			e1 = syscall.EINVAL
		}
		return 0, 0, e1
	}
	return m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys, nil
}

// diskUsage walks every fixed local drive (C:, D:, ...). Network and removable
// drives are excluded so we don't stall on offline shares or surface ephemeral
// USB sticks.
func diskUsage() []proto.DiskSnapshot {
	drives := getLogicalDrives()
	out := make([]proto.DiskSnapshot, 0, len(drives))
	for _, d := range drives {
		dp, err := windows.UTF16PtrFromString(d)
		if err != nil {
			continue
		}
		// DRIVE_FIXED == 3.
		if windows.GetDriveType(dp) != 3 {
			continue
		}
		var freeAvailable, totalBytes, totalFree uint64
		if err := windows.GetDiskFreeSpaceEx(dp, &freeAvailable, &totalBytes, &totalFree); err != nil || totalBytes == 0 {
			continue
		}
		out = append(out, proto.DiskSnapshot{
			Device:     d,
			Mountpoint: d,
			TotalBytes: totalBytes,
			UsedBytes:  totalBytes - totalFree,
			FSType:     getVolumeFSType(dp),
		})
	}
	return out
}

func getLogicalDrives() []string {
	// Buffer up to 26 drive letter strings, each "X:\\\0" = 4 wchars.
	buf := make([]uint16, 26*4+1)
	n, err := windows.GetLogicalDriveStrings(uint32(len(buf)), &buf[0])
	if err != nil || n == 0 {
		return nil
	}
	// Output is double-NUL-terminated list of NUL-terminated UTF-16 strings.
	var out []string
	start := 0
	for i := 0; i < int(n); i++ {
		if buf[i] == 0 {
			if i > start {
				out = append(out, windows.UTF16ToString(buf[start:i]))
			}
			start = i + 1
		}
	}
	return out
}

func getVolumeFSType(rootPtr *uint16) string {
	var fsName [windows.MAX_PATH + 1]uint16
	if err := windows.GetVolumeInformation(
		rootPtr,
		nil, 0, // volume name buffer
		nil, // serial
		nil, // max component len
		nil, // fs flags
		&fsName[0], uint32(len(fsName)),
	); err != nil {
		return ""
	}
	return windows.UTF16ToString(fsName[:])
}

// ifaceStats: per-interface byte/packet counters require GetIfTable2 +
// MIB_IF_ROW2 decoding (~1.3KB struct, fields added across Windows
// versions). Skipping in v1; counters will show 0 and the dashboard's
// rate calculation will treat the interface as quiet. Add later via
// iphlpapi.GetIfEntry2 if you want the per-NIC graphs to populate.
func ifaceStats(name string) (rxBytes, txBytes, rxPkts, txPkts uint64, isUp bool) {
	_ = name
	return 0, 0, 0, 0, true
}

// startupBaseline pre-seeds CPU% so the first heartbeat after a fresh start
// doesn't report 0%. Without this, cpuPercent() returns 0 on its first call
// and the real value only appears 30s later.
func init() {
	if t, err := readSystemTimes(); err == nil {
		lastCPU = t
		hasLast.Store(true)
	}
}
