package infra

import (
	"context"
	"net"
	"strings"
)

// probeLDAPRootDSE does an anonymous LDAPv3 rootDSE query against ip:389 and
// returns a map of human-readable details. Returns nil on any failure.
//
// No external library is used. The request is a fixed BER-encoded LDAPv3
// SearchRequest (base="", scope=baseObject, filter=(objectClass=*)) and the
// response parser is purpose-built for the small set of attributes we need.
func probeLDAPRootDSE(ctx context.Context, ip string) map[string]string {
	d := &net.Dialer{}
	conn, err := d.DialContext(ctx, "tcp", ip+":389")
	if err != nil {
		return nil
	}
	defer conn.Close()

	if dl, ok := ctx.Deadline(); ok {
		conn.SetDeadline(dl) //nolint:errcheck
	}

	if _, err := conn.Write(rootDSEReq); err != nil {
		return nil
	}

	// Collect up to 16 KB; rootDSE responses are always tiny on LAN.
	var buf []byte
	tmp := make([]byte, 4096)
	for len(buf) < 16384 {
		n, err := conn.Read(tmp)
		if n > 0 {
			buf = append(buf, tmp[:n]...)
		}
		if err != nil {
			break
		}
		// Stop once we see the SearchResultDone tag (0x65).
		if hasTag(buf, 0x65) {
			break
		}
	}

	raw := parseRootDSEResponse(buf)
	if len(raw) == 0 {
		return nil
	}
	return buildDetails(raw)
}

// hasTag returns true if tag appears as a top-level TLV tag anywhere in buf.
func hasTag(buf []byte, tag byte) bool {
	for i := 0; i < len(buf); {
		if buf[i] == tag {
			return true
		}
		_, _, next, ok := berNext(buf, i)
		if !ok {
			break
		}
		i = next
	}
	return false
}

// ── request ───────────────────────────────────────────────────────────────────

var rootDSEAttrs = []string{
	"defaultNamingContext",
	"dnsHostName",
	"forestFunctionality",
	"domainFunctionality",
	"vendorName",
	"vendorVersion",
}

// rootDSEReq is the pre-built request; computed once at package init.
var rootDSEReq = buildRootDSERequest(rootDSEAttrs)

func buildRootDSERequest(attrs []string) []byte {
	// Attributes: SEQUENCE OF OCTET STRING
	var attrBytes []byte
	for _, a := range attrs {
		attrBytes = append(attrBytes, berOctetStr(a)...)
	}

	// SearchRequest body
	body := berOctetStr("")                           // baseObject = ""
	body = append(body, berEnum(0)...)                // scope = baseObject(0)
	body = append(body, berEnum(0)...)                // derefAliases = neverDerefAliases(0)
	body = append(body, berInt(0)...)                 // sizeLimit = 0
	body = append(body, berInt(10)...)                // timeLimit = 10s
	body = append(body, berBool(false)...)            // typesOnly = false
	body = append(body, berPresent("objectClass")...) // filter: (objectClass=*)
	body = append(body, berSeq(attrBytes)...)         // attributes

	// [APPLICATION 3] CONSTRUCTED = 0x63
	msg := berInt(1) // messageID = 1
	msg = append(msg, berTLV(0x63, body)...)
	return berSeq(msg)
}

// ── response parser ───────────────────────────────────────────────────────────

const (
	tagSequence          = byte(0x30)
	tagOctetString       = byte(0x04)
	tagSet               = byte(0x31)
	tagSearchResultEntry = byte(0x64) // [APPLICATION 4] CONSTRUCTED
	tagSearchResultDone  = byte(0x65) // [APPLICATION 5] CONSTRUCTED
)

// parseRootDSEResponse walks the raw LDAP response and extracts attribute
// values from the first SearchResultEntry. Keys are lowercased.
func parseRootDSEResponse(buf []byte) map[string]string {
	result := make(map[string]string)
	pos := 0
	for pos < len(buf) {
		tag, val, next, ok := berNext(buf, pos)
		pos = next
		if !ok {
			break
		}
		if tag != tagSequence { // LDAPMessage is a SEQUENCE
			continue
		}
		// Inside LDAPMessage: skip messageID, look for protocolOp.
		inner := 0
		for inner < len(val) {
			itag, ival, inext, iok := berNext(val, inner)
			inner = inext
			if !iok {
				break
			}
			switch itag {
			case tagSearchResultEntry:
				parseSearchEntry(ival, result)
			case tagSearchResultDone:
				return result
			}
		}
	}
	return result
}

