package discover

import (
	"errors"
	"net"
	"strings"
	"time"
)

// GatewayARPEntry is one MAC/IP pairing scraped from a gateway's SNMP
// IP-Net-To-Media-Table (`1.3.6.1.2.1.4.22.1.2`). When an agent's host
// can SNMP-walk its default gateway, this surfaces every host the gateway
// has talked to recently — including hosts on VLANs the agent isn't on.
type GatewayARPEntry struct {
	IP  string
	MAC string
}

// gatewayARPLookup walks the default gateway's ipNetToMediaPhysAddress
// table over SNMPv2c. Returns nil if no gateway is detectable, the
// gateway doesn't speak SNMP, or no community in `communities` works.
func gatewayARPLookup(communities []string, timeout time.Duration) []GatewayARPEntry {
	gw := defaultGatewayIP()
	if gw == "" {
		return nil
	}
	if len(communities) == 0 {
		communities = []string{"public"}
	}
	for _, community := range communities {
		entries, err := snmpWalkARPTable(gw, community, timeout)
		if err == nil && len(entries) > 0 {
			return entries
		}
	}
	return nil
}

// snmpWalkARPTable issues repeated GetNext requests against the
// ipNetToMediaPhysAddress sub-OID and decodes each varbind into an
// (ip, mac) pair. The OID layout is:
//
//	1.3.6.1.2.1.4.22.1.2.<ifIndex>.<a>.<b>.<c>.<d>
//
// where the trailing 5 sub-IDs identify the interface and the IPv4
// address, and the value is the MAC as a 6-byte OCTET STRING.
func snmpWalkARPTable(ip, community string, timeout time.Duration) ([]GatewayARPEntry, error) {
	conn, err := net.DialTimeout("udp4", net.JoinHostPort(ip, "161"), timeout)
	if err != nil {
		return nil, err
	}
	defer conn.Close()

	baseOID := []uint32{1, 3, 6, 1, 2, 1, 4, 22, 1, 2}
	cur := append([]uint32(nil), baseOID...)
	deadline := time.Now().Add(timeout)
	var out []GatewayARPEntry
	reqID := uint32(0x4a4d0001)

	for i := 0; i < 256; i++ { // hard cap so a buggy agent can't loop forever
		left := time.Until(deadline)
		if left < 100*time.Millisecond {
			break
		}
		_ = conn.SetDeadline(time.Now().Add(left))

		pkt := buildSNMPGetNext(community, cur, reqID)
		reqID++
		if _, err := conn.Write(pkt); err != nil {
			break
		}
		buf := make([]byte, 4096)
		n, err := conn.Read(buf)
		if err != nil {
			break
		}
		nextOID, mac, err := parseSNMPARPResponse(buf[:n])
		if err != nil {
			break
		}
		// Walked off the table when the returned OID isn't a child of base.
		if !oidHasPrefix(nextOID, baseOID) {
			break
		}
		// Trailing 5 elements are ifIndex, a, b, c, d.
		if len(nextOID) < len(baseOID)+5 {
			break
		}
		tail := nextOID[len(nextOID)-4:]
		ipStr := joinIPv4(tail)
		if ipStr != "" && mac != "" {
			out = append(out, GatewayARPEntry{IP: ipStr, MAC: mac})
		}
		cur = nextOID
	}
	return out, nil
}

// buildSNMPGetNext is the same SEQUENCE as buildSNMPGet but with a
// GetNextRequest-PDU tag ([CONTEXT 1] = 0xa1).
func buildSNMPGetNext(community string, oid []uint32, reqID uint32) []byte {
	varbind := berSeq(append(berOID(oid), berNull()...))
	varbinds := berSeq(varbind)
	pdu := berContext(1, // GetNextRequest = [CONTEXT 1]
		append(append(append(berInt(int64(reqID)), berInt(0)...), berInt(0)...), varbinds...),
	)
	msg := berSeq(append(append(berInt(1), berOctetString([]byte(community))...), pdu...))
	return msg
}

