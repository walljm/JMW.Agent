package discover

import (
	"net"
	"strings"
	"time"
)

// mdnsReversePTR sends multicast PTR queries for `<ip>.in-addr.arpa.` for
// every IP in `ips` that doesn't already have an mDNS hostname in `out`.
// Avahi and macOS mDNSResponder both answer reverse PTR over multicast,
// which often reveals friendly names that unicast PTR (queried via the
// system resolver) won't return. Updates `out` in place.
//
// We reuse the QU bit on the qclass so responders unicast back to our
// ephemeral port — same reasoning as the forward query in mdnsLookup.
func mdnsReversePTR(out map[string]MDNSInfo, ips []string, timeout time.Duration) {
	targets := make([]string, 0, len(ips))
	for _, ip := range ips {
		if ip == "" {
			continue
		}
		// Only IPv4; v6 reverse zones are unwieldy and most home
		// devices don't register them anyway.
		v4 := net.ParseIP(ip).To4()
		if v4 == nil {
			continue
		}
		if info, ok := out[ip]; ok && info.Hostname != "" && !looksLikeUUIDHost(info.Hostname) {
			continue
		}
		// reverse: 4.3.2.1.in-addr.arpa.
		name := joinReverse(v4) + ".in-addr.arpa."
		targets = append(targets, name)
	}
	if len(targets) == 0 {
		return
	}
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: 0})
	if err != nil {
		return
	}
	defer conn.Close()
	mcast := &net.UDPAddr{IP: net.IPv4(224, 0, 0, 251), Port: 5353}
	for _, t := range targets {
		if q, err := buildMDNSQuery(t, 12); err == nil {
			_, _ = conn.WriteTo(q, mcast)
		}
	}
	_ = conn.SetReadDeadline(time.Now().Add(timeout))
	buf := make([]byte, 9000)
	for {
		n, _, err := conn.ReadFrom(buf)
		if err != nil {
			break
		}
		pkt := append([]byte(nil), buf[:n]...)
		for _, r := range parseAllRecords(pkt) {
			if r.typ != 12 { // PTR
				continue
			}
			ip := ipFromReverse(r.owner)
			if ip == "" {
				continue
			}
			name, _, ok := decodeName(r.pkt, r.rdataOff)
			if !ok || name == "" {
				continue
			}
			h := strings.TrimSuffix(strings.TrimSuffix(strings.ToLower(name), "."), ".local")
			if h == "" || looksLikeUUIDHost(h) {
				continue
			}
			info := out[ip]
			if info.Hostname == "" || looksLikeUUIDHost(info.Hostname) {
				info.Hostname = h
				out[ip] = info
			}
		}
	}
}

// joinReverse turns 192.168.1.42 into "42.1.168.192".
func joinReverse(ip net.IP) string {
	v4 := ip.To4()
	if v4 == nil {
		return ""
	}
	return revIPByte(v4[3]) + "." + revIPByte(v4[2]) + "." + revIPByte(v4[1]) + "." + revIPByte(v4[0])
}

// ipFromReverse parses "42.1.168.192.in-addr.arpa." back into 192.168.1.42.
// Returns "" if the owner doesn't look like a v4 reverse name.
func ipFromReverse(owner string) string {
	owner = strings.TrimSuffix(strings.ToLower(owner), ".")
	if !strings.HasSuffix(owner, ".in-addr.arpa") {
		return ""
	}
	front := strings.TrimSuffix(owner, ".in-addr.arpa")
	parts := strings.Split(front, ".")
	if len(parts) != 4 {
		return ""
	}
	// Reverse the order.
	return parts[3] + "." + parts[2] + "." + parts[1] + "." + parts[0]
}

// revIPByte is a tiny stack-friendly byte-to-decimal-string converter
// used by joinReverse to build the in-addr.arpa label sequence.
func revIPByte(b byte) string {
	if b == 0 {
		return "0"
	}
	var buf [3]byte
	n := len(buf)
	for x := int(b); x > 0; x /= 10 {
		n--
		buf[n] = byte('0' + x%10)
	}
	return string(buf[n:])
}
