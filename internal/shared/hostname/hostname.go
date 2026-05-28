// Package hostname provides canonical hostname normalization and source-priority
// rules shared by both the server pipeline and the agent's discovery layer.
//
// Priority: lower integer = more authoritative. Ordered roughly as:
//
//	user-labelled → infrastructure records (DNS/DHCP) → protocol-announced
//	→ OS self-reported → broadcast/passive
package hostname

import (
	"net"
	"strings"
)

// Priority returns the canonical priority for a hostname source.
// Lower number = more authoritative. Used as the stored priority value in
// hostname_aliases; ORDER BY priority ASC picks the best name at query time.
func Priority(sourceKind string) int {
	switch sourceKind {
	case "user-input":
		return 1 // admin explicitly labelled this device
	case "ldap":
		return 3 // Active Directory dnsHostName — IT-provisioned
	case "terrain-dns":
		return 5 // forward/reverse DNS record in the configured DNS server
	case "terrain-dhcp":
		return 8 // name registered in the DHCP server for this lease
	case "hassio":
		return 10 // Home Assistant Supervisor's authoritative view of the host hostname
	case "snmp", "snmp-poller":
		return 12 // sysName is admin-configured, especially reliable for network gear
	case "smb":
		return 15 // NetBIOS computer name, reliable for Windows
	case "nbns":
		return 18 // NetBIOS Name Service
	case "dhcp":
		return 20 // DHCP hostname observed in traffic (less reliable than server record)
	case "agent":
		return 25 // OS self-reported via os.Hostname() — needs filtering
	case "ssh":
		return 28 // SSH banner hostname
	case "llmnr":
		return 35 // Link-Local Multicast Name Resolution
	case "mdns":
		return 40 // mDNS/Bonjour — .local suffix stripped, self-assigned
	case "eureka":
		return 45 // Google Cast / Nest device name
	case "docker":
		return 50 // Docker DNS — often synthetic
	case "ipp":
		return 55 // printer name from IPP
	case "roku":
		return 58
	case "airplay":
		return 60
	case "wsd":
		return 62 // WS-Discovery
	case "ssdp":
		return 65 // UPnP/SSDP
	case "garp":
		return 70 // gratuitous ARP from gateway SNMP
	case "tls":
		return 75 // TLS certificate CN/SAN
	case "rdns":
		return 80 // reverse DNS PTR — often ISP-assigned or stale
	case "http":
		return 85 // HTTP server header / page title
	case "nmap-scanner":
		return 90
	}
	return 100
}

// Normalize cleans a candidate hostname and returns the normalized form, or ""
// if the name is not useful (synthetic, local-only, or otherwise unreliable).
//
// Normalization rules applied in order:
//  1. Trim whitespace and trailing dot (DNS FQDN artefact).
//  2. Lowercase.
//  3. Strip ".local" suffix (mDNS/Bonjour names).
//  4. Reject known-bad names: localhost variants, Docker-internal, generic OS
//     defaults, and Docker container-ID hex strings.
//  5. Reject names that parse as an IP address.
//  6. Reject names shorter than 2 characters.
func Normalize(h string) string {
	h = strings.TrimSpace(h)
	h = strings.TrimSuffix(h, ".") // trailing dot from fully-qualified DNS names
	h = strings.ToLower(h)
	h = strings.TrimSuffix(h, ".local")

	if h == "" {
		return ""
	}

	// Reject Docker-internal magic names.
	if h == "docker.internal" || strings.HasSuffix(h, ".docker.internal") {
		return ""
	}

	// Reject known useless/synthetic names.
	switch h {
	case "localhost", "localdomain", "local",
		"ubuntu", "debian", "raspberrypi", "kali", "openwrt",
		"homeassistant", "android", "linux", "windows",
		"router", "gateway":
		return ""
	}

	// Reject Docker container IDs (12-character lowercase hex strings).
	if len(h) == 12 && isHex(h) {
		return ""
	}

	// Reject raw IP addresses.
	if net.ParseIP(h) != nil {
		return ""
	}

	if len(h) < 2 {
		return ""
	}

	return h
}

// IsUsable reports whether h is a meaningful hostname after normalization.
func IsUsable(h string) bool {
	return Normalize(h) != ""
}

func isHex(s string) bool {
	for _, c := range s {
		if !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) {
			return false
		}
	}
	return true
}
