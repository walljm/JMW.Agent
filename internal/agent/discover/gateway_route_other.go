//go:build !linux && !darwin

package discover

// platformDefaultGateway has no portable implementation on other
// platforms (notably Windows, where we'd need iphlpapi). Returns ""
// so gatewayARPLookup falls back to the heuristic guess.
func platformDefaultGateway() string { return "" }
