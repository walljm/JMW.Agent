package pipeline

import (
	"context"
	"database/sql"
	"encoding/json"
	"strings"

	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/oui"
)

// Device kind constants. These align with the values the legacy classifier
// produced so the UI's existing rendering keeps working.
const (
	KindUnknown     = "unknown"
	KindServer      = "server"
	KindWorkstation = "workstation"
	KindLaptop      = "laptop"
	KindRouter      = "router"
	KindSwitch      = "switch"
	KindAP          = "access-point"
	KindPrinter     = "printer"
	KindTV          = "tv"
	KindStreamer    = "streamer"
	KindMobile      = "mobile"
	KindIoT         = "iot"
	KindNAS         = "nas"
	KindCamera      = "camera"
	KindContainer   = "container"
	KindHypervisor  = "hypervisor"
)

// deriveSignals is the set of facts the classifier looks at for a single
// hardware record. All fields are lowercased on assignment.
type deriveSignals struct {
	hasAgent      bool
	chassisType   string // from inventory: server | desktop | laptop | vm | ...
	systemVendor  string // hardware.system_vendor (DMI)
	macs          []string
	hostnames     []string
	mdnsServices  []string // _ipp._tcp, _googlecast._tcp, ...
	sightingKinds []string // "container", "google-cast", etc. from agent discovery payloads
	probeKeys     []string // "ipp", "airplay", "ssh_fp", etc. — confirmed protocol probe hits
}

// DeriveDeviceKindForHardware classifies a single hardware record based on
// the observation signals attached to its interfaces and updates the
// `hardware.device_kind` column. It is safe to call repeatedly; the result
// is deterministic for a given set of signals.
//
// The classification rules are heuristics, ordered from most specific to
// most general. Earlier matches win.
func DeriveDeviceKindForHardware(ctx context.Context, st *store.Store, hardwareID string) error {
	sig, err := gatherSignals(ctx, st, hardwareID)
	if err != nil {
		return err
	}
	kind := classify(sig)
	if kind == "" {
		return nil // no confident classification; leave as-is
	}
	current, err := st.GetHardwareDeviceKind(ctx, hardwareID)
	if err != nil {
		return err
	}
	if current == kind {
		return nil
	}
	return st.SetHardwareDeviceKind(ctx, hardwareID, kind)
}

func gatherSignals(ctx context.Context, st *store.Store, hardwareID string) (*deriveSignals, error) {
	sig := &deriveSignals{}

	// Hardware-level fields: chassis_type, system_vendor.
	hw, err := st.GetHardware(ctx, hardwareID)
	if err != nil {
		return nil, err
	}
	sig.chassisType = strings.ToLower(hw.ChassisType)
	sig.systemVendor = strings.ToLower(hw.SystemVendor)

	// System: does any system on this hardware have an agent? (agent_id != '')
	rows, err := st.DB.QueryContext(ctx,
		`SELECT COALESCE(agent_id, '') FROM systems WHERE hardware_id = ?`, hardwareID)
	if err != nil {
		return nil, err
	}
	for rows.Next() {
		var agentID string
		if err := rows.Scan(&agentID); err != nil {
			rows.Close()
			return nil, err
		}
		if agentID != "" {
			sig.hasAgent = true
		}
	}
	rows.Close()

	// Interfaces: collect MACs so we can look up vendors. Also collect interface
	// IDs so we can pull observations + hostname aliases.
	ifRows, err := st.DB.QueryContext(ctx,
		`SELECT id, mac FROM interfaces WHERE hardware_id = ?`, hardwareID)
	if err != nil {
		return nil, err
	}
	var ifaceIDs []string
	for ifRows.Next() {
		var id, mac string
		if err := ifRows.Scan(&id, &mac); err != nil {
			ifRows.Close()
			return nil, err
		}
		ifaceIDs = append(ifaceIDs, id)
		if mac != "" {
			sig.macs = append(sig.macs, mac)
		}
	}
	ifRows.Close()
	if len(ifaceIDs) == 0 {
		return sig, nil
	}

	placeholders := strings.Repeat("?,", len(ifaceIDs)-1) + "?"
	args := make([]any, len(ifaceIDs))
	for i, id := range ifaceIDs {
		args[i] = id
	}

	// Hostname aliases.
	haRows, err := st.DB.QueryContext(ctx,
		`SELECT hostname FROM hostname_aliases WHERE interface_id IN (`+placeholders+`)`, args...)
	if err != nil {
		return nil, err
	}
	for haRows.Next() {
		var h string
		if err := haRows.Scan(&h); err != nil {
			haRows.Close()
			return nil, err
		}
		if h != "" {
			sig.hostnames = append(sig.hostnames, strings.ToLower(h))
		}
	}
	haRows.Close()

	// Observations — recent discovery raw_json carries Services + Kind.
	obRows, err := st.DB.QueryContext(ctx,
		`SELECT obs_type, raw_json FROM observations
		 WHERE interface_id IN (`+placeholders+`)
		 ORDER BY observed_at DESC LIMIT 200`, args...)
	if err != nil {
		return nil, err
	}
	for obRows.Next() {
		var obsType string
		var raw sql.NullString
		if err := obRows.Scan(&obsType, &raw); err != nil {
			obRows.Close()
			return nil, err
		}
		if !raw.Valid || raw.String == "" {
			continue
		}
		// discovery + dhcp-lease payloads have a flat shape; we just pull the
		// fields we care about with a permissive map.
		var m map[string]any
		if err := json.Unmarshal([]byte(raw.String), &m); err != nil {
			continue
		}
		if kind, ok := m["kind"].(string); ok && kind != "" {
			sig.sightingKinds = append(sig.sightingKinds, strings.ToLower(kind))
		}
		if svcs, ok := m["services"].([]any); ok {
			for _, s := range svcs {
				if str, ok := s.(string); ok && str != "" {
					sig.mdnsServices = append(sig.mdnsServices, strings.ToLower(str))
				}
			}
		}
		if probes, ok := m["probes"].(map[string]any); ok {
			for k := range probes {
				if k != "" {
					sig.probeKeys = append(sig.probeKeys, strings.ToLower(k))
				}
			}
		}
	}
	obRows.Close()

	return sig, nil
}