// parseSearchEntry extracts PartialAttribute values from a SearchResultEntry body.
func parseSearchEntry(buf []byte, result map[string]string) {
	pos := 0
	// Skip objectName (OCTET STRING).
	_, _, next, ok := berNext(buf, pos)
	if !ok {
		return
	}
	pos = next

	// attributes: SEQUENCE OF PartialAttribute
	atTag, atBuf, _, ok := berNext(buf, pos)
	if !ok || atTag != tagSequence {
		return
	}

	aPos := 0
	for aPos < len(atBuf) {
		paTag, paBuf, paNext, ok := berNext(atBuf, aPos)
		aPos = paNext
		if !ok || paTag != tagSequence {
			continue
		}
		// type: OCTET STRING
		tTag, tVal, tNext, ok := berNext(paBuf, 0)
		if !ok || tTag != tagOctetString {
			continue
		}
		attrName := strings.ToLower(string(tVal))

		// vals: SET OF OCTET STRING — take the first value only.
		sTag, sBuf, _, ok := berNext(paBuf, tNext)
		if !ok || sTag != tagSet {
			continue
		}
		vTag, vVal, _, ok := berNext(sBuf, 0)
		if !ok || vTag != tagOctetString {
			continue
		}
		result[attrName] = string(vVal)
	}
}

// ── detail extraction ─────────────────────────────────────────────────────────

func buildDetails(raw map[string]string) map[string]string {
	d := make(map[string]string)
	if v := raw["defaultnamingcontext"]; v != "" {
		d["domain"] = parseDomainFromDN(v)
	}
	if v := raw["dnshostname"]; v != "" {
		d["dc_hostname"] = v
	}
	if v := raw["domainfunctionality"]; v != "" {
		d["ad_level"] = adFunctionalLevel(v)
	}
	if v := raw["vendorname"]; v != "" {
		d["vendor"] = v
	}
	if v := raw["vendorversion"]; v != "" {
		d["version"] = v
	}
	return d
}

// parseDomainFromDN converts "DC=home,DC=local" → "home.local".
func parseDomainFromDN(dn string) string {
	var labels []string
	for _, part := range strings.Split(strings.ToLower(dn), ",") {
		part = strings.TrimSpace(part)
		if strings.HasPrefix(part, "dc=") {
			labels = append(labels, strings.TrimPrefix(part, "dc="))
		}
	}
	return strings.Join(labels, ".")
}

func adFunctionalLevel(s string) string {
	switch s {
	case "0":
		return "2000"
	case "1":
		return "2003 Mixed"
	case "2":
		return "2003"
	case "3":
		return "2008"
	case "4":
		return "2008 R2"
	case "5":
		return "2012"
	case "6":
		return "2012 R2"
	case "7":
		return "2016+"
	default:
		return s
	}
}

// ── minimal BER encoder ───────────────────────────────────────────────────────

func berLen(n int) []byte {
	switch {
	case n < 0x80:
		return []byte{byte(n)}
	case n < 0x100:
		return []byte{0x81, byte(n)}
	default:
		return []byte{0x82, byte(n >> 8), byte(n)}
	}
}

func berTLV(tag byte, val []byte) []byte {
	b := []byte{tag}
	b = append(b, berLen(len(val))...)
	return append(b, val...)
}

func berSeq(val []byte) []byte    { return berTLV(0x30, val) }
func berInt(n int) []byte         { return berTLV(0x02, []byte{byte(n)}) }
func berEnum(n int) []byte        { return berTLV(0x0a, []byte{byte(n)}) }
func berOctetStr(s string) []byte { return berTLV(0x04, []byte(s)) }
func berBool(b bool) []byte {
	v := byte(0x00)
	if b {
		v = 0xff
	}
	return berTLV(0x01, []byte{v})
}

// berPresent encodes an LDAP "present" filter: [7] IMPLICIT OCTET STRING.
func berPresent(attr string) []byte {
	b := []byte{0x87}
	b = append(b, berLen(len(attr))...)
	return append(b, []byte(attr)...)
}

// ── minimal BER decoder ───────────────────────────────────────────────────────

// berNext reads one TLV from buf[pos:].
// Returns (tag, contents, nextPos, ok). ok=false means truncated/malformed.
func berNext(buf []byte, pos int) (byte, []byte, int, bool) {
	if pos+2 > len(buf) {
		return 0, nil, pos, false
	}
	tag := buf[pos]
	pos++

	first := buf[pos]
	pos++

	var length int
	if first < 0x80 {
		length = int(first)
	} else {
		n := int(first & 0x7f)
		if n == 0 || n > 4 || pos+n > len(buf) {
			return 0, nil, pos, false
		}
		for i := 0; i < n; i++ {
			length = length<<8 | int(buf[pos])
			pos++
		}
	}

	end := pos + length
	if end > len(buf) {
		return 0, nil, pos, false
	}
	return tag, buf[pos:end], end, true
}
