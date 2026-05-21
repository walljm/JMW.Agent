package discover

import (
	"encoding/binary"
	"errors"
	"net"
	"strings"
	"time"
)

// MDNSInfo is the per-IP mDNS profile collected during a scan.
type MDNSInfo struct {
	Hostname string            `json:"hostname,omitempty"`
	Services []string          `json:"services,omitempty"` // e.g. _ipp._tcp, _airplay._tcp
	TXT      map[string]string `json:"txt,omitempty"`      // merged across instances
}

// rawRecord is one RR collected during a scan, with absolute offsets so
// compression pointers in name fields can still resolve.
type rawRecord struct {
	owner    string
	typ      uint16
	rdataOff int
	rdataLen int
	pkt      []byte
}

// mdnsLookup performs a two-phase mDNS scan and returns a map of IP → info.
// Phase 1: PTR for `_services._dns-sd._udp.local.` to enumerate service types.
// Phase 2: PTR for each discovered service type to harvest SRV/TXT/A.
func mdnsLookup(timeout time.Duration) map[string]MDNSInfo {
	out := map[string]MDNSInfo{}
	mcast := &net.UDPAddr{IP: net.IPv4(224, 0, 0, 251), Port: 5353}

	conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: 0})
	if err != nil {
		return out
	}
	defer conn.Close()

	phase1 := timeout / 3
	if phase1 < 300*time.Millisecond {
		phase1 = 300 * time.Millisecond
	}
	_ = conn.SetReadDeadline(time.Now().Add(phase1))
	if q, err := buildMDNSQuery("_services._dns-sd._udp.local.", 12); err == nil {
		_, _ = conn.WriteTo(q, mcast)
	}

	serviceTypes := map[string]struct{}{
		// seed common types in case nothing answers _services
		"_workstation._tcp.local.": {},
		"_ssh._tcp.local.":         {},
		"_http._tcp.local.":        {},
		"_ipp._tcp.local.":         {},
		"_airplay._tcp.local.":     {},
		"_googlecast._tcp.local.":  {},
		"_smb._tcp.local.":         {},
		"_device-info._tcp.local.": {},
	}
	var records []rawRecord
	buf := make([]byte, 9000)
	for {
		n, _, err := conn.ReadFrom(buf)
		if err != nil {
			break
		}
		pkt := append([]byte(nil), buf[:n]...)
		records = append(records, parseAllRecords(pkt)...)
	}
	// PTRs in answers to _services give us new service types.
	for _, r := range records {
		if r.typ == 12 && r.owner == "_services._dns-sd._udp.local." {
			if name, _, ok := decodeName(r.pkt, r.rdataOff); ok {
				if !strings.HasSuffix(name, ".") {
					name += "."
				}
				serviceTypes[strings.ToLower(name)] = struct{}{}
			}
		}
	}

	phase2 := timeout - phase1
	if phase2 < 500*time.Millisecond {
		phase2 = 500 * time.Millisecond
	}
	_ = conn.SetReadDeadline(time.Now().Add(phase2))
	for st := range serviceTypes {
		if q, err := buildMDNSQuery(st, 12); err == nil {
			_, _ = conn.WriteTo(q, mcast)
		}
	}
	for {
		n, _, err := conn.ReadFrom(buf)
		if err != nil {
			break
		}
		pkt := append([]byte(nil), buf[:n]...)
		records = append(records, parseAllRecords(pkt)...)
	}

	// Aggregate.
	ipByHost := map[string]string{}
	instanceTarget := map[string]string{}
	instanceTXT := map[string]map[string]string{}
	instanceService := map[string]string{}

	for _, r := range records {
		switch r.typ {
		case 1: // A
			if r.rdataLen == 4 {
				d := r.pkt[r.rdataOff : r.rdataOff+4]
				ip := net.IPv4(d[0], d[1], d[2], d[3]).String()
				if _, ok := ipByHost[r.owner]; !ok {
					ipByHost[r.owner] = ip
				}
			}
		case 33: // SRV: priority(2) weight(2) port(2) target(name)
			if r.rdataLen < 7 {
				continue
			}
			if name, _, ok := decodeName(r.pkt, r.rdataOff+6); ok {
				t := strings.ToLower(name)
				if !strings.HasSuffix(t, ".") {
					t += "."
				}
				instanceTarget[r.owner] = t
			}
		case 16: // TXT
			data := r.pkt[r.rdataOff : r.rdataOff+r.rdataLen]
			kv := parseTXT(data)
			if len(kv) > 0 {
				if instanceTXT[r.owner] == nil {
					instanceTXT[r.owner] = map[string]string{}
				}
				for k, v := range kv {
					instanceTXT[r.owner][k] = v
				}
			}
		case 12: // PTR — owner is service type, target is instance
			if name, _, ok := decodeName(r.pkt, r.rdataOff); ok {
				instance := strings.ToLower(name)
				if !strings.HasSuffix(instance, ".") {
					instance += "."
				}
				if r.owner != "_services._dns-sd._udp.local." {
					instanceService[instance] = strings.TrimSuffix(r.owner, ".local.")
				}
			}
		}
	}

	for instance, target := range instanceTarget {
		ip := ipByHost[target]
		if ip == "" {
			continue
		}
		info := out[ip]
		// Hostname preference (best to worst):
		//   1. TXT friendly-name key (e.g. _googlecast._tcp uses "fn")
		//   2. The instance label itself, when it isn't a bare UUID
		//   3. The SRV target hostname (often a UUID for cast/IoT devices)
		friendly := friendlyNameFromTXT(instanceTXT[instance])
		if friendly == "" {
			friendly = friendlyNameFromInstance(instance)
		}
		if friendly != "" && (info.Hostname == "" || looksLikeUUIDHost(info.Hostname)) {
			info.Hostname = friendly
		} else if info.Hostname == "" {
			h := strings.TrimSuffix(target, ".")
			h = strings.TrimSuffix(h, ".local")
			info.Hostname = h
		}
		if svc := instanceService[instance]; svc != "" {
			if !contains(info.Services, svc) {
				info.Services = append(info.Services, svc)
			}
		}
		if txt := instanceTXT[instance]; len(txt) > 0 {
			if info.TXT == nil {
				info.TXT = map[string]string{}
			}
			for k, v := range txt {
				if _, exists := info.TXT[k]; !exists {
					info.TXT[k] = v
				}
			}
		}
		out[ip] = info
	}
	// Catch direct A records for hosts with no SRV pointer.
	for host, ip := range ipByHost {
		if _, ok := out[ip]; ok {
			continue
		}
		h := strings.TrimSuffix(host, ".")
		h = strings.TrimSuffix(h, ".local")
		out[ip] = MDNSInfo{Hostname: h}
	}

	return out
}