// parseSNMPARPResponse expects a GetResponse with a single varbind whose
// OID names an entry in ipNetToMediaPhysAddress and whose value is the
// 6-byte MAC. Returns the OID and the MAC string, or an error.
func parseSNMPARPResponse(pkt []byte) ([]uint32, string, error) {
	seq, _, err := berRead(pkt)
	if err != nil || seq.tag != 0x30 {
		return nil, "", errors.New("snmp: bad outer")
	}
	body := seq.value
	if _, body, err = berRead(body); err != nil { // version
		return nil, "", err
	}
	if _, body, err = berRead(body); err != nil { // community
		return nil, "", err
	}
	pdu, _, err := berRead(body)
	if err != nil {
		return nil, "", err
	}
	pduBody := pdu.value
	if _, pduBody, err = berRead(pduBody); err != nil { // request-id
		return nil, "", err
	}
	es, pduBody, err := berRead(pduBody) // error-status
	if err != nil {
		return nil, "", err
	}
	if len(es.value) > 0 && es.value[0] != 0 {
		return nil, "", errors.New("snmp: error-status nonzero")
	}
	if _, pduBody, err = berRead(pduBody); err != nil { // error-index
		return nil, "", err
	}
	vbs, _, err := berRead(pduBody)
	if err != nil || vbs.tag != 0x30 {
		return nil, "", errors.New("snmp: bad varbinds")
	}
	vb, _, err := berRead(vbs.value)
	if err != nil || vb.tag != 0x30 {
		return nil, "", errors.New("snmp: bad varbind")
	}
	oidTLV, after, err := berRead(vb.value)
	if err != nil || oidTLV.tag != 0x06 {
		return nil, "", errors.New("snmp: bad oid")
	}
	val, _, err := berRead(after)
	if err != nil {
		return nil, "", err
	}
	// endOfMibView = [CONTEXT 2] (0x82) — terminates the walk.
	if val.tag == 0x82 {
		return nil, "", errors.New("snmp: end of mib")
	}
	if val.tag != 0x04 || len(val.value) != 6 {
		return nil, "", errors.New("snmp: not a 6-byte mac")
	}
	mac := macFromBytes(val.value)
	oid, err := decodeOID(oidTLV.value)
	return oid, mac, err
}

// decodeOID is the inverse of berOID: read base-128 sub-IDs into a
// uint32 slice. The first byte combines the first two arcs.
func decodeOID(b []byte) ([]uint32, error) {
	if len(b) == 0 {
		return nil, nil
	}
	var out []uint32
	out = append(out, uint32(b[0])/40, uint32(b[0])%40)
	var v uint32
	for _, x := range b[1:] {
		v = (v << 7) | uint32(x&0x7f)
		if x&0x80 == 0 {
			out = append(out, v)
			v = 0
		}
	}
	return out, nil
}

func oidHasPrefix(oid, prefix []uint32) bool {
	if len(oid) < len(prefix) {
		return false
	}
	for i := range prefix {
		if oid[i] != prefix[i] {
			return false
		}
	}
	return true
}

func joinIPv4(parts []uint32) string {
	if len(parts) != 4 {
		return ""
	}
	for _, p := range parts {
		if p > 255 {
			return ""
		}
	}
	return net.IPv4(byte(parts[0]), byte(parts[1]), byte(parts[2]), byte(parts[3])).String()
}

func macFromBytes(b []byte) string {
	if len(b) != 6 {
		return ""
	}
	const hex = "0123456789abcdef"
	out := make([]byte, 0, 17)
	for i, x := range b {
		if i > 0 {
			out = append(out, ':')
		}
		out = append(out, hex[x>>4], hex[x&0x0f])
	}
	return string(out)
}

// defaultGatewayIP returns the IPv4 address of the default route. Best-
// effort: opens a UDP "connection" to a public address and reads the
// chosen local interface, then walks `ip route` / `route -n get` style
// approaches via OS-specific helpers when the simple trick doesn't work.
func defaultGatewayIP() string {
	if gw := platformDefaultGateway(); gw != "" {
		return gw
	}
	// Fallback: derive interface, then assume gateway is .1 of that subnet.
	// This is wrong on plenty of networks (router on .254 etc.) but
	// covers the common home/SMB case where SNMP discovery would
	// otherwise be silently skipped.
	conn, err := net.Dial("udp4", "8.8.8.8:53")
	if err != nil {
		return ""
	}
	defer conn.Close()
	la, ok := conn.LocalAddr().(*net.UDPAddr)
	if !ok || la.IP == nil {
		return ""
	}
	ip := la.IP.To4()
	if ip == nil {
		return ""
	}
	// Find the matching interface so we can walk its mask.
	ifs, err := net.Interfaces()
	if err != nil {
		return ""
	}
	for _, ifc := range ifs {
		addrs, err := ifc.Addrs()
		if err != nil {
			continue
		}
		for _, a := range addrs {
			if ipn, ok := a.(*net.IPNet); ok && ipn.Contains(ip) {
				m := ipn.Mask
				if len(m) == 4 {
					gw := net.IPv4(ip[0]&m[0]|^m[0]&0, ip[1]&m[1]|^m[1]&0, ip[2]&m[2]|^m[2]&0, (ip[3]&m[3]|^m[3]&0)+1)
					if !strings.HasSuffix(gw.String(), ".0") {
						return gw.String()
					}
				}
			}
		}
	}
	return ""
}
