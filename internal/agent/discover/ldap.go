package discover

import (
	"net"
	"strings"
	"time"
)

// LDAPInfo is what we extract from an anonymous rootDSE search against
// ldap://<ip>:389. A successful response strongly indicates an Active
// Directory domain controller (or other LDAP server). The dnsHostName
// attribute gives us the DC's FQDN — the most authoritative identity
// available for a Windows DC.
type LDAPInfo struct {
	DNSHostName            string `json:"dns_hostname,omitempty"`
	DefaultNamingContext   string `json:"default_naming_context,omitempty"`
	DomainFunctionality    string `json:"domain_functionality,omitempty"`
	ForestFunctionality    string `json:"forest_functionality,omitempty"`
	LDAPServiceName        string `json:"ldap_service_name,omitempty"`
	ServerName             string `json:"server_name,omitempty"`
}

// ldapProbe sends a single anonymous LDAPv3 SearchRequest against the
// rootDSE (baseObject="", scope=base, filter=(objectClass=*)) on port
// 389 and parses any returned attributes. Returns nil for non-LDAP
// hosts, timeouts, or LDAP servers that refuse anonymous binds.
//
// LDAP wire format (RFC 4511): BER-encoded LDAPMessage SEQUENCE with a
// messageID INTEGER and a protocolOp CHOICE. We send a single
// SearchRequest [APPLICATION 3] without a prior Bind — most directories
// allow rootDSE access unauthenticated.
func ldapProbe(ip string, timeout time.Duration) *LDAPInfo {
	if ip == "" {
		return nil
	}
	conn, err := net.DialTimeout("tcp", net.JoinHostPort(ip, "389"), timeout)
	if err != nil {
		return nil
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(timeout))

	req := buildLDAPRootDSESearch()
	if _, err := conn.Write(req); err != nil {
		return nil
	}
	// Drain everything until the SearchResultDone or the deadline. LDAP
	// can chunk responses across multiple TCP reads.
	var buf []byte
	tmp := make([]byte, 4096)
	for {
		n, err := conn.Read(tmp)
		if n > 0 {
			buf = append(buf, tmp[:n]...)
		}
		if err != nil {
			break
		}
		// Bail once we've seen at least one complete LDAPMessage and the
		// next read would block past the deadline. We just rely on the
		// Read deadline to terminate us in practice.
		if len(buf) > 32768 {
			break
		}
	}
	if len(buf) == 0 {
		return nil
	}
	return parseLDAPSearchResult(buf)
}

// buildLDAPRootDSESearch constructs an LDAPv3 SearchRequest for the root
// DSE with the attributes Active Directory uses to identify itself.
//
// Filter: a "present" filter [7] OCTET STRING "objectClass" matches any
// entry that has an objectClass attribute (every entry does). This is
// the canonical "give me whatever you have" filter.
func buildLDAPRootDSESearch() []byte {
	// Filter [7] OCTET STRING "objectClass"
	filter := berContextPrim(7, []byte("objectClass"))
	// AttributeSelection: SEQUENCE OF LDAPString
	attrs := berSeq(
		concat(
			berOctetString([]byte("dnsHostName")),
			berOctetString([]byte("defaultNamingContext")),
			berOctetString([]byte("domainFunctionality")),
			berOctetString([]byte("forestFunctionality")),
			berOctetString([]byte("ldapServiceName")),
			berOctetString([]byte("serverName")),
		),
	)
	searchBody := concat(
		berOctetString([]byte("")),    // baseObject
		berEnum(0),                    // scope: baseObject
		berEnum(0),                    // derefAliases: neverDerefAliases
		berInt(0),                     // sizeLimit
		berInt(5),                     // timeLimit
		berBool(false),                // typesOnly
		filter,
		attrs,
	)
	searchReq := berAppCons(3, searchBody) // [APPLICATION 3] CONSTRUCTED
	msg := berSeq(concat(berInt(1), searchReq))
	return msg
}