// buildMDNSQuery constructs a single-question DNS query packet.
func buildMDNSQuery(name string, qtype uint16) ([]byte, error) {
	b := []byte{0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0}
	enc, err := encodeName(name)
	if err != nil {
		return nil, err
	}
	b = append(b, enc...)
	var qt [2]byte
	binary.BigEndian.PutUint16(qt[:], qtype)
	// qclass = IN (1) with the top bit (QU) set, asking responders to
	// unicast their reply directly to our source port. Without QU, mDNS
	// responses go to multicast 5353 — which we can't bind on macOS
	// (mDNSResponder owns it) and don't bind on other platforms either,
	// so the SRV/TXT records never reach us and `fn`/`n`/`nm` friendly
	// names get lost. With QU set, responders reply to our ephemeral
	// port and the full record set arrives.
	b = append(b, qt[0], qt[1], 0x80, 1)
	return b, nil
}

func encodeName(name string) ([]byte, error) {
	var b []byte
	for _, label := range strings.Split(strings.TrimSuffix(name, "."), ".") {
		if len(label) == 0 || len(label) > 63 {
			return nil, errors.New("bad label")
		}
		b = append(b, byte(len(label)))
		b = append(b, []byte(label)...)
	}
	b = append(b, 0)
	return b, nil
}

// parseAllRecords walks an mDNS response and returns every RR found.
func parseAllRecords(pkt []byte) []rawRecord {
	var out []rawRecord
	if len(pkt) < 12 {
		return out
	}
	qd := int(binary.BigEndian.Uint16(pkt[4:6]))
	an := int(binary.BigEndian.Uint16(pkt[6:8]))
	ns := int(binary.BigEndian.Uint16(pkt[8:10]))
	ar := int(binary.BigEndian.Uint16(pkt[10:12]))
	off := 12
	for i := 0; i < qd && off < len(pkt); i++ {
		_, n, ok := decodeName(pkt, off)
		if !ok {
			return out
		}
		off = n + 4
	}
	total := an + ns + ar
	for i := 0; i < total && off < len(pkt); i++ {
		owner, n, ok := decodeName(pkt, off)
		if !ok {
			return out
		}
		if n+10 > len(pkt) {
			return out
		}
		typ := binary.BigEndian.Uint16(pkt[n : n+2])
		rdlen := int(binary.BigEndian.Uint16(pkt[n+8 : n+10]))
		rdataOff := n + 10
		if rdataOff+rdlen > len(pkt) {
			return out
		}
		off = rdataOff + rdlen
		ownerLower := strings.ToLower(owner)
		if !strings.HasSuffix(ownerLower, ".") {
			ownerLower += "."
		}
		out = append(out, rawRecord{owner: ownerLower, typ: typ, rdataOff: rdataOff, rdataLen: rdlen, pkt: pkt})
	}
	return out
}

