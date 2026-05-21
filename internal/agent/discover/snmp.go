package discover

import (
	"errors"
	"net"
	"strings"
	"time"
)

// snmpLookup sends an SNMPv2c GET for sysName.0 (1.3.6.1.2.1.1.5.0) to
// `ip:161`. Returns the sysName as a string, or "" on any failure
// (timeout, no SNMP, wrong community, parse error). It tries each community
// in order and returns the first success.
//
// Useful for naming routers, switches, APs, printers, NAS, UPS, ESXi —
// anything that runs SNMP, which mDNS and NBNS usually miss.
func snmpLookup(ip string, communities []string, timeout time.Duration) string {
	if ip == "" {
		return ""
	}
	if len(communities) == 0 {
		communities = []string{"public"}
	}
	for _, community := range communities {
		name, err := snmpGetSysName(ip, community, timeout)
		if err == nil && name != "" {
			return name
		}
	}
	return ""
}

// snmpGetSysName performs a single SNMPv2c GET for sysName.0.
func snmpGetSysName(ip, community string, timeout time.Duration) (string, error) {
	conn, err := net.DialTimeout("udp4", net.JoinHostPort(ip, "161"), timeout)
	if err != nil {
		return "", err
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(timeout))

	// sysName.0 = 1.3.6.1.2.1.1.5.0
	pkt := buildSNMPGet(community, []uint32{1, 3, 6, 1, 2, 1, 1, 5, 0}, 0x4a4a4a4a)
	if _, err := conn.Write(pkt); err != nil {
		return "", err
	}
	buf := make([]byte, 1500)
	n, err := conn.Read(buf)
	if err != nil {
		return "", err
	}
	return parseSNMPSysNameReply(buf[:n])
}

// buildSNMPGet builds a minimal SNMPv2c GetRequest PDU.
//
// SNMP message structure (BER/ASN.1):
//
//	SEQUENCE {
//	  INTEGER version (1 = v2c),
//	  OCTET STRING community,
//	  GetRequest-PDU [CONTEXT 0] {
//	    INTEGER request-id,
//	    INTEGER error-status (0),
//	    INTEGER error-index (0),
//	    SEQUENCE varbinds {
//	      SEQUENCE varbind {
//	        OBJECT IDENTIFIER oid,
//	        NULL value
//	      }
//	    }
//	  }
//	}
func buildSNMPGet(community string, oid []uint32, reqID uint32) []byte {
	varbind := berSeq(append(berOID(oid), berNull()...))
	varbinds := berSeq(varbind)
	pdu := berContext(0, // GetRequest = [CONTEXT 0]
		append(append(append(berInt(int64(reqID)), berInt(0)...), berInt(0)...), varbinds...),
	)
	msg := berSeq(append(append(berInt(1), berOctetString([]byte(community))...), pdu...))
	return msg
}

// parseSNMPSysNameReply walks an SNMPv2c GetResponse and returns the value
// of the first varbind, decoded as a UTF-8 string. Returns an error if the
// reply looks malformed or carries an SNMP error-status.
func parseSNMPSysNameReply(pkt []byte) (string, error) {
	// Outer SEQUENCE.
	seq, _, err := berRead(pkt)
	if err != nil || seq.tag != 0x30 {
		return "", errors.New("snmp: bad outer")
	}
	body := seq.value
	// version
	v, body, err := berRead(body)
	if err != nil || v.tag != 0x02 {
		return "", errors.New("snmp: bad version")
	}
	// community
	c, body, err := berRead(body)
	if err != nil || c.tag != 0x04 {
		return "", errors.New("snmp: bad community")
	}
	// PDU [CONTEXT n] (we accept any — GetResponse is [2])
	p, _, err := berRead(body)
	if err != nil || (p.tag&0xe0) != 0xa0 {
		return "", errors.New("snmp: bad pdu")
	}
	pduBody := p.value
	// request-id
	_, pduBody, err = berRead(pduBody)
	if err != nil {
		return "", err
	}
	// error-status
	es, pduBody, err := berRead(pduBody)
	if err != nil {
		return "", err
	}
	if es.tag == 0x02 && len(es.value) > 0 && es.value[0] != 0 {
		return "", errors.New("snmp: error-status nonzero")
	}
	// error-index
	_, pduBody, err = berRead(pduBody)
	if err != nil {
		return "", err
	}
	// varbinds SEQUENCE
	vbs, _, err := berRead(pduBody)
	if err != nil || vbs.tag != 0x30 {
		return "", errors.New("snmp: bad varbinds")
	}
	// first varbind
	vb, _, err := berRead(vbs.value)
	if err != nil || vb.tag != 0x30 {
		return "", errors.New("snmp: bad varbind")
	}
	// skip oid
	_, after, err := berRead(vb.value)
	if err != nil {
		return "", err
	}
	// value
	val, _, err := berRead(after)
	if err != nil {
		return "", err
	}
	if val.tag != 0x04 { // OCTET STRING
		return "", errors.New("snmp: value not string")
	}
	return strings.TrimSpace(string(val.value)), nil
}

