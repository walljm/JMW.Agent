// Package hostfs provides a configurable filesystem-root prefix so the agent
// can read host system files when running inside a container with the host
// filesystem bind-mounted. The prefix is read once from the JMW_HOST_ROOT
// environment variable.
//
// When JMW_HOST_ROOT is unset (the native install case), Path returns the
// input path unchanged, so all collector code continues to read /proc, /sys,
// and /etc directly.
//
// When JMW_HOST_ROOT is set (e.g. to "/host"), Path("/proc/meminfo") returns
// "/host/proc/meminfo". The host filesystem is expected to be bind-mounted
// at that root via `docker run -v /:/host:ro` (or equivalent).
//
// Reported paths (e.g. mountpoints sent to the server) must NOT be prefixed —
// the prefix is for *reading* host files, not for *displaying* host paths.
package hostfs

import (
	"os"
	"strings"
)

const envVar = "JMW_HOST_ROOT"

var root = strings.TrimRight(os.Getenv(envVar), "/")

// Root returns the configured host-root prefix, or "" when running natively.
func Root() string { return root }

// Active reports whether the agent is reading through a host-root prefix
// (i.e. running in container-observes-host mode).
func Active() bool { return root != "" }

// Path returns p prefixed with the host root when configured. Inputs that do
// not begin with "/" are returned unchanged.
func Path(p string) string {
	if root == "" || p == "" || p[0] != '/' {
		return p
	}
	return root + p
}

// Hostname returns the host's hostname, preferring ${JMW_HOST_ROOT}/etc/hostname
// when running in container-observes-host mode. Falls back to os.Hostname().
// Returns "unknown" if both fail.
func Hostname() string {
	if Active() {
		if b, err := os.ReadFile(Path("/etc/hostname")); err == nil {
			if h := strings.TrimSpace(string(b)); h != "" {
				return h
			}
		}
	}
	h, err := os.Hostname()
	if err != nil || h == "" {
		return "unknown"
	}
	return h
}
