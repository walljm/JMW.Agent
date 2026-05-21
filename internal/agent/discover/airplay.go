package discover

import (
	"context"
	"io"
	"net"
	"net/http"
	"regexp"
	"strings"
	"time"
)

// AirPlayInfo is what we can extract from an AirPlay /info or /server-info
// endpoint. The newer AirPlay 2 endpoint replies with a binary plist that
// we don't fully parse — we just scan it for printable strings around the
// well-known keys, which is enough to recover `name` and `model`. AirPlay
// is mostly redundant with mDNS TXT (`am=` model, device-id) so this is a
// fallback for hosts where the QU-bit'd mDNS scan still didn't yield TXT.
type AirPlayInfo struct {
	Name    string `json:"name,omitempty"`
	Model   string `json:"model,omitempty"`
	Version string `json:"version,omitempty"` // Server header
}

var (
	apNameRe  = regexp.MustCompile(`(?:name|deviceName|computerName)\x00*([\x20-\x7e]{1,64})`)
	apModelRe = regexp.MustCompile(`(?:model|deviceModel|am)\x00*([\x20-\x7e]{1,32})`)
)

// airPlayProbe queries the AirPlay /info endpoint. AirPlay listens on TCP
// 7000 (legacy) and 5000 (AirPlay 2). Returns nil if the device isn't an
// AirPlay receiver.
func airPlayProbe(ip string, timeout time.Duration) *AirPlayInfo {
	if ip == "" {
		return nil
	}
	for _, port := range []string{"7000", "5000"} {
		urlStr := "http://" + net.JoinHostPort(ip, port) + "/info"
		ctx, cancel := context.WithTimeout(context.Background(), timeout)
		req, err := http.NewRequestWithContext(ctx, "GET", urlStr, nil)
		if err != nil {
			cancel()
			continue
		}
		// AirPlay 2 expects the User-Agent so it'll respond with the
		// binary-plist info dictionary instead of 403.
		req.Header.Set("User-Agent", "AirPlay/540.31")
		cli := &http.Client{Timeout: timeout, Transport: &http.Transport{DisableKeepAlives: true}}
		resp, err := cli.Do(req)
		if err != nil {
			cancel()
			continue
		}
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 16384))
		_ = resp.Body.Close()
		cancel()
		if resp.StatusCode != http.StatusOK {
			continue
		}
		info := &AirPlayInfo{Version: strings.TrimSpace(resp.Header.Get("Server"))}
		if m := apNameRe.FindSubmatch(body); m != nil {
			info.Name = strings.TrimSpace(string(m[1]))
		}
		if m := apModelRe.FindSubmatch(body); m != nil {
			info.Model = strings.TrimSpace(string(m[1]))
		}
		if info.Name == "" && info.Model == "" && info.Version == "" {
			continue
		}
		return info
	}
	return nil
}
