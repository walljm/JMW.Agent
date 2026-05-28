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
	sightingKinds []string // "container", etc. from agent discovery payloads
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
	}
	obRows.Close()

	return sig, nil
}

// classify applies heuristics against the gathered signals and returns a
// device kind. Returns "" if no rule matched confidently (caller leaves the
// existing value in place).
func classify(s *deriveSignals) string {
	// Agent-reported container sighting — strongest single signal.
	for _, k := range s.sightingKinds {
		if k == "container" {
			return KindContainer
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
			strings.Contains(svc, "_raop._tcp"):
			return KindStreamer
		case strings.Contains(svc, "_smb._tcp"),
			strings.Contains(svc, "_afpovertcp._tcp"),
			strings.Contains(svc, "_nfs._tcp"):
			// File-sharing services lean NAS but can also be a workstation;
			// hand off to vendor/hostname rules below before deciding.
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
			return KindServer // a managed VM is server-class by default
		default:
			// Agent present, chassis unknown — assume server (an agent is
			// typically installed on always-on infrastructure).
			return KindServer
		}
	}

	// Hostname pattern rules.
	for _, h := range s.hostnames {
		switch {
		case strings.HasPrefix(h, "router"),
			strings.HasPrefix(h, "gateway"),
			strings.HasPrefix(h, "gw-"),
			strings.HasPrefix(h, "edge-"):
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
			strings.HasPrefix(h, "camera"):
			return KindCamera
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

	return ""
}

// classifyByVendor maps common vendor name fragments to a kind. Order matters
// only for vendors that overlap (e.g. Apple makes both phones and laptops);
// in those cases we return the more conservative kind and rely on hostname /
// chassis_type to refine.
func classifyByVendor(vendor string) string {
	switch {
	// Networking
	case contains(vendor, "ubiquiti", "mikrotik", "tp-link", "tplink",
		"netgear", "d-link", "dlink", "linksys", "asus router",
		"juniper", "arista", "cisco systems", "cisco-linksys",
		"fortinet", "palo alto"):
		return KindRouter
	case contains(vendor, "ruckus", "aruba", "meraki", "extreme networks"):
		return KindAP

	// Printers
	case contains(vendor, "hewlett packard", "hewlett-packard", "hp inc",
		"brother", "epson", "canon", "lexmark", "kyocera",
		"ricoh", "xerox", "konica minolta"):
		return KindPrinter

	// NAS / storage
	case contains(vendor, "synology", "qnap", "asustor", "drobo", "buffalo"):
		return KindNAS

	// IoT / smart home
	case contains(vendor, "espressif", "nest labs", "ring llc", "amazon technologies",
		"google llc", "philips lighting", "tuya smart"):
		return KindIoT

	// Cameras
	case contains(vendor, "hikvision", "dahua", "axis communications", "uniview"):
		return KindCamera

	// Mobile / consumer
	case contains(vendor, "samsung electronics"):
		return KindMobile
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
