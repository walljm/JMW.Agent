package discover

import (
	"context"
	"crypto/tls"
	"io"
	"net"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// httpBanner probes common admin web ports for an IP and returns the most
// useful name it can find:
//   - <title> stripped of common suffixes ("- Login", " | Admin"), or
//   - the Server: header value when no usable title is present.
//
// Returns "" if no port answers or both signals are empty/unhelpful. This
// is intentionally a *weak* hostname source: page titles can be anything.
func httpBanner(ip string, timeout time.Duration) string {
	if ip == "" {
		return ""
	}
	type probe struct {
		scheme string
		port   int
	}
	probes := []probe{{"http", 80}, {"https", 443}, {"http", 8080}, {"https", 8443}}

	cli := &http.Client{
		Timeout: timeout,
		Transport: &http.Transport{
			TLSClientConfig:   &tls.Config{InsecureSkipVerify: true}, // device certs are usually self-signed
			DisableKeepAlives: true,
		},
		// Don't follow redirects — the redirect target may be irrelevant.
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}

	for _, p := range probes {
		urlStr := p.scheme + "://" + net.JoinHostPort(ip, strconv.Itoa(p.port)) + "/"
		ctx, cancel := context.WithTimeout(context.Background(), timeout)
		req, err := http.NewRequestWithContext(ctx, "GET", urlStr, nil)
		if err != nil {
			cancel()
			continue
		}
		req.Header.Set("User-Agent", "jmw-agent/discover")
		resp, err := cli.Do(req)
		if err != nil {
			cancel()
			continue
		}
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 32768))
		_ = resp.Body.Close()
		cancel()

		if name := extractHTMLTitle(body); name != "" {
			return name
		}
		if srv := strings.TrimSpace(resp.Header.Get("Server")); srv != "" {
			return srv
		}
	}
	return ""
}

var titleRe = regexp.MustCompile(`(?is)<title[^>]*>(.*?)</title>`)

// extractHTMLTitle pulls the inner text of the first <title>, collapses
// whitespace, and trims well-known login/admin suffixes that add noise
// without identifying the device.
func extractHTMLTitle(body []byte) string {
	m := titleRe.FindSubmatch(body)
	if m == nil {
		return ""
	}
	t := strings.TrimSpace(string(m[1]))
	// Collapse whitespace runs.
	t = strings.Join(strings.Fields(t), " ")
	if t == "" {
		return ""
	}
	// Drop common pretty-suffixes ("EdgeOS - Login" → "EdgeOS").
	for _, suf := range []string{" - Login", " - Sign in", " | Login", " | Admin", " - Admin", " Login"} {
		if i := strings.LastIndex(strings.ToLower(t), strings.ToLower(suf)); i > 0 {
			t = t[:i]
		}
	}
	t = strings.TrimSpace(t)
	if len(t) > 64 {
		t = t[:64]
	}
	return t
}