// ---- minimal BER encoder/decoder ----

func berLen(n int) []byte {
	if n < 128 {
		return []byte{byte(n)}
	}
	// long form
	var b []byte
	for x := n; x > 0; x >>= 8 {
		b = append([]byte{byte(x & 0xff)}, b...)
	}
	return append([]byte{0x80 | byte(len(b))}, b...)
}

func berTLV(tag byte, value []byte) []byte {
	return append(append([]byte{tag}, berLen(len(value))...), value...)
}

func berSeq(value []byte) []byte         { return berTLV(0x30, value) }
func berOctetString(b []byte) []byte     { return berTLV(0x04, b) }
func berNull() []byte                    { return []byte{0x05, 0x00} }
func berContext(n byte, b []byte) []byte { return berTLV(0xa0|n|0x20, b) }

func berInt(v int64) []byte {
	if v == 0 {
		return berTLV(0x02, []byte{0x00})
	}
	// Two's complement, minimal length.
	b := make([]byte, 8)
	for i := 7; i >= 0; i-- {
		b[i] = byte(v & 0xff)
		v >>= 8
	}
	// Trim leading 0x00 bytes (or 0xff for negatives) when the next byte
	// preserves the sign bit.
	i := 0
	for i < 7 && ((b[i] == 0x00 && b[i+1]&0x80 == 0) || (b[i] == 0xff && b[i+1]&0x80 != 0)) {
		i++
	}
	return berTLV(0x02, b[i:])
}

func berOID(oid []uint32) []byte {
	if len(oid) < 2 {
		return berTLV(0x06, nil)
	}
	first := oid[0]*40 + oid[1]
	out := encodeBase128(first)
	for _, x := range oid[2:] {
		out = append(out, encodeBase128(x)...)
	}
	return berTLV(0x06, out)
}

func encodeBase128(v uint32) []byte {
	if v == 0 {
		return []byte{0}
	}
	var b []byte
	for v > 0 {
		b = append([]byte{byte(v & 0x7f)}, b...)
		v >>= 7
	}
	for i := 0; i < len(b)-1; i++ {
		b[i] |= 0x80
	}
	return b
}

type berValue struct {
	tag   byte
	value []byte
}

// berRead parses one BER TLV from the start of `buf` and returns the parsed
// value plus the remaining unread bytes.
func berRead(buf []byte) (berValue, []byte, error) {
	if len(buf) < 2 {
		return berValue{}, nil, errors.New("ber: short")
	}
	tag := buf[0]
	off := 1
	ln := int(buf[off])
	off++
	if ln&0x80 != 0 {
		nb := ln & 0x7f
		if nb == 0 || off+nb > len(buf) {
			return berValue{}, nil, errors.New("ber: bad length")
		}
		ln = 0
		for i := 0; i < nb; i++ {
			ln = (ln << 8) | int(buf[off+i])
		}
		off += nb
	}
	if off+ln > len(buf) {
		return berValue{}, nil, errors.New("ber: truncated")
	}
	return berValue{tag: tag, value: buf[off : off+ln]}, buf[off+ln:], nil
}
