//go:build !linux && !darwin

package discover

// platformSSID is a stub for platforms where SSID detection is not implemented.
func platformSSID(iface string) string { return "" }
