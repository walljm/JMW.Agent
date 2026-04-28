package discover

import (
	"bufio"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"net"
	"strconv"
	"strings"
	"time"

	"golang.org/x/crypto/ssh"
)

// SSHFingerprint is the SHA256 fingerprint of the server's host key, in
// the OpenSSH "SHA256:<base64>" form. It's a stable identity for the
// device that survives hostname/IP changes — useful for deduping mobile
// laptops that hop subnets.
type SSHFingerprint struct {
	Algorithm   string `json:"algo,omitempty"`        // ssh-rsa | ssh-ed25519 | ecdsa-sha2-nistp256 | ...
	SHA256      string `json:"sha256,omitempty"`      // SHA256:<base64-no-padding>
	BannerShort string `json:"banner_short,omitempty"`
}

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

// sshHostKey runs the SSH transport handshake far enough to capture the
// server's host key, then disconnects. We never authenticate — the
// HostKeyCallback grabs the key during the kex exchange and we abort the
// connection by returning a sentinel error.
//
// The point is the fingerprint, not access. SHA256 of the wire-format
// key bytes (rfc4253 §6.6) is the same fingerprint sshd prints in its
// host-key advertisement and `ssh-keyscan | ssh-keygen -lf` produces.
func sshHostKey(ip string, timeout time.Duration) *SSHFingerprint {
	if ip == "" {
		return nil
	}
	for _, port := range []int{22, 2222} {
		if fp := sshHostKeyFromPort(ip, port, timeout); fp != nil {
			return fp
		}
	}
	return nil
}

var errCaptureDone = errors.New("captured host key")

func sshHostKeyFromPort(ip string, port int, timeout time.Duration) *SSHFingerprint {
	addr := net.JoinHostPort(ip, strconv.Itoa(port))
	dialer := &net.Dialer{Timeout: timeout}
	conn, err := dialer.Dial("tcp", addr)
	if err != nil {
		return nil
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(timeout))

	var captured *SSHFingerprint
	cfg := &ssh.ClientConfig{
		User: "jmw-agent-discover",
		Auth: []ssh.AuthMethod{ssh.Password("")},
		HostKeyCallback: func(hostname string, remote net.Addr, key ssh.PublicKey) error {
			h := sha256.Sum256(key.Marshal())
			captured = &SSHFingerprint{
				Algorithm: key.Type(),
				SHA256:    "SHA256:" + base64.RawStdEncoding.EncodeToString(h[:]),
			}
			// Stop the handshake here — we don't want to continue into
			// auth (and waste cycles negotiating something we'll throw
			// away). Returning an error aborts the handshake; the
			// captured fingerprint is already saved.
			return errCaptureDone
		},
		Timeout: timeout,
	}
	_, _, _, _ = ssh.NewClientConn(conn, addr, cfg)
	return captured
}