// classify applies heuristics against the gathered signals and returns a
// device kind. Returns "" if no rule matched confidently (caller leaves the
// existing value in place).
func classify(s *deriveSignals) string {
	// Sighting kinds set by the agent — all protocol-detected kinds, not just container.
	for _, k := range s.sightingKinds {
		switch k {
		case "container":
			return KindContainer
		case "google-cast":
			return KindStreamer
		case "printer":
			return KindPrinter
		case "roku":
			return KindStreamer
		case "domain-controller":
			return KindServer
		}
	}

	// Probe-based — confirmed protocol responses. ssh_fp is a weak signal held
	// back until after hostname rules so a NAS named "nas-01" with SSH isn't
	// misclassified as a server.
	for _, k := range s.probeKeys {
		switch k {
		case "ipp":
			return KindPrinter
		case "airplay", "eureka", "roku":
			return KindStreamer
		case "ldap":
			return KindServer
		}
	}

	// mDNS service fingerprints — very high confidence when present.
	for _, svc := range s.mdnsServices {
		switch {
		case strings.Contains(svc, "_ipp._tcp"),
			strings.Contains(svc, "_printer._tcp"),
			strings.Contains(svc, "_pdl-datastream._tcp"),
			strings.Contains(svc, "_ipps._tcp"):
			return KindPrinter
		case strings.Contains(svc, "_googlecast._tcp"),
			strings.Contains(svc, "_airplay._tcp"),
			strings.Contains(svc, "_raop._tcp"),
			strings.Contains(svc, "_appletv-v2._tcp"):
			return KindStreamer
		case strings.Contains(svc, "_appletv._tcp"):
			return KindTV
		case strings.Contains(svc, "_companion-link._tcp"):
			// iOS devices advertise this; Macs do not.
			return KindMobile
		case strings.Contains(svc, "_ssh._tcp"),
			strings.Contains(svc, "_sftp-ssh._tcp"):
			return KindServer
		case strings.Contains(svc, "_smb._tcp"),
			strings.Contains(svc, "_afpovertcp._tcp"),
			strings.Contains(svc, "_nfs._tcp"):
			// File-sharing can be a NAS or a workstation; defer to later rules.
		case strings.Contains(svc, "_hap._tcp"):
			return KindIoT
		case strings.Contains(svc, "_rfb._tcp"),
			strings.Contains(svc, "_workstation._tcp"):
			return KindWorkstation
		}
	}

	// Agent-installed system: classify by chassis type the agent reported.
	if s.hasAgent {
		switch s.chassisType {
		case "laptop", "notebook", "portable":
			return KindLaptop
		case "desktop", "tower", "mini-tower", "all-in-one":
			return KindWorkstation
		case "server", "rack-mount", "blade":
			return KindServer
		case "vm", "virtual", "container":
			return KindServer
		default:
			return KindServer
		}
	}

	// Hostname pattern rules.
	for _, h := range s.hostnames {
		switch {
		case strings.HasPrefix(h, "router"),
			strings.HasPrefix(h, "gateway"),
			strings.HasPrefix(h, "gw-"),
			strings.HasPrefix(h, "edge-"),
			strings.HasPrefix(h, "fw-"),
			strings.HasPrefix(h, "firewall"),
			strings.Contains(h, "onhub"),
			strings.Contains(h, "google-wifi"):
			return KindRouter
		case strings.HasPrefix(h, "switch"),
			strings.HasPrefix(h, "sw-"):
			return KindSwitch
		case strings.HasPrefix(h, "ap-"),
			strings.HasPrefix(h, "wap-"),
			strings.Contains(h, "-ap-"):
			return KindAP
		case strings.HasPrefix(h, "nas"),
			strings.HasPrefix(h, "synology"),
			strings.HasPrefix(h, "asustor"),
			strings.HasPrefix(h, "qnap"):
			return KindNAS
		case strings.HasPrefix(h, "printer"),
			strings.Contains(h, "-printer"):
			return KindPrinter
		case strings.HasPrefix(h, "cam-"),
			strings.HasPrefix(h, "camera"),
			strings.HasPrefix(h, "ring-"):
			return KindCamera
		case strings.Contains(h, "iphone"),
			strings.Contains(h, "ipad"),
			strings.Contains(h, "phone"),
			strings.HasPrefix(h, "pixel-"),
			strings.HasPrefix(h, "galaxy-"):
			return KindMobile
		case strings.Contains(h, "macbook"),
			strings.HasPrefix(h, "laptop-"):
			return KindLaptop
		case strings.Contains(h, "imac"),
			strings.HasPrefix(h, "desktop-"),
			strings.HasPrefix(h, "pc-"):
			return KindWorkstation
		case strings.Contains(h, "esxi"),
			strings.Contains(h, "proxmox"),
			strings.HasPrefix(h, "pve-"),
			strings.Contains(h, "hyperv"):
			return KindHypervisor
		case strings.Contains(h, "chromecast"),
			strings.Contains(h, "firetv"),
			strings.HasPrefix(h, "roku-"):
			return KindStreamer
		case strings.Contains(h, "appletv"),
			strings.Contains(h, "apple-tv"):
			return KindTV
		case strings.HasPrefix(h, "echo-"),
			strings.HasPrefix(h, "shelly-"),
			strings.HasPrefix(h, "tasmota-"),
			strings.HasPrefix(h, "esp-"),
			strings.HasPrefix(h, "esp32-"),
			strings.HasPrefix(h, "hue-"):
			return KindIoT
		}
	}

	// Vendor-based fallback. OUI lookup uses the first MAC that resolves.
	vendor := ""
	for _, mac := range s.macs {
		if v := oui.Lookup(mac); v != "" {
			vendor = strings.ToLower(v)
			break
		}
	}
	if vendor == "" {
		vendor = s.systemVendor
	}
	if vendor != "" {
		if k := classifyByVendor(vendor); k != "" {
			return k
		}
	}

	// ssh_fp as last resort: SSH confirmed but nothing else matched.
	for _, k := range s.probeKeys {
		if k == "ssh_fp" {
			return KindServer
		}
	}

	return ""
}

