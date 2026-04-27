package collect

import (
	"bufio"
	"context"
	"net"
	"os"
	"os/exec"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/agent/hostfs"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// Inventory builds a full Inventory snapshot. Includes packages only if includePackages is true.
func Inventory(ctx context.Context, includePackages bool) proto.Inventory {
	inv := proto.Inventory{
		CollectedAt: time.Now().UTC(),
		Hardware:    collectHardware(ctx),
		OS:          collectOS(ctx),
		Disks:       collectDisks(ctx),
		Network:     collectNetwork(ctx),
		Routes:      collectRoutes(ctx),
		Users:       collectUsers(ctx),
		Listening:   collectListening(ctx),
		Processes:   collectProcesses(ctx),
		Reboots:     collectReboots(ctx),
		Docker:      collectDocker(ctx),
	}
	if includePackages {
		if pkgs := collectPackages(ctx); pkgs != nil {
			inv.Packages = pkgs
		}
	}
	return inv
}

// --- Cross-platform helpers + shared collectors ---

func collectNetwork(ctx context.Context) proto.NetworkInfo {
	out := proto.NetworkInfo{}
	ifs, err := net.Interfaces()
	if err == nil {
		for _, ifi := range ifs {
			ni := proto.NetInterface{
				Name:       ifi.Name,
				MAC:        ifi.HardwareAddr.String(),
				MTU:        ifi.MTU,
				IsUp:       ifi.Flags&net.FlagUp != 0,
				IsLoopback: ifi.Flags&net.FlagLoopback != 0,
			}
			if ni.IsLoopback {
				ni.Type = "loopback"
			} else if ifi.Flags&net.FlagPointToPoint != 0 {
				ni.Type = "ptp"
			}
			addrs, _ := ifi.Addrs()
			for _, a := range addrs {
				ipn, ok := a.(*net.IPNet)
				if !ok {
					continue
				}
				if ipn.IP.To4() != nil {
					ni.IPv4 = append(ni.IPv4, ipn.String())
				} else if ipn.IP.To16() != nil {
					ni.IPv6 = append(ni.IPv6, ipn.String())
				}
			}
			ifaceEnrich(ifi.Name, &ni)
			out.Interfaces = append(out.Interfaces, ni)
		}
	}
	out.DNSServers, out.DNSSearch = readResolvConf()
	out.Gateway4, out.Gateway6 = defaultGateways(ctx)
	return out
}

func readResolvConf() (servers, search []string) {
	f, err := os.Open(hostfs.Path("/etc/resolv.conf"))
	if err != nil {
		return nil, nil
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, ";") {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}
		switch fields[0] {
		case "nameserver":
			servers = append(servers, fields[1])
		case "search", "domain":
			search = append(search, fields[1:]...)
		}
	}
	return dedupe(servers), dedupe(search)
}

func dedupe(in []string) []string {
	if len(in) == 0 {
		return in
	}
	seen := make(map[string]struct{}, len(in))
	out := make([]string, 0, len(in))
	for _, s := range in {
		if _, ok := seen[s]; ok {
			continue
		}
		seen[s] = struct{}{}
		out = append(out, s)
	}
	return out
}

func runCmd(ctx context.Context, name string, args ...string) (string, error) {
	c, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	out, err := exec.CommandContext(c, name, args...).Output()
	if err != nil {
		return "", err
	}
	return string(out), nil
}

func trimNL(s string) string { return strings.TrimRight(s, "\r\n ") }

func readFirstLine(path string) string {
	f, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line != "" {
			return line
		}
	}
	return ""
}

// parseHumanSize parses "12.3MB", "1GB", "456kB" into bytes (best effort).
func parseHumanSize(s string) uint64 {
	s = strings.TrimSpace(s)
	if s == "" {
		return 0
	}
	s = strings.TrimSuffix(strings.TrimSuffix(s, "B"), "b")
	mult := float64(1)
	switch {
	case strings.HasSuffix(s, "k") || strings.HasSuffix(s, "K"):
		mult = 1 << 10
		s = s[:len(s)-1]
	case strings.HasSuffix(s, "M"):
		mult = 1 << 20
		s = s[:len(s)-1]
	case strings.HasSuffix(s, "G"):
		mult = 1 << 30
		s = s[:len(s)-1]
	case strings.HasSuffix(s, "T"):
		mult = 1 << 40
		s = s[:len(s)-1]
	}
	v, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return 0
	}
	return uint64(v * mult)
}

// runtimeOS returns runtime.GOOS as the OS family name.
func runtimeOS() string { return runtime.GOOS }
