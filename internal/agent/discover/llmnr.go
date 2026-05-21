package discover

import (
	"encoding/binary"
	"net"
	"strings"
	"time"
)

// llmnrLookup sends an LLMNR PTR query for `ip` to the link-local multicast
// group 224.0.0.252:5355 and returns the responder's hostname, or "" on
// timeout/no-response.
//
// Caveat: many LLMNR implementations only answer A/AAAA queries for their
// own name and ignore PTR queries (RFC 4795 makes PTR optional). Hit rate
// is low, but the cost is one UDP packet per IP, so we try anyway. Hosts
// that *do* answer give us a high-quality, modern-Windows-style hostname.
func llmnrLookup(ip string, timeout time.Duration) string {
	if ip == "" {
		return ""
	}
	parsed := net.ParseIP(ip).To4()
	if parsed == nil {
		return "" // IPv4 only for now
	}
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: 0})
	if err != nil {
		return ""
	}
	defer conn.Close()
	_ = conn.SetReadDeadline(time.Now().Add(timeout))

	dst := &net.UDPAddr{IP: net.IPv4(224, 0, 0, 252), Port: 5355}
	q := buildLLMNRPTRQuery(parsed)
	if _, err := conn.WriteTo(q, dst); err != nil {
		return ""
	}

	buf := make([]byte, 1500)
	for {
		n, _, err := conn.ReadFrom(buf)
		if err != nil {
			return ""
		}
		if name := parseLLMNRPTRReply(buf[:n]); name != "" {
			return name
		}
	}
}

// buildLLMNRPTRQuery builds a DNS-format PTR query for `<d>.<c>.<b>.<a>.in-addr.arpa.`
// with TXID 0x4b4b. LLMNR uses standard DNS wire format.
func buildLLMNRPTRQuery(ip net.IP) []byte {
	hdr := []byte{
		0x4b, 0x4b, // txid
		0x00, 0x00, // flags
		0x00, 0x01, // qdcount
		0x00, 0x00, // ancount
		0x00, 0x00, // nscount
		0x00, 0x00, // arcount
	}
	name := dnsEncode(reverseInAddr(ip))
	q := append(name, 0x00, 0x0c, 0x00, 0x01) // QTYPE=PTR, QCLASS=IN
	return append(hdr, q...)
}

func reverseInAddr(ip net.IP) string {
	return itoa(ip[3]) + "." + itoa(ip[2]) + "." + itoa(ip[1]) + "." + itoa(ip[0]) + ".in-addr.arpa"
}

func itoa(b byte) string {
	if b == 0 {
		return "0"
	}
	var out [3]byte
	n := 0
	for b > 0 {
		out[n] = '0' + b%10
		b /= 10
		n++
	}
	// reverse
	res := make([]byte, n)
	for i := 0; i < n; i++ {
		res[i] = out[n-1-i]
	}
	return string(res)
}

func dnsEncode(name string) []byte {
	var out []byte
	for _, label := range strings.Split(name, ".") {
		if label == "" {
			continue
		}
		out = append(out, byte(len(label)))
		out = append(out, []byte(label)...)
	}
	return out
}

// parseLLMNRPTRReply extracts the first PTR answer from an LLMNR response.
func parseLLMNRPTRReply(pkt []byte) string {
	if len(pkt) < 12 {
		return ""
	}
	if binary.BigEndian.Uint16(pkt[6:8]) == 0 { // ANCOUNT
		return ""
	}
	off := 12
	// Skip questions.
	qd := int(binary.BigEndian.Uint16(pkt[4:6]))
	for i := 0; i < qd && off < len(pkt); i++ {
		_, n, ok := decodeName(pkt, off)
		if !ok {
			return ""
		}
		off = n + 4
	}
	// First answer.
	_, n, ok := decodeName(pkt, off)
	if !ok {
		return ""
	}
	if n+10 > len(pkt) {
		return ""
	}
	typ := binary.BigEndian.Uint16(pkt[n : n+2])
	rdlen := int(binary.BigEndian.Uint16(pkt[n+8 : n+10]))
	rdataOff := n + 10
	if rdataOff+rdlen > len(pkt) {
		return ""
	}
	if typ != 0x000c { // PTR
		return ""
	}
	name, _, ok := decodeName(pkt, rdataOff)
	if !ok {
		return ""
	}
	name = strings.TrimSuffix(name, ".")
	if name == "" {
		return ""
	}
	return strings.ToLower(name)
}
