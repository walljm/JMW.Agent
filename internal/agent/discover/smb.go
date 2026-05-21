package discover

import (
	"crypto/rand"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"strings"
	"time"
	"unicode/utf16"
)

// smbLookup connects to `ip:445` and walks the SMB2 NEGOTIATE +
// SESSION_SETUP handshake just far enough to receive an NTLMSSP CHALLENGE
// (Type 2) message, then extracts the server's hostname from the NTLM
// TargetInfo AV pairs.
//
// We prefer the DNS computer name (AV id 3); if the server only sends the
// NetBIOS computer name (AV id 1) we use that instead. Returns "" on any
// failure — a missing answer is the normal case for non-SMB hosts.
//
// This is the technique nmap's `smb-os-discovery` script uses: NTLMSSP
// requires the server to disclose its identity in the challenge, so
// modern Windows + Samba both reveal hostname here even when NetBIOS Name
// Service (137) is disabled.
func smbLookup(ip string, timeout time.Duration) string {
	if ip == "" {
		return ""
	}
	conn, err := net.DialTimeout("tcp", net.JoinHostPort(ip, "445"), timeout)
	if err != nil {
		return ""
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(timeout))

	// 1) SMB2 NEGOTIATE
	if _, err := conn.Write(buildSMB2Negotiate()); err != nil {
		return ""
	}
	if _, err := readSMB2Response(conn); err != nil {
		return ""
	}

	// 2) SMB2 SESSION_SETUP carrying NTLMSSP NEGOTIATE
	if _, err := conn.Write(buildSMB2SessionSetup()); err != nil {
		return ""
	}
	resp, err := readSMB2Response(conn)
	if err != nil {
		return ""
	}
	// SESSION_SETUP response carries an NTLMSSP CHALLENGE in its Buffer.
	blob, err := extractSecurityBlob(resp)
	if err != nil {
		return ""
	}
	return parseNTLMChallengeName(blob)
}

// readSMB2Response reads one NetBIOS Session Service framed message from
// conn. Returns just the SMB2 payload (header + body), without the 4-byte
// NetBIOS framing. Caps at 64 KiB to bound memory.
func readSMB2Response(conn net.Conn) ([]byte, error) {
	hdr := make([]byte, 4)
	if _, err := io.ReadFull(conn, hdr); err != nil {
		return nil, err
	}
	// NetBIOS length is 24 bits.
	ln := int(hdr[1])<<16 | int(hdr[2])<<8 | int(hdr[3])
	if ln <= 0 || ln > 65536 {
		return nil, errors.New("smb: bad length")
	}
	body := make([]byte, ln)
	if _, err := io.ReadFull(conn, body); err != nil {
		return nil, err
	}
	return body, nil
}

// nbssWrap prepends the 4-byte NetBIOS Session Service framing to an SMB
// payload (type=0x00 = session message; 24-bit big-endian length).
func nbssWrap(payload []byte) []byte {
	ln := len(payload)
	out := make([]byte, 4+ln)
	out[0] = 0x00
	out[1] = byte(ln >> 16)
	out[2] = byte(ln >> 8)
	out[3] = byte(ln)
	copy(out[4:], payload)
	return out
}

// smb2Header builds a 64-byte SMB2 sync header for the given command and
// message id.
func smb2Header(cmd uint16, msgID uint64) []byte {
	h := make([]byte, 64)
	copy(h[0:4], []byte{0xFE, 'S', 'M', 'B'})
	binary.LittleEndian.PutUint16(h[4:6], 64) // StructureSize
	binary.LittleEndian.PutUint16(h[6:8], 0)  // CreditCharge
	binary.LittleEndian.PutUint32(h[8:12], 0) // Status
	binary.LittleEndian.PutUint16(h[12:14], cmd)
	binary.LittleEndian.PutUint16(h[14:16], 1) // CreditRequest
	binary.LittleEndian.PutUint32(h[16:20], 0) // Flags
	binary.LittleEndian.PutUint32(h[20:24], 0) // NextCommand
	binary.LittleEndian.PutUint64(h[24:32], msgID)
	// Reserved (4) + TreeId (4) + SessionId (8) + Signature (16) all zero.
	return h
}

// buildSMB2Negotiate builds an SMB2 NEGOTIATE request advertising just
// dialect 0x0202 (SMB 2.0.2). Most servers will accept and respond.
func buildSMB2Negotiate() []byte {
	hdr := smb2Header(0x0000, 1) // SMB2_NEGOTIATE
	body := make([]byte, 36+2)
	binary.LittleEndian.PutUint16(body[0:2], 36)   // StructureSize
	binary.LittleEndian.PutUint16(body[2:4], 1)    // DialectCount
	binary.LittleEndian.PutUint16(body[4:6], 0x01) // SecurityMode = signing enabled
	// Reserved (2), Capabilities (4), ClientStartTime (8) all zero.
	guid := body[8:24]
	_, _ = rand.Read(guid)
	binary.LittleEndian.PutUint16(body[36:38], 0x0202) // dialect: SMB 2.0.2
	return nbssWrap(append(hdr, body...))
}