// parseLDAPSearchResult walks the LDAP response stream. It looks for a
// SearchResultEntry [APPLICATION 4] containing PartialAttributeList,
// extracts the attributes we asked for, and returns them. Returns nil
// if no SearchResultEntry was seen.
func parseLDAPSearchResult(buf []byte) *LDAPInfo {
	info := &LDAPInfo{}
	any := false
	rest := buf
	for len(rest) > 0 {
		// Each response is an LDAPMessage SEQUENCE.
		msg, next, err := berRead(rest)
		if err != nil || msg.tag != 0x30 {
			break
		}
		rest = next
		body := msg.value
		// Skip messageID.
		_, body, err = berRead(body)
		if err != nil {
			continue
		}
		op, _, err := berRead(body)
		if err != nil {
			continue
		}
		// SearchResultEntry = [APPLICATION 4] CONSTRUCTED = 0x64.
		if op.tag != 0x64 {
			continue
		}
		any = true
		entry := op.value
		// objectName LDAPDN.
		_, entry, err = berRead(entry)
		if err != nil {
			continue
		}
		// PartialAttributeList SEQUENCE OF SEQUENCE.
		pal, _, err := berRead(entry)
		if err != nil || pal.tag != 0x30 {
			continue
		}
		attrList := pal.value
		for len(attrList) > 0 {
			attr, n, err := berRead(attrList)
			if err != nil {
				break
			}
			attrList = n
			if attr.tag != 0x30 {
				continue
			}
			ab := attr.value
			nameTLV, ab, err := berRead(ab)
			if err != nil || nameTLV.tag != 0x04 {
				continue
			}
			name := string(nameTLV.value)
			vals, _, err := berRead(ab)
			if err != nil || vals.tag != 0x31 { // SET OF
				continue
			}
			vb := vals.value
			var firstVal string
			if len(vb) > 0 {
				v, _, err := berRead(vb)
				if err == nil && v.tag == 0x04 {
					firstVal = string(v.value)
				}
			}
			firstVal = strings.TrimSpace(firstVal)
			if firstVal == "" {
				continue
			}
			switch name {
			case "dnsHostName":
				info.DNSHostName = firstVal
			case "defaultNamingContext":
				info.DefaultNamingContext = firstVal
			case "domainFunctionality":
				info.DomainFunctionality = firstVal
			case "forestFunctionality":
				info.ForestFunctionality = firstVal
			case "ldapServiceName":
				info.LDAPServiceName = firstVal
			case "serverName":
				info.ServerName = firstVal
			}
		}
	}
	if !any {
		return nil
	}
	return info
}

// concat is a tiny convenience helper to flatten variadic byte slices.
func concat(parts ...[]byte) []byte {
	n := 0
	for _, p := range parts {
		n += len(p)
	}
	out := make([]byte, 0, n)
	for _, p := range parts {
		out = append(out, p...)
	}
	return out
}

// berEnum encodes an ASN.1 ENUMERATED. Same wire format as INTEGER but
// different universal tag (0x0A).
func berEnum(v int64) []byte {
	t := berInt(v)
	if len(t) > 0 {
		t[0] = 0x0a
	}
	return t
}

// berBool encodes an ASN.1 BOOLEAN. DER uses 0xFF for true, 0x00 for false.
func berBool(v bool) []byte {
	if v {
		return []byte{0x01, 0x01, 0xff}
	}
	return []byte{0x01, 0x01, 0x00}
}

// berContextPrim returns a primitive context-specific TLV: tag = 0x80|n.
func berContextPrim(n byte, value []byte) []byte {
	return berTLV(0x80|n, value)
}

// berAppCons returns a constructed application-class TLV.
// tag = 0x40 (application) | 0x20 (constructed) | n.
func berAppCons(n byte, value []byte) []byte {
	return berTLV(0x60|n, value)
}
