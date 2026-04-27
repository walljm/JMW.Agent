package discover

import (
	"crypto/tls"
	"net"
	"strconv"
	"strings"
	"time"
)

// tlsCertName attempts a TLS handshake against common HTTPS ports and
// returns the most useful name from the leaf certificate:
//   - the first DNS SAN (preferred — modern certs put names here), or
//   - the Subject CommonName as a fallback.
//
// Returns "" if no port answers or the cert has no usable name. Self-signed
// certs are accepted intentionally — most appliances ship with one.
func tlsCertName(ip string, timeout time.Duration) string {
	if ip == "" {
		return ""
	}
	for _, port := range []int{443, 8443} {
		if name := tlsCertNameFromPort(ip, port, timeout); name != "" {
			return name
		}
	}
	return ""
}

func tlsCertNameFromPort(ip string, port int, timeout time.Duration) string {
	dialer := &net.Dialer{Timeout: timeout}
	conn, err := tls.DialWithDialer(dialer, "tcp", net.JoinHostPort(ip, strconv.Itoa(port)), &tls.Config{
		InsecureSkipVerify: true,
		ServerName:         ip, // some servers refuse handshake without SNI
	})
	if err != nil {
		return ""
	}
	defer conn.Close()

	state := conn.ConnectionState()
	if len(state.PeerCertificates) == 0 {
		return ""
	}
	leaf := state.PeerCertificates[0]
	for _, dns := range leaf.DNSNames {
		dns = strings.TrimSpace(dns)
		if dns == "" || strings.HasPrefix(dns, "*.") {
			continue
		}
		// Skip the ip-as-name case; that's not informative.
		if dns == ip {
			continue
		}
		return strings.ToLower(dns)
	}
	cn := strings.TrimSpace(leaf.Subject.CommonName)
	if cn == "" || cn == ip {
		return ""
	}
	return strings.ToLower(cn)
}