// buildSMB2SessionSetup builds an SMB2 SESSION_SETUP request whose
// security buffer carries an NTLMSSP NEGOTIATE_MESSAGE (Type 1).
func buildSMB2SessionSetup() []byte {
	hdr := smb2Header(0x0001, 2) // SMB2_SESSION_SETUP
	ntlm := buildNTLMNegotiate()

	// SESSION_SETUP request fixed body is 25 bytes; security buffer follows.
	body := make([]byte, 24)
	binary.LittleEndian.PutUint16(body[0:2], 25) // StructureSize (must be 25 even though body is 24+blob)
	body[2] = 0x00                               // Flags
	body[3] = 0x01                               // SecurityMode = signing enabled
	binary.LittleEndian.PutUint32(body[4:8], 0)  // Capabilities
	binary.LittleEndian.PutUint32(body[8:12], 0) // Channel
	// SecurityBufferOffset is from the start of the SMB2 header.
	secOff := uint16(64 + 24)
	binary.LittleEndian.PutUint16(body[12:14], secOff)
	binary.LittleEndian.PutUint16(body[14:16], uint16(len(ntlm)))
	binary.LittleEndian.PutUint64(body[16:24], 0) // PreviousSessionId

	full := append(hdr, body...)
	full = append(full, ntlm...)
	return nbssWrap(full)
}

// buildNTLMNegotiate builds a minimal NTLMSSP NEGOTIATE_MESSAGE (Type 1).
// The flags request Unicode + a TargetInfo block in the response, which
// is what we need to extract the hostname.
func buildNTLMNegotiate() []byte {
	out := make([]byte, 32)
	copy(out[0:8], "NTLMSSP\x00")
	binary.LittleEndian.PutUint32(out[8:12], 1) // MessageType = NEGOTIATE
	// Flags: Unicode | OEM | RequestTarget | NTLM | Always Sign | NTLMv2 | TargetInfo | 56 | 128 | KeyExch
	const flags = 0x60088215 |
		0x80000000 | // 56
		0x20000000 | // 128
		0x00000800 | // Anonymous
		0x00800000 | // TargetInfo
		0x40000000 | // KeyExch
		0x00000001 | // UnicodeEncoding (already in 0x6...)
		0
	binary.LittleEndian.PutUint32(out[12:16], uint32(flags))
	// DomainName (8 bytes len/maxlen/offset = 0,0,0) + Workstation (same) +
	// Version (8 bytes, all zero) — already zero.
	return out
}

// extractSecurityBlob reads the SMB2 SESSION_SETUP response and returns
// the contents of the security buffer.
func extractSecurityBlob(resp []byte) ([]byte, error) {
	if len(resp) < 64+8 {
		return nil, errors.New("smb: short response")
	}
	body := resp[64:]
	if len(body) < 8 {
		return nil, errors.New("smb: short body")
	}
	secOff := int(binary.LittleEndian.Uint16(body[4:6]))
	secLen := int(binary.LittleEndian.Uint16(body[6:8]))
	if secOff < 64 || secLen <= 0 || secOff-64+secLen > len(body) {
		return nil, errors.New("smb: bad security buffer")
	}
	return body[secOff-64 : secOff-64+secLen], nil
}

// parseNTLMChallengeName extracts a hostname from an NTLMSSP
// CHALLENGE_MESSAGE (Type 2). Modern Windows / Samba wrap the NTLMSSP
// blob in a SPNEGO/GSS-API token; we tolerate that by scanning for the
// NTLMSSP signature within the blob.
func parseNTLMChallengeName(blob []byte) string {
	// Find "NTLMSSP\0".
	idx := strings.Index(string(blob), "NTLMSSP\x00")
	if idx < 0 {
		return ""
	}
	ntlm := blob[idx:]
	if len(ntlm) < 48 {
		return ""
	}
	if binary.LittleEndian.Uint32(ntlm[8:12]) != 2 { // MessageType = 2
		return ""
	}
	// TargetInfoFields at offset 40: len(2), maxlen(2), offset(4).
	tiLen := int(binary.LittleEndian.Uint16(ntlm[40:42]))
	tiOff := int(binary.LittleEndian.Uint32(ntlm[44:48]))
	if tiLen <= 0 || tiOff < 0 || tiOff+tiLen > len(ntlm) {
		return ""
	}
	av := ntlm[tiOff : tiOff+tiLen]

	var nbName, dnsName string
	for off := 0; off+4 <= len(av); {
		id := binary.LittleEndian.Uint16(av[off : off+2])
		ln := int(binary.LittleEndian.Uint16(av[off+2 : off+4]))
		off += 4
		if id == 0x0000 || off+ln > len(av) {
			break
		}
		val := av[off : off+ln]
		off += ln
		switch id {
		case 0x0001: // MsvAvNbComputerName
			nbName = decodeUTF16LE(val)
		case 0x0003: // MsvAvDnsComputerName
			dnsName = decodeUTF16LE(val)
		}
	}
	if dnsName != "" {
		return strings.ToLower(dnsName)
	}
	if nbName != "" {
		return strings.ToLower(nbName)
	}
	return ""
}

// decodeUTF16LE decodes a UTF-16LE byte slice to a Go string. Returns ""
// on odd-length input.
func decodeUTF16LE(b []byte) string {
	if len(b)%2 != 0 {
		return ""
	}
	u := make([]uint16, len(b)/2)
	for i := range u {
		u[i] = binary.LittleEndian.Uint16(b[i*2 : i*2+2])
	}
	return string(utf16.Decode(u))
}
