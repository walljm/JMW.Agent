package discover

import (
	"bufio"
	"context"
	"encoding/xml"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// SSDPInfo is the per-IP SSDP/UPnP profile collected during a scan.
type SSDPInfo struct {
	FriendlyName string // <friendlyName> from device description XML
	Manufacturer string // <manufacturer>
	ModelName    string // <modelName>
	Server       string // SSDP `SERVER:` header (e.g. "Linux/3.x UPnP/1.0 EdgeOS/2.0")
}

// ssdpLookup sends an SSDP M-SEARCH multicast and returns a per-IP map of
// device info. Best-effort: hosts that don't answer SSDP simply don't
// appear. The total wall time is bounded by `timeout`.
func ssdpLookup(timeout time.Duration) map[string]SSDPInfo {
	out := map[string]SSDPInfo{}
	mcast := &net.UDPAddr{IP: net.IPv4(239, 255, 255, 250), Port: 1900}

	conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: 0})
	if err != nil {
		return out
	}
	defer conn.Close()

	listenFor := timeout
	if listenFor < 800*time.Millisecond {
		listenFor = 800 * time.Millisecond
	}
	_ = conn.SetReadDeadline(time.Now().Add(listenFor))

	// Two M-SEARCH variants: ssdp:all (everything) and a generic UPnP root
	// device, which some narrow responders prefer.
	for _, st := range []string{"ssdp:all", "upnp:rootdevice"} {
		msg := "M-SEARCH * HTTP/1.1\r\n" +
			"HOST: 239.255.255.250:1900\r\n" +
			"MAN: \"ssdp:discover\"\r\n" +
			"MX: 2\r\n" +
			"ST: " + st + "\r\n\r\n"
		_, _ = conn.WriteTo([]byte(msg), mcast)
	}

	type rawHit struct {
		ip       string
		location string
		server   string
	}
	var hits []rawHit
	seen := map[string]bool{} // dedupe by location URL

	buf := make([]byte, 4096)
	for {
		n, addr, err := conn.ReadFromUDP(buf)
		if err != nil {
			break
		}
		headers := parseSSDPHeaders(buf[:n])
		loc := headers["location"]
		if loc == "" || seen[loc] {
			// Even without LOCATION we can still record the SERVER banner.
			ip := addr.IP.String()
			if cur, ok := out[ip]; !ok && headers["server"] != "" {
				cur.Server = headers["server"]
				out[ip] = cur
			}
			continue
		}
		seen[loc] = true
		hits = append(hits, rawHit{ip: addr.IP.String(), location: loc, server: headers["server"]})
	}

	// Fetch each LOCATION URL to extract friendlyName/manufacturer/modelName.
	httpCli := &http.Client{Timeout: 1500 * time.Millisecond}
	for _, h := range hits {
		info := out[h.ip]
		if info.Server == "" {
			info.Server = h.server
		}
		if !isSafeUPnPURL(h.location) {
			slog.Warn("ssdp: skipping unsafe LOCATION URL (SSRF prevention)", "location", h.location, "src_ip", h.ip)
			out[h.ip] = info
			continue
		}
		if friendly, mfr, model := fetchUPnPDescription(httpCli, h.location); friendly != "" || mfr != "" || model != "" {
			if info.FriendlyName == "" {
				info.FriendlyName = friendly
			}
			if info.Manufacturer == "" {
				info.Manufacturer = mfr
			}
			if info.ModelName == "" {
				info.ModelName = model
			}
		}
		// If LOCATION host differs from the source IP, also key by that host's
		// resolved IP — some SSDP responders advertise from one interface but
		// host the description on another.
		out[h.ip] = info
		if u, err := url.Parse(h.location); err == nil {
			host := u.Hostname()
			if ip := net.ParseIP(host); ip != nil && ip.String() != h.ip {
				secondary := out[ip.String()]
				if secondary.FriendlyName == "" {
					secondary.FriendlyName = info.FriendlyName
				}
				if secondary.Manufacturer == "" {
					secondary.Manufacturer = info.Manufacturer
				}
				if secondary.ModelName == "" {
					secondary.ModelName = info.ModelName
				}
				if secondary.Server == "" {
					secondary.Server = info.Server
				}
				out[ip.String()] = secondary
			}
		}
	}
	return out
}

// parseSSDPHeaders parses the headers of an SSDP HTTP-over-UDP response.
// Header names are lowercased on output so callers can use a stable key.
func parseSSDPHeaders(pkt []byte) map[string]string {
	out := map[string]string{}
	r := bufio.NewReader(strings.NewReader(string(pkt)))
	// Skip status line.
	if _, err := r.ReadString('\n'); err != nil {
		return out
	}
	for {
		line, err := r.ReadString('\n')
		if err != nil && line == "" {
			return out
		}
		line = strings.TrimRight(line, "\r\n")
		if line == "" {
			return out
		}
		idx := strings.IndexByte(line, ':')
		if idx <= 0 {
			continue
		}
		key := strings.ToLower(strings.TrimSpace(line[:idx]))
		val := strings.TrimSpace(line[idx+1:])
		out[key] = val
		if err == io.EOF {
			return out
		}
	}
}

// isSafeUPnPURL validates a LOCATION URL from an SSDP response to prevent
// Server-Side Request Forgery (SSRF). It allows http/https to normal private
// (RFC1918) addresses but rejects loopback, link-local metadata endpoints
// (169.254.169.254), and IPv6 link-local ranges (fe80::/10).
func isSafeUPnPURL(raw string) bool {
	u, err := url.Parse(raw)
	if err != nil {
		return false
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return false
	}
	host := u.Hostname()
	if host == "" {
		return false
	}
	ip := net.ParseIP(host)
	if ip == nil {
		// Hostname — resolve to IP before checking.
		addrs, err := net.LookupIP(host)
		if err != nil || len(addrs) == 0 {
			return false
		}
		ip = addrs[0]
	}
	if ip.IsLoopback() {
		return false
	}
	// Block cloud metadata endpoint (AWS/GCP/Azure): 169.254.169.254
	if ip.Equal(net.ParseIP("169.254.169.254")) {
		return false
	}
	// Block IPv6 link-local: fe80::/10
	fe80 := net.IPNet{
		IP:   net.ParseIP("fe80::"),
		Mask: net.CIDRMask(10, 128),
	}
	if fe80.Contains(ip) {
		return false
	}
	return true
}

// fetchUPnPDescription fetches a UPnP device description XML and extracts
// friendlyName, manufacturer, and modelName.
func fetchUPnPDescription(cli *http.Client, urlStr string) (friendly, manufacturer, model string) {
	ctx, cancel := context.WithTimeout(context.Background(), 1500*time.Millisecond)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, "GET", urlStr, nil)
	if err != nil {
		return "", "", ""
	}
	resp, err := cli.Do(req)
	if err != nil {
		return "", "", ""
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 65536))
	if err != nil {
		return "", "", ""
	}
	var doc struct {
		Device struct {
			FriendlyName string `xml:"friendlyName"`
			Manufacturer string `xml:"manufacturer"`
			ModelName    string `xml:"modelName"`
		} `xml:"device"`
	}
	if err := xml.Unmarshal(body, &doc); err != nil {
		return "", "", ""
	}
	return strings.TrimSpace(doc.Device.FriendlyName),
		strings.TrimSpace(doc.Device.Manufacturer),
		strings.TrimSpace(doc.Device.ModelName)
}
