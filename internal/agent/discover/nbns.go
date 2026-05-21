package discover

import (
	"encoding/binary"
	"net"
	"strings"
	"time"
)

// nbnsLookup sends a unicast NetBIOS Name Service node-status (NBSTAT) query
// to `ip:137` and returns the first non-group, non-MSBROWSE machine name in
// the response. Returns "" on timeout or any parse error.
//
// Many Windows hosts, NAS boxes, and printers answer NBSTAT even when they
// don't do mDNS and have no PTR record, so this is a useful third-line
// fallback after mDNS and reverse DNS.
func nbnsLookup(ip string, timeout time.Duration) string {
	if ip == "" {
		return ""
	}
	conn, err := net.DialTimeout("udp4", net.JoinHostPort(ip, "137"), timeout)
	if err != nil {
		return ""
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(timeout))

	if _, err := conn.Write(buildNBSTATQuery()); err != nil {
		return ""
	}
	buf := make([]byte, 1500)
	n, err := conn.Read(buf)
	if err != nil {
		return ""
	}
	return parseNBSTATReply(buf[:n])
}

// buildNBSTATQuery returns a fixed NBNS NBSTAT query packet.
//
// Header (12 bytes): TXID=0x4a4a, flags=0x0000 (standard query, no recursion),
// QDCOUNT=1, others=0.
// Question:
//   - Name: encoded "*" padded to 16 bytes with NULs, then second-level
//     encoded into 32 ASCII chars + length prefix + null terminator.
//   - QTYPE=NBSTAT (0x0021), QCLASS=IN (0x0001).
func buildNBSTATQuery() []byte {
	hdr := []byte{
		0x4a, 0x4a, // txid
		0x00, 0x00, // flags
		0x00, 0x01, // qdcount
		0x00, 0x00, // ancount
		0x00, 0x00, // nscount
		0x00, 0x00, // arcount
	}
	// 16-byte NetBIOS name: "*" + 15 NUL bytes
	raw := make([]byte, 16)
	raw[0] = '*'
	encoded := encodeNetBIOSName(raw)
	q := append([]byte{0x20}, encoded...) // length prefix (0x20 = 32)
	q = append(q, 0x00)                   // null terminator
	q = append(q, 0x00, 0x21, 0x00, 0x01) // QTYPE NBSTAT, QCLASS IN
	return append(hdr, q...)
}

// encodeNetBIOSName performs the RFC 1001/1002 second-level encoding: each
// byte is split into two nibbles, each added to 'A'.
func encodeNetBIOSName(name []byte) []byte {
	out := make([]byte, 0, 32)
	for _, b := range name {
		out = append(out, 'A'+(b>>4), 'A'+(b&0x0f))
	}
	return out
}

// parseNBSTATReply walks an NBSTAT response and returns the first usable name.
// NBSTAT RDATA layout:
//   - 1 byte: NUM_NAMES
//   - For each name: 15 bytes ASCII name, 1 byte type, 2 bytes flags
//
// Flags bit 0x8000 = group name (skip). We pick the first name whose type
// indicates a workstation/server (0x00 or 0x20) and isn't the well-known
// "..__MSBROWSE__." entry.
func parseNBSTATReply(pkt []byte) string {
	if len(pkt) < 12 {
		return ""
	}
	if binary.BigEndian.Uint16(pkt[6:8]) == 0 { // ANCOUNT
		return ""
	}
	// Skip header.
	off := 12
	// Skip the answer's name (length-prefixed labels, terminated by 0x00).
	for off < len(pkt) {
		ln := int(pkt[off])
		if ln == 0 {
			off++
			break
		}
		if ln&0xc0 == 0xc0 { // pointer
			off += 2
			break
		}
		off += 1 + ln
	}
	if off+10 > len(pkt) {
		return ""
	}
	// Skip TYPE(2) CLASS(2) TTL(4) RDLEN(2)
	off += 10
	if off >= len(pkt) {
		return ""
	}
	num := int(pkt[off])
	off++
	// Pass 1 prefers workstation/server (0x00, 0x20). Pass 2 falls back to
	// messenger (0x03) and any other non-group entry — better a less ideal
	// name than no name at all.
	for pass := 0; pass < 2; pass++ {
		o := off
		for i := 0; i < num && o+18 <= len(pkt); i++ {
			nameRaw := pkt[o : o+15]
			typ := pkt[o+15]
			flags := binary.BigEndian.Uint16(pkt[o+16 : o+18])
			o += 18
			if flags&0x8000 != 0 { // group
				continue
			}
			if pass == 0 && typ != 0x00 && typ != 0x20 {
				continue
			}
			name := strings.TrimSpace(string(nameRaw))
			if name == "" || strings.Contains(name, "__MSBROWSE__") {
				continue
			}
			return strings.ToLower(name)
		}
	}
	return ""
}