// classifyByVendor maps common vendor name fragments to a kind. Order matters
// for overlapping vendors — more specific cases must come first.
func classifyByVendor(vendor string) string {
	switch {
	// Servers — check HPE before generic "hewlett packard" so HP printers don't match here.
	case contains(vendor, "hewlett packard enterprise", "super micro", "supermicro",
		"ibm", "fujitsu technology", "hpe"):
		return KindServer

	// Networking — routers/firewalls/switches.
	case contains(vendor, "ubiquiti", "mikrotik", "tp-link", "tplink",
		"netgear", "d-link", "dlink", "linksys", "asus router",
		"juniper", "arista", "cisco systems", "cisco-linksys",
		"fortinet", "palo alto", "eero", "zyxel", "sophos",
		"watchguard", "sonicwall", "cradlepoint", "pepwave",
		"brocade", "calix", "adtran"):
		return KindRouter
	case contains(vendor, "ruckus", "aruba", "meraki", "extreme networks",
		"cambium", "aerohive", "mist systems"):
		return KindAP

	// Printers.
	case contains(vendor, "hewlett packard", "hewlett-packard", "hp inc",
		"brother", "epson", "canon", "lexmark", "kyocera",
		"ricoh", "xerox", "konica minolta", "oki data", "oki electric",
		"zebra technologies", "datalogic", "toshiba tec"):
		return KindPrinter

	// NAS / storage.
	case contains(vendor, "synology", "qnap", "asustor", "drobo", "buffalo",
		"terramaster", "western digital"):
		return KindNAS

	// IoT / smart home.
	case contains(vendor, "espressif", "nest labs", "ring llc", "amazon technologies",
		"philips lighting", "tuya smart", "raspberry pi",
		"shenzhen lumi", "particle industries", "nordic semiconductor"):
		return KindIoT

	// Cameras.
	case contains(vendor, "hikvision", "dahua", "axis communications", "uniview",
		"hanwha", "bosch security systems", "pelco", "vivotek", "amcrest"):
		return KindCamera

	// TVs.
	case contains(vendor, "lg electronics", "vizio", "tcl technology", "hisense",
		"sony", "roku"):
		return KindTV

	// Mobile — Samsung makes phones and TVs; phones dominate in ARP tables.
	case contains(vendor, "samsung electronics", "motorola mobility",
		"oneplus technology", "xiaomi", "huawei", "oppo"):
		return KindMobile

	// Workstations.
	case contains(vendor, "dell inc", "lenovo", "acer incorporated", "micro-star",
		"asustek computer", "gigabyte", "intel corporate"):
		return KindWorkstation
	}
	return ""
}

func contains(haystack string, needles ...string) bool {
	for _, n := range needles {
		if strings.Contains(haystack, n) {
			return true
		}
	}
	return false
}
