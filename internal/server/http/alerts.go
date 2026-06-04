package httpsrv

import (
	"encoding/json"
	"log/slog"
	"net"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/go-chi/chi/v5"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/server/terrain"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

func (s *Server) alertsList(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	rules, _ := s.Store.ListAlertRules(ctx)
	firings, _ := s.Store.ListFirings(ctx, 50)
	channels, _ := s.Store.ListChannels(ctx)
	agents, _ := s.Store.ListAgents(ctx, store.AgentStatusApproved)
	agentDeviceIDs, _ := s.Store.AgentPrimaryDeviceIDs(ctx)
	csrf := s.ensureCSRF(w, r)

	agentMap := map[string]string{}
	for _, a := range agents {
		agentMap[a.ID] = a.Hostname
	}
	ruleMap := map[int64]string{}
	for _, ru := range rules {
		ruleMap[ru.ID] = ru.Name
	}
	channelMap := map[int64]string{}
	for _, ch := range channels {
		channelMap[ch.ID] = ch.Name
	}

	s.render(w, r, "alerts.html", map[string]any{
		"CSRFToken":      csrf,
		"Title":          "Alerts",
		"Active":         "alerts",
		"Rules":          rules,
		"Firings":        firings,
		"Channels":       channels,
		"Agents":         agents,
		"AgentMap":       agentMap,
		"AgentDeviceIDs": agentDeviceIDs,
		"RuleMap":        ruleMap,
		"ChannelMap":     channelMap,
	})
}

func (s *Server) alertCreate(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	threshold, _ := strconv.ParseFloat(r.FormValue("threshold"), 64)
	dur, _ := strconv.Atoi(r.FormValue("duration_seconds"))
	if dur <= 0 {
		dur = 60
	}

	// Accept either the v2 schema (metric_kind + metric_path) directly or the
	// legacy `metric` shorthand. When only the legacy value is supplied, derive
	// the v2 fields so the runtime dispatcher (FetchMetric) can serve the rule.
	metric := strings.TrimSpace(r.FormValue("metric"))
	metricKind := strings.TrimSpace(r.FormValue("metric_kind"))
	metricPath := strings.TrimSpace(r.FormValue("metric_path"))
	if metricKind == "" {
		metricKind, metricPath = legacyMetricToV2(metric)
	}

	rule := &store.AlertRule{
		Name:            strings.TrimSpace(r.FormValue("name")),
		Enabled:         true,
		Metric:          metric,
		MetricKind:      metricKind,
		MetricPath:      metricPath,
		Op:              r.FormValue("op"),
		Threshold:       threshold,
		DurationSeconds: dur,
		TargetKind:      "agent",
		TargetID:        r.FormValue("target_id"),
		Severity:        r.FormValue("severity"),
	}
	if rule.TargetID == "" {
		rule.TargetKind = "all"
	}
	if rule.Severity == "" {
		rule.Severity = store.SeverityWarning
	}
	if cid := r.FormValue("channel_id"); cid != "" {
		if id, err := strconv.ParseInt(cid, 10, 64); err == nil && id > 0 {
			rule.ChannelID = &id
		}
	}
	if rule.Name == "" || rule.Op == "" || rule.MetricKind == "" {
		http.Error(w, "name, metric, op required", http.StatusBadRequest)
		return
	}
	if err := s.Store.CreateAlertRule(r.Context(), rule); err != nil {
		slog.Error("create alert rule failed", "handler", "alertCreate", "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

// legacyMetricToV2 maps the legacy `metric` shorthand to v2 (kind, path).
// Returns ("", "") if the legacy name is unknown.
func legacyMetricToV2(metric string) (kind, path string) {
	switch metric {
	case "cpu_pct", "mem_pct", "load_1", "load_5", "load_15":
		return "numeric_snapshot", metric
	case "offline_minutes", "offline":
		return "offline", ""
	case "disk_pct":
		return "disk_usage", ""
	default:
		return "", ""
	}
}

func (s *Server) alertDelete(w http.ResponseWriter, r *http.Request) {
	id, _ := strconv.ParseInt(chi.URLParam(r, "id"), 10, 64)
	if err := s.Store.DeleteAlertRule(r.Context(), id); err != nil {
		slog.Error("delete alert rule failed", "handler", "alertDelete", "id", id, "err", err)
		http.Error(w, "internal error", http.StatusBadRequest)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

func (s *Server) channelCreate(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	kind := r.FormValue("kind")
	cfg := map[string]any{}
	switch kind {
	case "webhook":
		cfg["url"] = r.FormValue("url")
	case "email":
		cfg["host"] = r.FormValue("host")
		cfg["port"], _ = strconv.Atoi(r.FormValue("port"))
		cfg["username"] = r.FormValue("username")
		cfg["password"] = r.FormValue("password")
		cfg["from"] = r.FormValue("from")
		cfg["to"] = r.FormValue("to")
		cfg["tls"] = r.FormValue("tls") == "on"
	default:
		http.Error(w, "unknown channel kind", http.StatusBadRequest)
		return
	}
	ch := &store.NotificationChannel{
		Name:    r.FormValue("name"),
		Kind:    kind,
		Config:  cfg,
		Enabled: true,
	}
	if ch.Name == "" {
		http.Error(w, "name required", http.StatusBadRequest)
		return
	}
	if err := s.Store.CreateChannel(r.Context(), ch); err != nil {
		slog.Error("create channel failed", "handler", "channelCreate", "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

func (s *Server) channelDelete(w http.ResponseWriter, r *http.Request) {
	id, _ := strconv.ParseInt(chi.URLParam(r, "id"), 10, 64)
	if err := s.Store.DeleteChannel(r.Context(), id); err != nil {
		slog.Error("delete channel failed", "handler", "channelDelete", "id", id, "err", err)
		http.Error(w, "internal error", http.StatusBadRequest)
		return
	}
	http.Redirect(w, r, "/alerts", http.StatusSeeOther)
}

func (s *Server) devicesList(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	networkID := r.URL.Query().Get("network")

	// Determine which devices to show based on network filter.
	var devices []*store.Device
	var err error
	switch networkID {
	case "", "monitored":
		// Default: show only devices on monitored networks.
		// If no networks are monitored yet, fall back to showing all devices
		// (graceful transition for existing installs).
		monCount, _ := s.Store.MonitoredNetworkCount(ctx)
		if monCount > 0 {
			devices, err = s.Store.ListDevicesOnMonitoredNetworks(ctx)
		} else {
			devices, err = s.Store.ListDevices(ctx)
		}
		networkID = "monitored"
	case "all":
		devices, err = s.Store.ListDevices(ctx)
	case "unclassified":
		devices, err = s.Store.ListUnclassifiedDevices(ctx)
	default:
		// Specific network selected.
		devices, err = s.Store.ListDevicesOnNetwork(ctx, networkID)
	}
	if err != nil {
		devices = nil
	}

	agents, _ := s.Store.ListAgents(ctx, "")
	names := make(map[string]string, len(agents))
	for _, a := range agents {
		names[a.ID] = a.Hostname
	}
	agentDeviceIDs, _ := s.Store.AgentPrimaryDeviceIDs(ctx)
	groups := groupDevices(devices)
	tagsByID, _ := s.Store.ListTagsForTargets(ctx, store.TagTargetDevice)
	// Aggregate tags + descriptions across each group's member device rows so
	// the list view reflects the whole logical machine, not just the primary.
	groupTags := make(map[string][]string, len(groups))
	groupDesc := make(map[string]string, len(groups))
	for _, g := range groups {
		key := g.Primary.ID
		seen := make(map[string]bool)
		var agg []string
		for _, m := range g.Members {
			for _, t := range tagsByID[m.ID] {
				if !seen[t] {
					seen[t] = true
					agg = append(agg, t)
				}
			}
			if groupDesc[key] == "" && strings.TrimSpace(m.Notes) != "" {
				groupDesc[key] = m.Notes
			}
		}
		sort.Strings(agg)
		groupTags[key] = agg
	}

	// Load networks for the selector dropdown.
	networks, _ := s.Store.ListNetworks(ctx, "")

	s.render(w, r, "devices.html", map[string]any{
		"Title":          "Devices",
		"Active":         "devices",
		"Groups":         groups,
		"AgentNames":     names,
		"AgentDeviceIDs": agentDeviceIDs,
		"GroupTags":      groupTags,
		"GroupDesc":      groupDesc,
		"Networks":       networks,
		"NetworkFilter":  networkID,
	})
}

// DeviceGroup is a UI-only aggregation of one or more Device rows that share
// a device_group_id (or a single ungrouped Device row treated as its own
// group). The Primary row drives the list-row identity and link target.
type DeviceGroup struct {
	GroupID    string // empty if ungrouped (single-row group)
	Primary    *store.Device
	Members    []*store.Device
	IPs        []string // distinct, IPv4 first
	MACs       []string // distinct
	ExtraIPs   int      // len(IPs) - 1, clamped at 0; for "+N" badge
	ExtraMACs  int      // len(MACs) - 1, clamped at 0
	LastSeenAt time.Time
}

func groupDevices(devices []*store.Device) []*DeviceGroup {
	byGroup := make(map[string][]*store.Device)
	keys := make([]string, 0)
	for _, d := range devices {
		k := d.GroupID
		if k == "" {
			k = "_ungrouped:" + d.ID // unique per-row, never collides
		}
		if _, ok := byGroup[k]; !ok {
			keys = append(keys, k)
		}
		byGroup[k] = append(byGroup[k], d)
	}
	out := make([]*DeviceGroup, 0, len(keys))
	for _, k := range keys {
		members := byGroup[k]
		// Primary: prefer rows with non-empty IP; lowest IPv4 wins, MAC tiebreak.
		sort.SliceStable(members, func(i, j int) bool {
			ii, ji := members[i].IP, members[j].IP
			switch {
			case ii != "" && ji == "":
				return true
			case ii == "" && ji != "":
				return false
			}
			if c := compareIPStr(ii, ji); c != 0 {
				return c < 0
			}
			return members[i].MAC < members[j].MAC
		})
		g := &DeviceGroup{Primary: members[0], Members: members}
		if !strings.HasPrefix(k, "_ungrouped:") {
			g.GroupID = k
		}
		seenIP := make(map[string]bool)
		seenMAC := make(map[string]bool)
		for _, m := range members {
			if m.IP != "" && !seenIP[m.IP] {
				seenIP[m.IP] = true
				g.IPs = append(g.IPs, m.IP)
			}
			if m.MAC != "" && !seenMAC[m.MAC] {
				seenMAC[m.MAC] = true
				g.MACs = append(g.MACs, m.MAC)
			}
			if m.LastSeenAt.After(g.LastSeenAt) {
				g.LastSeenAt = m.LastSeenAt
			}
		}
		if n := len(g.IPs) - 1; n > 0 {
			g.ExtraIPs = n
		}
		if n := len(g.MACs) - 1; n > 0 {
			g.ExtraMACs = n
		}
		out = append(out, g)
	}
	// Order groups by primary IP for stable display.
	sort.SliceStable(out, func(i, j int) bool {
		return compareIPStr(out[i].Primary.IP, out[j].Primary.IP) < 0
	})
	return out
}

// compareIPStr sorts numerically; empties last; IPv4 before IPv6.
func compareIPStr(a, b string) int {
	ipa, ipb := net.ParseIP(a), net.ParseIP(b)
	switch {
	case ipa == nil && ipb == nil:
		return 0
	case ipa == nil:
		return 1
	case ipb == nil:
		return -1
	}
	a4, b4 := ipa.To4(), ipb.To4()
	switch {
	case a4 != nil && b4 == nil:
		return -1
	case a4 == nil && b4 != nil:
		return 1
	case a4 != nil && b4 != nil:
		for i := 0; i < 4; i++ {
			if a4[i] != b4[i] {
				if a4[i] < b4[i] {
					return -1
				}
				return 1
			}
		}
		return 0
	}
	a16, b16 := ipa.To16(), ipb.To16()
	for i := 0; i < 16; i++ {
		if a16[i] != b16[i] {
			if a16[i] < b16[i] {
				return -1
			}
			return 1
		}
	}
	return 0
}

// MDNSProfile mirrors the JSON blob stored in devices.services_json.
// Despite the name (kept for backwards-compat), it now carries the full
// per-device probe payload, not just mDNS records.
type MDNSProfile struct {
	Hostname string            `json:"hostname,omitempty"`
	Services []string          `json:"services,omitempty"`
	TXT      map[string]string `json:"txt,omitempty"`
	// Per-protocol probe payloads. Each is an opaque flat map of strings
	// rendered as a labelled section on the device detail page.
	Eureka  map[string]string `json:"eureka,omitempty"`
	IPP     map[string]string `json:"ipp,omitempty"`
	Roku    map[string]string `json:"roku,omitempty"`
	AirPlay map[string]string `json:"airplay,omitempty"`
	LDAP    map[string]string `json:"ldap,omitempty"`
	SSHFP   map[string]string `json:"ssh_fp,omitempty"`
	DHCP    map[string]string `json:"dhcp,omitempty"`
	// Probes carries any future/unknown probe results sent by newer agents.
	Probes map[string]map[string]string `json:"probes,omitempty"`
}

func (s *Server) deviceDetail(w http.ResponseWriter, r *http.Request) {
	id := strings.ToLower(chi.URLParam(r, "id"))
	d, err := s.Store.GetDevice(r.Context(), id)
	if err != nil || d == nil {
		http.NotFound(w, r)
		return
	}
	// Load every sibling row in this device's group (or just this row when
	// ungrouped), so the page reflects the whole logical machine and not
	// just one NIC.
	members, _ := s.Store.ListGroupMembers(r.Context(), d.GroupID, d.ID)
	if len(members) == 0 {
		members = []*store.Device{d}
	}
	memberIDs := make([]string, 0, len(members))
	macs := make([]string, 0, len(members))
	ips := make([]string, 0, len(members))
	seenIP := make(map[string]bool)
	for _, m := range members {
		memberIDs = append(memberIDs, m.ID)
		if m.MAC != "" {
			macs = append(macs, m.MAC)
		}
		if m.IP != "" && !seenIP[m.IP] {
			seenIP[m.IP] = true
			ips = append(ips, m.IP)
		}
	}
	sightings, _ := s.Store.ListSightingsForDevices(r.Context(), memberIDs, 200)
	aliases, _ := s.Store.ListHostnamesForDevices(r.Context(), memberIDs)
	agents, _ := s.Store.ListAgents(r.Context(), "")
	names := make(map[string]string, len(agents))
	for _, a := range agents {
		names[a.ID] = a.Hostname
	}
	agentDeviceIDs, _ := s.Store.AgentPrimaryDeviceIDs(r.Context())
	var profile *MDNSProfile
	if d.ServicesJSON != "" {
		var p MDNSProfile
		if err := json.Unmarshal([]byte(d.ServicesJSON), &p); err == nil {
			profile = &p
		}
	}
	managedHostname := ""
	if d.AgentID != "" {
		managedHostname = names[d.AgentID]
	}
	tags, _ := s.Store.ListTagsForTarget(r.Context(), store.TagTargetDevice, d.ID)
	csrf := s.ensureCSRF(w, r)

	// DNS activity: cross-reference this device's IPs with the terrain
	// poller's TopClients list. The upstream DNS server only ranks its
	// top-N busiest clients, so a "not listed" result means low/no
	// query volume relative to other clients, not necessarily zero.
	dnsActivity := buildDNSActivity(s.Terrain.Status(), ips)

	// Networks this device has been seen on.
	deviceNetworks, _ := s.Store.NetworksForDevice(r.Context(), memberIDs)
	allDevices, _ := s.Store.ListDevices(r.Context())
	mergeCandidates := make([]*store.Device, 0, len(allDevices))
	for _, candidate := range allDevices {
		if candidate.GroupID == d.GroupID {
			continue
		}
		mergeCandidates = append(mergeCandidates, candidate)
	}

	// If this device is one of our managed agents, additionally load
	// agent metadata, latest metrics, and parsed inventory. The merged
	// detail page renders all known data — discovery + reported — in one
	// place, regardless of the source.
	var (
		agent     *store.Agent
		latest    *proto.MetricSnapshot
		inventory *proto.Inventory
		agentTags []string
	)
	if d.AgentID != "" {
		if a, err := s.Store.GetAgent(r.Context(), d.AgentID); err == nil && a != nil {
			agent = a
			latest, _ = s.Store.LatestSnapshot(r.Context(), d.AgentID)
			invJSON, _, _ := s.Store.GetAgentInventory(r.Context(), d.AgentID)
			if invJSON != "" {
				var parsed proto.Inventory
				if err := json.Unmarshal([]byte(invJSON), &parsed); err == nil {
					inventory = &parsed
				}
			}
			agentTags, _ = s.Store.ListTagsForTarget(r.Context(), store.TagTargetAgent, d.AgentID)
		}
	}

	// Merge tags from both surfaces (device + agent) for display. The edit
	// form writes to whichever surface owns the entity (agent if agent,
	// device otherwise).
	mergedTags := mergeTags(tags, agentTags)
	editTagsCSV := strings.Join(tags, ", ")
	editNotes := d.Notes
	if agent != nil {
		// Authoritative edit surface is the agent record.
		editTagsCSV = strings.Join(agentTags, ", ")
		editNotes = agent.Notes
	}

	s.render(w, r, "device_detail.html", map[string]any{
		"CSRFToken":       csrf,
		"Title":           "Device " + d.Hostname,
		"Active":          "devices",
		"Device":          d,
		"Members":         members,
		"AllMACs":         macs,
		"AllIPs":          ips,
		"Sightings":       sightings,
		"Aliases":         aliases,
		"AgentNames":      names,
		"AgentDeviceIDs":  agentDeviceIDs,
		"MDNS":            profile,
		"ManagedHostname": managedHostname,
		"Tags":            mergedTags,
		"TagsCSV":         editTagsCSV,
		"EditNotes":       editNotes,
		"DNSActivity":     dnsActivity,
		"Networks":        deviceNetworks,
		"MergeCandidates": mergeCandidates,
		"Agent":           agent,
		"Latest":          latest,
		"Inventory":       inventory,
		"HasInventory":    inventory != nil,
	})
}

// mergeTags returns the union of two tag slices, preserving the order of the
// first slice for already-present tags and appending new ones from the second.
func mergeTags(a, b []string) []string {
	if len(b) == 0 {
		return a
	}
	seen := make(map[string]bool, len(a)+len(b))
	out := make([]string, 0, len(a)+len(b))
	for _, t := range a {
		if t == "" || seen[t] {
			continue
		}
		seen[t] = true
		out = append(out, t)
	}
	for _, t := range b {
		if t == "" || seen[t] {
			continue
		}
		seen[t] = true
		out = append(out, t)
	}
	return out
}

// DNSActivity summarises DNS query volume observed for a device, derived
// from the terrain poller's top-N clients list.
type DNSActivity struct {
	Source       string // e.g. "Technitium DNS"
	Available    bool   // terrain reachable and DNS stats present
	InTop        bool   // any of the device's IPs appear in TopClients
	TotalQueries int64  // sum across this device's matching IPs
	MatchedIPs   []string
	Note         string // human-readable caveat
}

func buildDNSActivity(status terrain.Status, ips []string) *DNSActivity {
	a := &DNSActivity{Source: string(status.Kind)}
	if !status.Reachable || status.DNS == nil {
		a.Note = "Terrain DNS server not reachable"
		return a
	}
	a.Available = true
	if len(status.DNS.TopClients) == 0 || len(ips) == 0 {
		a.Note = "No top-client data available"
		return a
	}
	ipSet := make(map[string]bool, len(ips))
	for _, ip := range ips {
		if ip != "" {
			ipSet[ip] = true
		}
	}
	for _, c := range status.DNS.TopClients {
		if ipSet[c.Name] {
			a.InTop = true
			a.TotalQueries += c.Count
			a.MatchedIPs = append(a.MatchedIPs, c.Name)
		}
	}
	if !a.InTop {
		a.Note = "Not in top-10 querying clients (24h)"
	}
	return a
}

// deviceEdit handles POST /devices/{id}/edit — updates description (notes)
// and tags for a discovered device.
func (s *Server) deviceEdit(w http.ResponseWriter, r *http.Request) {
	id := strings.ToLower(chi.URLParam(r, "id"))
	d, err := s.Store.GetDevice(r.Context(), id)
	if err != nil || d == nil {
		http.NotFound(w, r)
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	notes := strings.TrimSpace(r.FormValue("description"))
	if len(notes) > 2000 {
		notes = notes[:2000]
	}
	tags := store.ParseTagInput(r.FormValue("tags"))
	if err := s.Store.UpdateDeviceNotes(r.Context(), d.ID, notes); err != nil {
		slog.Error("update device notes failed", "handler", "deviceEdit", "id", d.ID, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	if err := s.Store.SetTagsForTarget(r.Context(), store.TagTargetDevice, d.ID, tags); err != nil {
		slog.Error("set device tags failed", "handler", "deviceEdit", "id", d.ID, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	http.Redirect(w, r, "/devices/"+d.ID, http.StatusSeeOther)
}

// deviceMerge handles POST /devices/{id}/merge. The current device's hardware
// row survives and the target device's hardware is merged into it.
func (s *Server) deviceMerge(w http.ResponseWriter, r *http.Request) {
	id := strings.ToLower(chi.URLParam(r, "id"))
	d, err := s.Store.GetDevice(r.Context(), id)
	if err != nil || d == nil {
		http.NotFound(w, r)
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "bad form", http.StatusBadRequest)
		return
	}
	targetRef := strings.ToLower(strings.TrimSpace(r.FormValue("target")))
	if targetRef == "" {
		http.Error(w, "merge target required", http.StatusBadRequest)
		return
	}
	target, err := s.Store.GetDevice(r.Context(), targetRef)
	if err != nil {
		slog.Error("lookup merge target failed", "handler", "deviceMerge", "id", d.ID, "target", targetRef, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	if target == nil {
		http.NotFound(w, r)
		return
	}
	if d.GroupID == target.GroupID {
		http.Redirect(w, r, "/devices/"+d.ID, http.StatusSeeOther)
		return
	}
	if err := s.Store.MergeHardware(r.Context(), d.GroupID, target.GroupID); err != nil {
		slog.Error("merge hardware failed", "handler", "deviceMerge", "survivor", d.GroupID, "source", target.GroupID, "err", err)
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	if err := pipeline.DeriveDeviceKindForHardware(r.Context(), s.Store, d.GroupID); err != nil {
		slog.Warn("derive device kind after manual merge failed", "handler", "deviceMerge", "hardware_id", d.GroupID, "err", err)
	}
	http.Redirect(w, r, "/devices/"+d.ID, http.StatusSeeOther)
}
