package discover

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// EurekaInfo is the subset of fields we extract from a Google Cast / Nest /
// Google WiFi device's Eureka endpoint. Many of these devices reply on
// :8443 (https), :8008 (http), or :8080 (http) when they otherwise advertise
// nothing on mDNS — so this is often the only way to identify a Google
// mesh AP or a Nest speaker that's gone quiet.
type EurekaInfo struct {
	Name      string `json:"name,omitempty"`
	DeviceID  string `json:"device_id,omitempty"`
	Model     string `json:"model,omitempty"`
	Build     string `json:"build,omitempty"`
	MAC       string `json:"mac,omitempty"`
	Locale    string `json:"locale,omitempty"`
	HasUpdate bool   `json:"has_update,omitempty"`
}

// eurekaProbe queries the device's eureka_info endpoint and returns parsed
// fields plus a friendly hostname guess. Returns nil on any failure; this
// is best-effort and silent when the device isn't a Google one.
func eurekaProbe(ip string, timeout time.Duration) *EurekaInfo {
	if ip == "" {
		return nil
	}
	cli := &http.Client{
		Timeout: timeout,
		Transport: &http.Transport{
			TLSClientConfig:   &tls.Config{InsecureSkipVerify: true},
			DisableKeepAlives: true,
		},
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	type probe struct {
		scheme string
		port   int
	}
	// Order matters: 8443 is the modern default, 8008 is the older Cast
	// endpoint, 8080 is sometimes used by Google WiFi mesh nodes.
	for _, p := range []probe{{"https", 8443}, {"http", 8008}, {"http", 8080}} {
		urlStr := p.scheme + "://" + net.JoinHostPort(ip, strconv.Itoa(p.port)) + "/setup/eureka_info?options=detail"
		ctx, cancel := context.WithTimeout(context.Background(), timeout)
		req, err := http.NewRequestWithContext(ctx, "GET", urlStr, nil)
		if err != nil {
			cancel()
			continue
		}
		resp, err := cli.Do(req)
		if err != nil {
			cancel()
			continue
		}
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 65536))
		_ = resp.Body.Close()
		cancel()
		if resp.StatusCode/100 != 2 || len(body) == 0 {
			continue
		}
		// Eureka returns flat JSON. Decode into a generic map and pull
		// out the fields we care about — different generations use
		// slightly different keys (e.g. "ssdp_udn" vs "device_id").
		var raw map[string]any
		if err := json.Unmarshal(body, &raw); err != nil {
			continue
		}
		info := &EurekaInfo{}
		if v := strField(raw, "name"); v != "" {
			info.Name = v
		} else if v := strField(raw, "device_name"); v != "" {
			info.Name = v
		}
		if v := strField(raw, "device_id"); v != "" {
			info.DeviceID = v
		} else if v := strField(raw, "ssdp_udn"); v != "" {
			info.DeviceID = v
		}
		if v := strField(raw, "model_name"); v != "" {
			info.Model = v
		} else if v := strField(raw, "device_model"); v != "" {
			info.Model = v
		}
		if v := strField(raw, "build_version"); v != "" {
			info.Build = v
		} else if v := strField(raw, "cast_build_revision"); v != "" {
			info.Build = v
		}
		if v := strField(raw, "mac_address"); v != "" {
			info.MAC = strings.ToLower(strings.ReplaceAll(v, "-", ":"))
		}
		if v := strField(raw, "locale"); v != "" {
			info.Locale = v
		}
		if b, ok := raw["has_update"].(bool); ok {
			info.HasUpdate = b
		}
		if info.Name == "" && info.Model == "" && info.DeviceID == "" {
			continue
		}
		return info
	}
	return nil
}

// strField returns the string value at key in m, or "" if missing or not
// a string.
func strField(m map[string]any, key string) string {
	if v, ok := m[key]; ok {
		if s, ok := v.(string); ok {
			return strings.TrimSpace(s)
		}
	}
	return ""
}
