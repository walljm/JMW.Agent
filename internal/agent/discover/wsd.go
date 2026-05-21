package discover

import (
	"encoding/xml"
	"fmt"
	"net"
	"regexp"
	"strings"
	"time"
)

// WSDInfo is the per-IP WS-Discovery profile collected during a scan.
type WSDInfo struct {
	// Types is the list of QName types advertised by the device, e.g.
	// "wsdp:Device" or "pnpx:NetworkInfrastructureType".
	Types []string
	// Scopes is the raw scope string from the ProbeMatch (often a
	// space-separated list of URIs encoding manufacturer / model).
	Scopes string
	// FriendlyName is best-effort: pulled from the Scopes URI when one of
	// them encodes a hostname (Windows hosts often advertise
	// `onvif://www.onvif.org/name/<hostname>` or similar). Returns "" when
	// nothing usable is present.
	FriendlyName string
}

// wsdLookup sends a WS-Discovery Probe (SOAP-over-UDP multicast) and
// returns a per-IP map of replies. Hits are common for: Windows hosts
// (PnP-X Device), network printers, and ONVIF cameras. Routers and most
// IoT do not respond.
func wsdLookup(timeout time.Duration) map[string]WSDInfo {
	out := map[string]WSDInfo{}
	mcast := &net.UDPAddr{IP: net.IPv4(239, 255, 255, 250), Port: 3702}

	conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: 0})
	if err != nil {
		return out
	}
	defer conn.Close()

	listenFor := timeout
	if listenFor < 1*time.Second {
		listenFor = 1 * time.Second
	}
	_ = conn.SetReadDeadline(time.Now().Add(listenFor))

	probe := buildWSDProbe()
	_, _ = conn.WriteTo(probe, mcast)

	buf := make([]byte, 8192)
	for {
		n, addr, err := conn.ReadFromUDP(buf)
		if err != nil {
			break
		}
		info, ok := parseWSDProbeMatch(buf[:n])
		if !ok {
			continue
		}
		ip := addr.IP.String()
		// Merge with any existing info for this IP (some hosts answer twice).
		cur := out[ip]
		if cur.FriendlyName == "" {
			cur.FriendlyName = info.FriendlyName
		}
		if cur.Scopes == "" {
			cur.Scopes = info.Scopes
		}
		for _, t := range info.Types {
			if !contains(cur.Types, t) {
				cur.Types = append(cur.Types, t)
			}
		}
		out[ip] = cur
	}
	return out
}

// buildWSDProbe constructs a WS-Discovery Probe SOAP envelope. We probe
// for the generic `wsdp:Device` type, which matches every WS-Discovery
// responder — printers, Windows hosts, ONVIF cameras, etc.
func buildWSDProbe() []byte {
	const tpl = `<?xml version="1.0" encoding="UTF-8"?>` +
		`<soap:Envelope ` +
		`xmlns:soap="http://www.w3.org/2003/05/soap-envelope" ` +
		`xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing" ` +
		`xmlns:wsd="http://schemas.xmlsoap.org/ws/2005/04/discovery" ` +
		`xmlns:wsdp="http://schemas.xmlsoap.org/ws/2006/02/devprof">` +
		`<soap:Header>` +
		`<wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>` +
		`<wsa:MessageID>urn:uuid:%s</wsa:MessageID>` +
		`<wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>` +
		`</soap:Header>` +
		`<soap:Body>` +
		`<wsd:Probe><wsd:Types>wsdp:Device</wsd:Types></wsd:Probe>` +
		`</soap:Body>` +
		`</soap:Envelope>`
	// MessageID just needs to be unique-ish; nanosecond timestamp is fine.
	id := fmt.Sprintf("%016x-jmw-agent", time.Now().UnixNano())
	return []byte(fmt.Sprintf(tpl, id))
}

// parseWSDProbeMatch extracts Types/Scopes from a WS-Discovery
// ProbeMatches SOAP envelope and derives a friendly name from the Scopes
// URI list when possible.
//
// We use encoding/xml with a permissive struct because the responder may
// use any combination of namespaces / prefixes — we match on local-name.
func parseWSDProbeMatch(pkt []byte) (WSDInfo, bool) {
	type probeMatch struct {
		Types  string `xml:"Types"`
		Scopes string `xml:"Scopes"`
	}
	type probeMatches struct {
		Match []probeMatch `xml:"ProbeMatch"`
	}
	type body struct {
		Matches probeMatches `xml:"ProbeMatches"`
	}
	type env struct {
		Body body `xml:"Body"`
	}
	var e env
	dec := xml.NewDecoder(strings.NewReader(string(pkt)))
	// Tolerate unknown namespaces.
	dec.Strict = false
	if err := dec.Decode(&e); err != nil {
		return WSDInfo{}, false
	}
	if len(e.Body.Matches.Match) == 0 {
		return WSDInfo{}, false
	}
	m := e.Body.Matches.Match[0]
	info := WSDInfo{
		Types:  splitFields(m.Types),
		Scopes: m.Scopes,
	}
	info.FriendlyName = nameFromWSDScopes(m.Scopes)
	return info, true
}

func splitFields(s string) []string {
	parts := strings.Fields(s)
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		if p != "" {
			out = append(out, p)
		}
	}
	return out
}

// scopeNameRe matches `*/name/<hostname>` paths in WSD scope URIs (as
// emitted by Windows PnP-X and ONVIF). The hostname is the last path
// segment and stops at the next `/` or whitespace.
var scopeNameRe = regexp.MustCompile(`(?i)/name/([A-Za-z0-9._-]+)`)

// nameFromWSDScopes scans space-separated scope URIs for a recognizable
// hostname-bearing pattern. Returns "" when nothing is found.
func nameFromWSDScopes(scopes string) string {
	for _, s := range strings.Fields(scopes) {
		if m := scopeNameRe.FindStringSubmatch(s); m != nil {
			return strings.ToLower(m[1])
		}
	}
	return ""
}
