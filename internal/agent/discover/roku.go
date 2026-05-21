package discover

import (
	"context"
	"encoding/xml"
	"io"
	"net"
	"net/http"
	"strings"
	"time"
)

// RokuInfo is the subset of fields we extract from a Roku device's ECP
// endpoint (`/query/device-info` on port 8060). Rokus advertise via mDNS
// `_roku._tcp` but the ECP endpoint returns much richer detail.
type RokuInfo struct {
	Name        string `json:"name,omitempty"`
	Model       string `json:"model,omitempty"`
	ModelNumber string `json:"model_number,omitempty"`
	Serial      string `json:"serial,omitempty"`
	Software    string `json:"software,omitempty"`
}

// rokuQuery is the XML schema returned by /query/device-info.
type rokuQuery struct {
	XMLName            xml.Name `xml:"device-info"`
	UserDeviceName     string   `xml:"user-device-name"`
	FriendlyDeviceName string   `xml:"friendly-device-name"`
	ModelName          string   `xml:"model-name"`
	ModelNumber        string   `xml:"model-number"`
	SerialNumber       string   `xml:"serial-number"`
	SoftwareVersion    string   `xml:"software-version"`
	SoftwareBuild      string   `xml:"software-build"`
}

// rokuProbe asks the Roku ECP service for device-info. Returns nil if the
// device isn't a Roku or doesn't reply within the timeout.
func rokuProbe(ip string, timeout time.Duration) *RokuInfo {
	if ip == "" {
		return nil
	}
	urlStr := "http://" + net.JoinHostPort(ip, "8060") + "/query/device-info"
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, "GET", urlStr, nil)
	if err != nil {
		return nil
	}
	cli := &http.Client{Timeout: timeout, Transport: &http.Transport{DisableKeepAlives: true}}
	resp, err := cli.Do(req)
	if err != nil {
		return nil
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil
	}
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 65536))
	var q rokuQuery
	if err := xml.Unmarshal(body, &q); err != nil {
		return nil
	}
	info := &RokuInfo{
		Model:       strings.TrimSpace(q.ModelName),
		ModelNumber: strings.TrimSpace(q.ModelNumber),
		Serial:      strings.TrimSpace(q.SerialNumber),
	}
	// Prefer the user-set name over the marketing friendly name — that's
	// what the user sees on the remote and recognises.
	switch {
	case strings.TrimSpace(q.UserDeviceName) != "":
		info.Name = strings.TrimSpace(q.UserDeviceName)
	case strings.TrimSpace(q.FriendlyDeviceName) != "":
		info.Name = strings.TrimSpace(q.FriendlyDeviceName)
	}
	if strings.TrimSpace(q.SoftwareVersion) != "" {
		v := q.SoftwareVersion
		if b := strings.TrimSpace(q.SoftwareBuild); b != "" {
			v += " (" + b + ")"
		}
		info.Software = v
	}
	if info.Name == "" && info.Model == "" && info.Serial == "" {
		return nil
	}
	return info
}