// parseTXT decodes TXT RDATA. Each entry is length-prefixed; "key=value" or
// just "key" (treated as bool true).
func parseTXT(data []byte) map[string]string {
	out := map[string]string{}
	for i := 0; i < len(data); {
		ln := int(data[i])
		i++
		if i+ln > len(data) {
			return out
		}
		entry := string(data[i : i+ln])
		i += ln
		if entry == "" {
			continue
		}
		if eq := strings.IndexByte(entry, '='); eq >= 0 {
			out[entry[:eq]] = entry[eq+1:]
		} else {
			out[entry] = "true"
		}
	}
	return out
}

// decodeName reads a DNS-encoded name starting at `off`. Returns the decoded
// name, the offset just past the name, and ok. Handles compression pointers.
func decodeName(pkt []byte, off int) (string, int, bool) {
	var labels []string
	original := off
	jumped := false
	maxJumps := 10
	for {
		if off >= len(pkt) {
			return "", 0, false
		}
		b := pkt[off]
		if b == 0 {
			off++
			break
		}
		if b&0xc0 == 0xc0 {
			if off+1 >= len(pkt) {
				return "", 0, false
			}
			ptr := int(binary.BigEndian.Uint16(pkt[off:off+2]) & 0x3fff)
			if !jumped {
				original = off + 2
			}
			off = ptr
			jumped = true
			maxJumps--
			if maxJumps < 0 {
				return "", 0, false
			}
			continue
		}
		if int(b)+off+1 > len(pkt) {
			return "", 0, false
		}
		labels = append(labels, string(pkt[off+1:off+1+int(b)]))
		off += int(b) + 1
	}
	if !jumped {
		original = off
	}
	return strings.Join(labels, "."), original, true
}

func contains(ss []string, s string) bool {
	for _, x := range ss {
		if x == s {
			return true
		}
	}
	return false
}

// friendlyNameFromTXT picks the user-facing device name out of TXT KV pairs.
// Common keys across Google Cast (`fn`), some IoT vendors (`n`, `nm`), and
// AirPlay-style services (`am`).
func friendlyNameFromTXT(txt map[string]string) string {
	if len(txt) == 0 {
		return ""
	}
	for _, k := range []string{"fn", "n", "nm", "FN"} {
		if v, ok := txt[k]; ok {
			v = strings.TrimSpace(v)
			if v != "" && !looksLikeUUIDHost(v) {
				return v
			}
		}
	}
	return ""
}

// friendlyNameFromInstance returns the first label of an mDNS instance name
// (the part before the service type), e.g. "Living Room" from
// "Living Room._googlecast._tcp.local.". Empty if the label is UUID-shaped or
// otherwise unhelpful.
func friendlyNameFromInstance(instance string) string {
	instance = strings.TrimSuffix(instance, ".")
	// Find the first "._" boundary that starts the service type.
	idx := strings.Index(instance, "._")
	if idx <= 0 {
		return ""
	}
	label := instance[:idx]
	// mDNS escapes spaces as "\ " and dots as "\.".
	label = strings.ReplaceAll(label, `\ `, " ")
	label = strings.ReplaceAll(label, `\.`, ".")
	label = strings.TrimSpace(label)
	if label == "" || looksLikeUUIDHost(label) {
		return ""
	}
	return label
}

// looksLikeUUIDHost reports whether s looks like a bare UUID/hex identifier
// rather than a human-meaningful name. Matches 32-hex strings (with or
// without dashes) and the common cast-device pattern of long hex blobs.
func looksLikeUUIDHost(s string) bool {
	s = strings.TrimSuffix(s, ".local")
	s = strings.TrimSuffix(s, ".")
	bare := strings.ReplaceAll(s, "-", "")
	if len(bare) < 16 {
		return false
	}
	hex := 0
	for _, r := range bare {
		switch {
		case r >= '0' && r <= '9', r >= 'a' && r <= 'f', r >= 'A' && r <= 'F':
			hex++
		default:
			return false
		}
	}
	return hex == len(bare)
}
