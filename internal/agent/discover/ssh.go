package discover

import (
	"bufio"
	"net"
	"strconv"
	"strings"
	"time"
)

// sshBanner connects to common SSH ports and reads the server's
// identification string per RFC 4253 §4.2:
//
//	SSH-protoversion-softwareversion SP comments CR LF
//
// The server sends this as the first line, before any key exchange or
// auth. We read it, disconnect, and return the trimmed banner.
//
// The banner alone often identifies the device (e.g.
// "OpenSSH_for_Windows_8.1", "ROSSSH" for MikroTik, "Cisco-1.25",
// "OpenSSH_8.4 Ubuntu-..."). It's not a hostname per se, but it's a
// useful "what is this thing?" signal — recorded as a hostname source so
// the server can surface it in the device detail view.
func sshBanner(ip string, timeout time.Duration) string {
	if ip == "" {
		return ""
	}
	for _, port := range []int{22, 2222} {
		if b := sshBannerFromPort(ip, port, timeout); b != "" {
			return b
		}
	}
	return ""
}

func sshBannerFromPort(ip string, port int, timeout time.Duration) string {
	conn, err := net.DialTimeout("tcp", net.JoinHostPort(ip, strconv.Itoa(port)), timeout)
	if err != nil {
		return ""
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(timeout))

	// RFC 4253 allows the server to send arbitrary text lines before the
	// SSH-* identification, but they must each end with CR LF and be < 255
	// bytes. Scan up to a few lines looking for the SSH- prefix.
	r := bufio.NewReaderSize(conn, 1024)
	for i := 0; i < 5; i++ {
		line, err := r.ReadString('\n')
		if err != nil && line == "" {
			return ""
		}
		line = strings.TrimRight(line, "\r\n")
		if strings.HasPrefix(line, "SSH-") {
			// Trim the SSH-2.0- prefix to keep the useful part. Keeps the
			// stored value short and consistent across hosts.
			rest := strings.TrimPrefix(line, "SSH-")
			if i := strings.IndexByte(rest, '-'); i >= 0 {
				rest = rest[i+1:]
			}
			rest = strings.TrimSpace(rest)
			if rest == "" {
				return ""
			}
			if len(rest) > 64 {
				rest = rest[:64]
			}
			return rest
		}
	}
	return ""
}
