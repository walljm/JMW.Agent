// Hassio Supervisor API collector. Cross-platform — Supervisor is reachable
// over a Linux-only socket, but we keep the file build-tag-free since it's
// pure HTTP and Go's net/http is portable.
package collect

import (
	"context"
	"encoding/json"
	"net/http"
	"os"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// supervisorBaseURL is the in-cluster DNS name Supervisor exposes to add-ons.
// Only resolvable inside the add-on container.
const supervisorBaseURL = "http://supervisor"

// collectHassio queries the Home Assistant Supervisor API. Returns nil when:
//   - SUPERVISOR_TOKEN env var is unset (we're not running as an HA add-on), or
//   - any Supervisor call fails (Supervisor is the source of truth — partial
//     data is worse than no data here).
//
// Add-ons must declare `homeassistant_api: true` in config.yaml to use the
// /core/info endpoint, and `hassio_api: true` (default for local add-ons) for
// /supervisor/info and /addons. We declare both implicitly via full_access.
func collectHassio(ctx context.Context) *proto.HassioInfo {
	token := os.Getenv("SUPERVISOR_TOKEN")
	if token == "" {
		return nil
	}
	client := &http.Client{Timeout: 5 * time.Second}

	info := &proto.HassioInfo{}

	// /supervisor/info → supervisor version, channel, arch, machine
	var sup struct {
		Data struct {
			Version string `json:"version"`
			Channel string `json:"channel"`
			Arch    string `json:"arch"`
			Machine string `json:"machine"`
		} `json:"data"`
	}
	if err := supervisorGet(ctx, client, token, "/supervisor/info", &sup); err != nil {
		return nil
	}
	info.SupervisorVersion = sup.Data.Version
	info.Channel = sup.Data.Channel
	info.Arch = sup.Data.Arch
	info.Machine = sup.Data.Machine

	// /core/info → HA Core version. Failure here is non-fatal (add-on may not
	// have homeassistant_api enabled).
	var core struct {
		Data struct {
			Version string `json:"version"`
		} `json:"data"`
	}
	if err := supervisorGet(ctx, client, token, "/core/info", &core); err == nil {
		info.CoreVersion = core.Data.Version
	}

	// /os/info → HAOS version (HA Green firmware version).
	var osi struct {
		Data struct {
			Version string `json:"version"`
		} `json:"data"`
	}
	if err := supervisorGet(ctx, client, token, "/os/info", &osi); err == nil {
		info.OSVersion = osi.Data.Version
	}

	// /host/info → host hostname, OS pretty name, kernel, chassis, boot time.
	// This is what gives us the *host's* OS info instead of the add-on
	// container's /etc/os-release. Failure is non-fatal.
	var host struct {
		Data struct {
			Hostname        string `json:"hostname"`
			OperatingSystem string `json:"operating_system"`
			Kernel          string `json:"kernel"`
			Chassis         string `json:"chassis"`
			// boot_timestamp comes from systemd's BootTimestamp via dbus,
			// which is microseconds since epoch — NOT seconds.
			BootTimestamp int64 `json:"boot_timestamp"`
		} `json:"data"`
	}
	if err := supervisorGet(ctx, client, token, "/host/info", &host); err == nil {
		info.Hostname = host.Data.Hostname
		info.HostOS = host.Data.OperatingSystem
		info.HostKernel = host.Data.Kernel
		info.Chassis = host.Data.Chassis
		if host.Data.BootTimestamp > 0 {
			info.BootTime = time.UnixMicro(host.Data.BootTimestamp).UTC()
		}
	}

	// /addons → installed add-on list. The endpoint returns the union of
	// installed and store add-ons; filter to "installed".
	var addons struct {
		Data struct {
			Addons []struct {
				Slug      string `json:"slug"`
				Name      string `json:"name"`
				Version   string `json:"version"`
				State     string `json:"state"`
				Installed string `json:"installed"`
				Update    bool   `json:"update_available"`
			} `json:"addons"`
		} `json:"data"`
	}
	if err := supervisorGet(ctx, client, token, "/addons", &addons); err == nil {
		for _, a := range addons.Data.Addons {
			// Supervisor uses "installed" presence (a version string) to mean
			// installed; store-only entries have it empty.
			if a.Installed == "" {
				continue
			}
			info.Addons = append(info.Addons, proto.HassioAddon{
				Slug:    a.Slug,
				Name:    a.Name,
				Version: a.Installed,
				State:   a.State,
				Update:  a.Update,
			})
		}
	}

	return info
}

func supervisorGet(ctx context.Context, c *http.Client, token, path string, into any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, supervisorBaseURL+path, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/json")
	resp, err := c.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return &supervisorErr{Status: resp.StatusCode, Path: path}
	}
	return json.NewDecoder(resp.Body).Decode(into)
}

type supervisorErr struct {
	Status int
	Path   string
}

func (e *supervisorErr) Error() string {
	return "supervisor " + e.Path + ": http " + http.StatusText(e.Status)
}
