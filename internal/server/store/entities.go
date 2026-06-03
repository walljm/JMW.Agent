package store

import (
	"context"
	"database/sql"
	"strings"
	"time"

	"github.com/google/uuid"
)

// --- Entity structs ---

// Hardware represents a physical (or virtual) device chassis.
type Hardware struct {
	ID              string
	SystemSerial    string
	BoardSerial     string
	SystemVendor    string
	SystemModel     string
	BoardVendor     string
	BoardModel      string
	CPUModel        string
	CPUCores        *int
	CPULogicalCores *int
	TotalMemBytes   *int64
	Virtualization  string
	ChassisType     string
	FirstSeenAt     time.Time
	LastSeenAt      time.Time
	CreatedAt       time.Time
	UpdatedAt       time.Time
}

// System represents an OS installation on a Hardware.
type System struct {
	ID          string
	HardwareID  string
	AgentID     string
	Hostname    string
	OSFamily    string
	OSDistro    string
	OSVersion   string
	OSBuild     string
	Kernel      string
	KernelArch  string
	Timezone    string
	BootTime    string
	InstallDate string
	FirstSeenAt time.Time
	LastSeenAt  time.Time
	CreatedAt   time.Time
	UpdatedAt   time.Time
}

// AgentSystemLinkResult describes the agent identity chosen for a hardware link.
type AgentSystemLinkResult struct {
	CanonicalAgentID   string
	SupersededAgentIDs []string
}

// Interface represents a network interface (physical or virtual).
type Interface struct {
	ID            string
	SystemID      string
	HardwareID    string
	MAC           string
	Name          string
	IfaceType     string
	MTU           *int
	LinkSpeedMbps *int
	IsUp          bool
	FirstSeenAt   time.Time
	LastSeenAt    time.Time
	CreatedAt     time.Time
	UpdatedAt     time.Time
}

// InterfaceAddress is one IP address on an interface.
type InterfaceAddress struct {
	ID          int64
	InterfaceID string
	Address     string // CIDR
	Family      string // ipv4|ipv6
	Scope       string
	FirstSeenAt time.Time
	LastSeenAt  time.Time
}

// Observation is a timestamped sighting from a source.
type Observation struct {
	ID          int64
	InterfaceID string
	SourceID    string
	ObservedAt  time.Time
	ObsType     string
	RawJSON     string
	CreatedAt   time.Time
}

// EntityHostnameAlias is a name associated with an interface from a specific source.
type EntityHostnameAlias struct {
	ID          int64
	InterfaceID string
	Hostname    string
	SourceKind  string
	Priority    int
	FirstSeenAt time.Time
	LastSeenAt  time.Time
}

// Service is a detected network service on an interface.
type Service struct {
	ID          int64
	InterfaceID string
	Proto       string
	Port        int
	ServiceName string
	Product     string
	Version     string
	Banner      string
	FirstSeenAt time.Time
	LastSeenAt  time.Time
}

// Disk is a physical storage device attached to a System.
type Disk struct {
	ID          int64
	SystemID    string
	Name        string
	Model       string
	Serial      string
	SizeBytes   *int64
	DiskType    string
	Removable   bool
	FirstSeenAt time.Time
	LastSeenAt  time.Time
}

// DiskSMART is the latest SMART health attributes for a disk.
type DiskSMART struct {
	DiskID              int64
	OverallHealth       string
	TemperatureC        *float64
	PowerOnHours        *int64
	PowerCycleCount     *int64
	ReallocatedSectors  *int64
	PendingSectors      *int64
	UncorrectableErrors *int64
	MediaWearoutPct     *float64
	PercentageUsed      *float64
	AvailableSparePct   *float64
	DataUnitsReadGB     *float64
	DataUnitsWrittenGB  *float64
	UpdatedAt           time.Time
}

// --- Hardware CRUD ---

// UpsertHardware inserts or updates a hardware record. Returns the ID.
func (s *Store) UpsertHardware(ctx context.Context, hw *Hardware) (string, error) {
	now := time.Now().UTC()
	if hw.ID == "" {
		hw.ID = uuid.New().String()
	}
	hw.LastSeenAt = now
	hw.UpdatedAt = now

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO hardware (id, system_serial, board_serial, system_vendor, system_model,
		    board_vendor, board_model, cpu_model, cpu_cores, cpu_logical_cores,
		    total_mem_bytes, virtualization, chassis_type, first_seen_at, last_seen_at, created_at, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(id) DO UPDATE SET
		    system_serial=excluded.system_serial, board_serial=excluded.board_serial,
		    system_vendor=excluded.system_vendor, system_model=excluded.system_model,
		    board_vendor=excluded.board_vendor, board_model=excluded.board_model,
		    cpu_model=excluded.cpu_model, cpu_cores=excluded.cpu_cores,
		    cpu_logical_cores=excluded.cpu_logical_cores, total_mem_bytes=excluded.total_mem_bytes,
		    virtualization=excluded.virtualization, chassis_type=excluded.chassis_type,
		    last_seen_at=excluded.last_seen_at, updated_at=excluded.updated_at`,
		hw.ID, nullStr(hw.SystemSerial), nullStr(hw.BoardSerial),
		nullStr(hw.SystemVendor), nullStr(hw.SystemModel),
		nullStr(hw.BoardVendor), nullStr(hw.BoardModel),
		nullStr(hw.CPUModel), hw.CPUCores, hw.CPULogicalCores,
		hw.TotalMemBytes, nullStr(hw.Virtualization), nullStr(hw.ChassisType),
		now.Format(time.RFC3339), now.Format(time.RFC3339),
		now.Format(time.RFC3339), now.Format(time.RFC3339))
	if err != nil {
		return "", err
	}
	return hw.ID, nil
}

// GetHardware retrieves a hardware record by ID.
func (s *Store) GetHardware(ctx context.Context, id string) (*Hardware, error) {
	hw := &Hardware{}
	var serial, boardSerial, vendor, model, bVendor, bModel, cpuModel, virt, chassis sql.NullString
	var cores, logCores sql.NullInt64
	var memBytes sql.NullInt64
	var firstSeen, lastSeen, createdAt, updatedAt string

	err := s.DB.QueryRowContext(ctx,
		`SELECT id, system_serial, board_serial, system_vendor, system_model,
		        board_vendor, board_model, cpu_model, cpu_cores, cpu_logical_cores,
		        total_mem_bytes, virtualization, chassis_type, first_seen_at, last_seen_at,
		        created_at, updated_at
		 FROM hardware WHERE id = ?`, id).Scan(
		&hw.ID, &serial, &boardSerial, &vendor, &model,
		&bVendor, &bModel, &cpuModel, &cores, &logCores,
		&memBytes, &virt, &chassis, &firstSeen, &lastSeen,
		&createdAt, &updatedAt)
	if err != nil {
		return nil, err
	}

	hw.SystemSerial = serial.String
	hw.BoardSerial = boardSerial.String
	hw.SystemVendor = vendor.String
	hw.SystemModel = model.String
	hw.BoardVendor = bVendor.String
	hw.BoardModel = bModel.String
	hw.CPUModel = cpuModel.String
	if cores.Valid {
		v := int(cores.Int64)
		hw.CPUCores = &v
	}
	if logCores.Valid {
		v := int(logCores.Int64)
		hw.CPULogicalCores = &v
	}
	if memBytes.Valid {
		hw.TotalMemBytes = &memBytes.Int64
	}
	hw.Virtualization = virt.String
	hw.ChassisType = chassis.String
	hw.FirstSeenAt, _ = time.Parse(time.RFC3339, firstSeen)
	hw.LastSeenAt, _ = time.Parse(time.RFC3339, lastSeen)
	hw.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
	hw.UpdatedAt, _ = time.Parse(time.RFC3339, updatedAt)
	return hw, nil
}

// SetHardwareDeviceKind updates the derived device classification for a
// hardware record. The classification is computed by the pipeline's Derive
// stage from observation signals (mDNS services, vendor OUI, hostname patterns).
func (s *Store) SetHardwareDeviceKind(ctx context.Context, hardwareID, kind string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE hardware SET device_kind = ?, updated_at = ? WHERE id = ?`,
		nullStr(kind), time.Now().UTC().Format(time.RFC3339), hardwareID)
	return err
}

// GetHardwareDeviceKind returns the current device_kind for a hardware record
// (empty string if unset).
func (s *Store) GetHardwareDeviceKind(ctx context.Context, hardwareID string) (string, error) {
	var kind sql.NullString
	err := s.DB.QueryRowContext(ctx,
		`SELECT device_kind FROM hardware WHERE id = ?`, hardwareID).Scan(&kind)
	if err != nil {
		return "", err
	}
	return kind.String, nil
}

// --- System CRUD ---

// UpsertSystem inserts or updates a system record. Returns the ID.
func (s *Store) UpsertSystem(ctx context.Context, sys *System) (string, error) {
	now := time.Now().UTC()
	if sys.ID == "" {
		sys.ID = uuid.New().String()
	}
	sys.LastSeenAt = now
	sys.UpdatedAt = now

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO systems (id, hardware_id, agent_id, hostname, os_family, os_distro,
		    os_version, os_build, kernel, kernel_arch, timezone, boot_time, install_date,
		    first_seen_at, last_seen_at, created_at, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(id) DO UPDATE SET
		    hardware_id=excluded.hardware_id, agent_id=excluded.agent_id,
		    hostname=excluded.hostname, os_family=excluded.os_family,
		    os_distro=excluded.os_distro, os_version=excluded.os_version,
		    os_build=excluded.os_build, kernel=excluded.kernel,
		    kernel_arch=excluded.kernel_arch, timezone=excluded.timezone,
		    boot_time=excluded.boot_time, install_date=excluded.install_date,
		    last_seen_at=excluded.last_seen_at, updated_at=excluded.updated_at`,
		sys.ID, sys.HardwareID, nullStr(sys.AgentID), sys.Hostname, sys.OSFamily,
		nullStr(sys.OSDistro), nullStr(sys.OSVersion), nullStr(sys.OSBuild),
		nullStr(sys.Kernel), nullStr(sys.KernelArch), nullStr(sys.Timezone),
		nullStr(sys.BootTime), nullStr(sys.InstallDate),
		now.Format(time.RFC3339), now.Format(time.RFC3339),
		now.Format(time.RFC3339), now.Format(time.RFC3339))
	if err != nil {
		return "", err
	}
	return sys.ID, nil
}

// GetSystem retrieves a system by ID.
func (s *Store) GetSystem(ctx context.Context, id string) (*System, error) {
	sys := &System{}
	var agentID, distro, version, build, kernel, kArch, tz, boot, install sql.NullString
	var firstSeen, lastSeen, createdAt, updatedAt string

	err := s.DB.QueryRowContext(ctx,
		`SELECT id, hardware_id, agent_id, hostname, os_family, os_distro,
		        os_version, os_build, kernel, kernel_arch, timezone, boot_time, install_date,
		        first_seen_at, last_seen_at, created_at, updated_at
		 FROM systems WHERE id = ?`, id).Scan(
		&sys.ID, &sys.HardwareID, &agentID, &sys.Hostname, &sys.OSFamily,
		&distro, &version, &build, &kernel, &kArch, &tz, &boot, &install,
		&firstSeen, &lastSeen, &createdAt, &updatedAt)
	if err != nil {
		return nil, err
	}

	sys.AgentID = agentID.String
	sys.OSDistro = distro.String
	sys.OSVersion = version.String
	sys.OSBuild = build.String
	sys.Kernel = kernel.String
	sys.KernelArch = kArch.String
	sys.Timezone = tz.String
	sys.BootTime = boot.String
	sys.InstallDate = install.String
	sys.FirstSeenAt, _ = time.Parse(time.RFC3339, firstSeen)
	sys.LastSeenAt, _ = time.Parse(time.RFC3339, lastSeen)
	sys.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
	sys.UpdatedAt, _ = time.Parse(time.RFC3339, updatedAt)
	return sys, nil
}

// GetSystemByAgent retrieves the system associated with an agent ID.
func (s *Store) GetSystemByAgent(ctx context.Context, agentID string) (*System, error) {
	var id string
	err := s.DB.QueryRowContext(ctx,
		`SELECT id FROM systems WHERE agent_id = ? ORDER BY last_seen_at DESC LIMIT 1`, agentID).Scan(&id)
	if err != nil {
		return nil, err
	}
	return s.GetSystem(ctx, id)
}

// EnsureAgentSystem links an agent to its hardware by upserting a systems row.
// It looks up the interface by MAC to get the hardware_id, then creates or
// updates the systems row so that hydrateDevices can populate Device.AgentID.
// Call this after inventory ingest once the pipeline has created the interface.
func (s *Store) EnsureAgentSystem(ctx context.Context, agentID, mac, hostname, osFamily string) (*AgentSystemLinkResult, error) {
	iface, err := s.GetInterfaceByMAC(ctx, mac)
	if err != nil {
		if err == sql.ErrNoRows {
			return &AgentSystemLinkResult{CanonicalAgentID: agentID}, nil // interface not yet resolved; skip — will be linked on next tick
		}
		return nil, err
	}

	now := time.Now().UTC().Format(time.RFC3339)
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback() }()

	// Update existing system row if one already exists for this agent.
	var existingID string
	err = tx.QueryRowContext(ctx,
		`SELECT id FROM systems WHERE agent_id = ? ORDER BY last_seen_at DESC LIMIT 1`, agentID).Scan(&existingID)
	if err != nil && err != sql.ErrNoRows {
		return nil, err
	}
	if existingID != "" {
		if _, err = tx.ExecContext(ctx,
			`UPDATE systems SET hardware_id = ?, hostname = ?, os_family = ?, last_seen_at = ?, updated_at = ? WHERE id = ?`,
			iface.HardwareID, hostname, osFamily, now, now, existingID); err != nil {
			return nil, err
		}
	} else {
		id := uuid.New().String()
		if _, err = tx.ExecContext(ctx,
			`INSERT INTO systems (id, hardware_id, agent_id, hostname, os_family, first_seen_at, last_seen_at, created_at, updated_at)
			 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
			 ON CONFLICT(id) DO UPDATE SET
			    hardware_id = excluded.hardware_id, agent_id = excluded.agent_id,
			    hostname = excluded.hostname, os_family = excluded.os_family,
			    last_seen_at = excluded.last_seen_at, updated_at = excluded.updated_at`,
			id, iface.HardwareID, agentID, hostname, osFamily, now, now, now, now); err != nil {
			return nil, err
		}
	}

	result, err := s.canonicalizeAgentsForHardware(ctx, tx, agentID, iface.HardwareID, now)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(); err != nil {
		return nil, err
	}
	return result, nil
}

func (s *Store) canonicalizeAgentsForHardware(ctx context.Context, tx *sql.Tx, currentAgentID, hardwareID, now string) (*AgentSystemLinkResult, error) {
	rows, err := tx.QueryContext(ctx,
		`SELECT DISTINCT a.id
		 FROM systems s
		 JOIN agents a ON a.id = s.agent_id
		 WHERE s.hardware_id = ? AND a.status = ?
		 ORDER BY a.registered_at ASC,
		          a.rowid ASC,
		          COALESCE(a.last_heartbeat_at, a.registered_at) DESC`,
		hardwareID, AgentStatusApproved)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var agentIDs []string
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		agentIDs = append(agentIDs, id)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	if len(agentIDs) == 0 {
		return &AgentSystemLinkResult{CanonicalAgentID: currentAgentID}, nil
	}

	canonicalAgentID := agentIDs[0]
	superseded := make([]string, 0, len(agentIDs)-1)
	for _, id := range agentIDs[1:] {
		if id != canonicalAgentID {
			superseded = append(superseded, id)
		}
	}
	if len(superseded) == 0 {
		return &AgentSystemLinkResult{CanonicalAgentID: canonicalAgentID}, nil
	}

	if _, err := tx.ExecContext(ctx,
		`UPDATE agents
		 SET notes = CASE
			 WHEN COALESCE(notes, '') = '' THEN COALESCE((
				 SELECT old.notes
				 FROM agents old
				 JOIN systems oldsys ON oldsys.agent_id = old.id
				 WHERE oldsys.hardware_id = ?
				   AND old.id != ?
				   AND old.status = ?
				   AND COALESCE(old.notes, '') != ''
				 ORDER BY COALESCE(old.last_heartbeat_at, old.registered_at) DESC
				 LIMIT 1
			 ), notes)
			 ELSE notes
		 END,
		 group_id = COALESCE(group_id, (
			 SELECT old.group_id
			 FROM agents old
			 JOIN systems oldsys ON oldsys.agent_id = old.id
			 WHERE oldsys.hardware_id = ?
			   AND old.id != ?
			   AND old.status = ?
			   AND old.group_id IS NOT NULL
			 ORDER BY COALESCE(old.last_heartbeat_at, old.registered_at) DESC
			 LIMIT 1
		 ))
		 WHERE id = ?`,
		hardwareID, canonicalAgentID, AgentStatusApproved,
		hardwareID, canonicalAgentID, AgentStatusApproved,
		canonicalAgentID); err != nil {
		return nil, err
	}

	if _, err := tx.ExecContext(ctx,
		`INSERT OR IGNORE INTO tag_assignments(tag_id, target_kind, target_id)
		 SELECT DISTINCT ta.tag_id, ta.target_kind, ?
		 FROM tag_assignments ta
		 JOIN systems s ON s.agent_id = ta.target_id
		 JOIN agents a ON a.id = s.agent_id
		 WHERE ta.target_kind = ?
		   AND s.hardware_id = ?
		   AND a.id != ?
		   AND a.status = ?`,
		canonicalAgentID, TagTargetAgent, hardwareID, canonicalAgentID, AgentStatusApproved); err != nil {
		return nil, err
	}

	if _, err := tx.ExecContext(ctx,
		`UPDATE agents
		 SET inventory_json = COALESCE((
			 SELECT old.inventory_json
			 FROM agents old
			 JOIN systems oldsys ON oldsys.agent_id = old.id
			 WHERE oldsys.hardware_id = ?
			   AND old.id != ?
			   AND old.status = ?
			   AND COALESCE(old.inventory_json, '') != ''
			 ORDER BY COALESCE(old.inventory_collected_at, old.registered_at) DESC
			 LIMIT 1
		 ), inventory_json),
		 inventory_collected_at = COALESCE((
			 SELECT old.inventory_collected_at
			 FROM agents old
			 JOIN systems oldsys ON oldsys.agent_id = old.id
			 WHERE oldsys.hardware_id = ?
			   AND old.id != ?
			   AND old.status = ?
			   AND old.inventory_collected_at IS NOT NULL
			 ORDER BY old.inventory_collected_at DESC
			 LIMIT 1
		 ), inventory_collected_at),
		 primary_ip = COALESCE(NULLIF(primary_ip, ''), (
			 SELECT old.primary_ip
			 FROM agents old
			 JOIN systems oldsys ON oldsys.agent_id = old.id
			 WHERE oldsys.hardware_id = ?
			   AND old.id != ?
			   AND old.status = ?
			   AND COALESCE(old.primary_ip, '') != ''
			 ORDER BY COALESCE(old.inventory_collected_at, old.registered_at) DESC
			 LIMIT 1
		 ))
		 WHERE id = ?`,
		hardwareID, canonicalAgentID, AgentStatusApproved,
		hardwareID, canonicalAgentID, AgentStatusApproved,
		hardwareID, canonicalAgentID, AgentStatusApproved,
		canonicalAgentID); err != nil {
		return nil, err
	}

	if _, err := tx.ExecContext(ctx,
		`UPDATE sources SET agent_id = ?, updated_at = ? WHERE agent_id IN (`+placeholdersFor(len(superseded))+`)`,
		append([]any{canonicalAgentID, now}, stringsToAny(superseded)...)...); err != nil {
		return nil, err
	}

	placeholders := strings.TrimRight(strings.Repeat("?,", len(superseded)), ",")
	clearArgs := make([]any, 0, len(superseded)+2)
	clearArgs = append(clearArgs, now, hardwareID)
	for _, id := range superseded {
		clearArgs = append(clearArgs, id)
	}
	if _, err := tx.ExecContext(ctx,
		`UPDATE systems SET agent_id = NULL, updated_at = ? WHERE hardware_id = ? AND agent_id IN (`+placeholders+`)`,
		clearArgs...); err != nil {
		return nil, err
	}

	statusArgs := make([]any, 0, len(superseded)+1)
	statusArgs = append(statusArgs, AgentStatusDeregistered)
	for _, id := range superseded {
		statusArgs = append(statusArgs, id)
	}
	if _, err := tx.ExecContext(ctx,
		`UPDATE agents SET status = ? WHERE id IN (`+placeholders+`)`,
		statusArgs...); err != nil {
		return nil, err
	}

	return &AgentSystemLinkResult{CanonicalAgentID: canonicalAgentID, SupersededAgentIDs: superseded}, nil
}

func placeholdersFor(count int) string {
	return strings.TrimRight(strings.Repeat("?,", count), ",")
}

func stringsToAny(values []string) []any {
	out := make([]any, len(values))
	for i, value := range values {
		out[i] = value
	}
	return out
}

// --- Interface CRUD ---

// UpsertInterface inserts or updates an interface by MAC. Returns the ID.
// COALESCE on name/type/mtu/speed means values only grow (progressive enrichment);
// once a field is set, it won't revert to NULL on subsequent upserts with empty values.
func (s *Store) UpsertInterface(ctx context.Context, iface *Interface) (string, error) {
	now := time.Now().UTC()
	if iface.ID == "" {
		iface.ID = uuid.New().String()
	}
	iface.LastSeenAt = now
	iface.UpdatedAt = now

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO interfaces (id, system_id, hardware_id, mac, name, iface_type, mtu,
		    link_speed_mbps, is_up, first_seen_at, last_seen_at, created_at, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(mac) DO UPDATE SET
		    system_id=excluded.system_id, hardware_id=excluded.hardware_id,
		    name=COALESCE(excluded.name, interfaces.name),
		    iface_type=COALESCE(excluded.iface_type, interfaces.iface_type),
		    mtu=COALESCE(excluded.mtu, interfaces.mtu),
		    link_speed_mbps=COALESCE(excluded.link_speed_mbps, interfaces.link_speed_mbps),
		    is_up=excluded.is_up,
		    last_seen_at=excluded.last_seen_at, updated_at=excluded.updated_at`,
		iface.ID, nullStr(iface.SystemID), iface.HardwareID, iface.MAC,
		nullStr(iface.Name), nullStr(iface.IfaceType), iface.MTU,
		iface.LinkSpeedMbps, boolToInt(iface.IsUp),
		now.Format(time.RFC3339), now.Format(time.RFC3339),
		now.Format(time.RFC3339), now.Format(time.RFC3339))
	if err != nil {
		return "", err
	}

	// If this was an upsert (conflict), we need the existing ID.
	var existingID string
	err = s.DB.QueryRowContext(ctx, `SELECT id FROM interfaces WHERE mac = ?`, iface.MAC).Scan(&existingID)
	if err != nil {
		return "", err
	}
	iface.ID = existingID
	return existingID, nil
}

// TouchInterface updates last_seen_at to now for the given interface ID.
// Called on every observation for an existing interface so the device's
// "Last Seen" reflects real activity rather than the creation timestamp.
func (s *Store) TouchInterface(ctx context.Context, id string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE interfaces SET last_seen_at = ? WHERE id = ?`,
		time.Now().UTC().Format(time.RFC3339), id)
	return err
}

// GetInterfaceByMAC retrieves an interface by MAC address.
func (s *Store) GetInterfaceByMAC(ctx context.Context, mac string) (*Interface, error) {
	iface := &Interface{}
	var sysID, name, ifType sql.NullString
	var mtu, speed sql.NullInt64
	var isUp int
	var firstSeen, lastSeen, createdAt, updatedAt string

	err := s.DB.QueryRowContext(ctx,
		`SELECT id, system_id, hardware_id, mac, name, iface_type, mtu,
		        link_speed_mbps, is_up, first_seen_at, last_seen_at, created_at, updated_at
		 FROM interfaces WHERE mac = ?`, strings.ToLower(mac)).Scan(
		&iface.ID, &sysID, &iface.HardwareID, &iface.MAC, &name, &ifType,
		&mtu, &speed, &isUp, &firstSeen, &lastSeen, &createdAt, &updatedAt)
	if err != nil {
		return nil, err
	}

	iface.SystemID = sysID.String
	iface.Name = name.String
	iface.IfaceType = ifType.String
	if mtu.Valid {
		v := int(mtu.Int64)
		iface.MTU = &v
	}
	if speed.Valid {
		v := int(speed.Int64)
		iface.LinkSpeedMbps = &v
	}
	iface.IsUp = isUp != 0
	iface.FirstSeenAt, _ = time.Parse(time.RFC3339, firstSeen)
	iface.LastSeenAt, _ = time.Parse(time.RFC3339, lastSeen)
	iface.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
	iface.UpdatedAt, _ = time.Parse(time.RFC3339, updatedAt)
	return iface, nil
}

// --- Interface Address CRUD ---

// UpsertInterfaceAddress inserts or updates an address on an interface.
// The address is normalized to a bare IP (CIDR prefix stripped) before
// storage so that "192.168.1.60" and "192.168.1.60/24" converge to the
// same row regardless of which source submitted it.
func (s *Store) UpsertInterfaceAddress(ctx context.Context, addr *InterfaceAddress) error {
	now := time.Now().UTC()
	nowStr := now.Format(time.RFC3339)

	ip := addr.Address
	if idx := strings.IndexByte(ip, '/'); idx > 0 {
		ip = ip[:idx]
	}

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO interface_addresses (interface_id, address, family, scope, first_seen_at, last_seen_at)
		 VALUES (?, ?, ?, ?, ?, ?)
		 ON CONFLICT(interface_id, address) DO UPDATE SET
		    last_seen_at=excluded.last_seen_at, scope=COALESCE(excluded.scope, interface_addresses.scope)`,
		addr.InterfaceID, ip, addr.Family, nullStr(addr.Scope), nowStr, nowStr)
	return err
}

// --- Observation insert ---

// InsertObservation records a new observation. Returns the auto-generated ID.
func (s *Store) InsertObservation(ctx context.Context, obs *Observation) (int64, error) {
	now := time.Now().UTC()
	result, err := s.DB.ExecContext(ctx,
		`INSERT INTO observations (interface_id, source_id, observed_at, obs_type, raw_json, created_at)
		 VALUES (?, ?, ?, ?, ?, ?)`,
		obs.InterfaceID, obs.SourceID, obs.ObservedAt.Format(time.RFC3339),
		obs.ObsType, nullStr(obs.RawJSON), now.Format(time.RFC3339))
	if err != nil {
		return 0, err
	}
	return result.LastInsertId()
}

// --- Hostname Alias ---

// UpsertHostnameAlias inserts or updates a hostname alias.
func (s *Store) UpsertHostnameAlias(ctx context.Context, alias *EntityHostnameAlias) error {
	now := time.Now().UTC()
	nowStr := now.Format(time.RFC3339)

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO hostname_aliases (interface_id, hostname, source_kind, priority, first_seen_at, last_seen_at, seen_count)
		 VALUES (?, ?, ?, ?, ?, ?, 1)
		 ON CONFLICT(interface_id, hostname, source_kind) DO UPDATE SET
		    priority=excluded.priority,
		    last_seen_at=excluded.last_seen_at,
		    seen_count=hostname_aliases.seen_count + 1`,
		alias.InterfaceID, alias.Hostname, alias.SourceKind, alias.Priority, nowStr, nowStr)
	return err
}

// GetCanonicalHostname returns the highest-priority (lowest number) hostname for an interface.
func (s *Store) GetCanonicalHostname(ctx context.Context, interfaceID string) (string, error) {
	var hostname string
	err := s.DB.QueryRowContext(ctx,
		`SELECT hostname FROM hostname_aliases
		 WHERE interface_id = ? ORDER BY priority ASC, last_seen_at DESC LIMIT 1`,
		interfaceID).Scan(&hostname)
	if err != nil {
		return "", err
	}
	return hostname, nil
}

// --- Service CRUD ---

// UpsertService inserts or updates a detected service.
func (s *Store) UpsertService(ctx context.Context, svc *Service) error {
	now := time.Now().UTC()
	nowStr := now.Format(time.RFC3339)

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO services (interface_id, proto, port, service_name, product, version, banner, first_seen_at, last_seen_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(interface_id, proto, port) DO UPDATE SET
		    service_name=COALESCE(excluded.service_name, services.service_name),
		    product=COALESCE(excluded.product, services.product),
		    version=COALESCE(excluded.version, services.version),
		    banner=COALESCE(excluded.banner, services.banner),
		    last_seen_at=excluded.last_seen_at`,
		svc.InterfaceID, svc.Proto, svc.Port,
		nullStr(svc.ServiceName), nullStr(svc.Product),
		nullStr(svc.Version), nullStr(svc.Banner), nowStr, nowStr)
	return err
}

// --- Disk CRUD ---

// UpsertDisk inserts or updates a disk. Returns the auto-generated ID.
func (s *Store) UpsertDisk(ctx context.Context, d *Disk) (int64, error) {
	now := time.Now().UTC()
	nowStr := now.Format(time.RFC3339)

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO disks (system_id, name, model, serial, size_bytes, disk_type, removable, first_seen_at, last_seen_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(system_id, name) DO UPDATE SET
		    model=COALESCE(excluded.model, disks.model),
		    serial=COALESCE(excluded.serial, disks.serial),
		    size_bytes=COALESCE(excluded.size_bytes, disks.size_bytes),
		    disk_type=COALESCE(excluded.disk_type, disks.disk_type),
		    removable=excluded.removable,
		    last_seen_at=excluded.last_seen_at`,
		d.SystemID, d.Name, nullStr(d.Model), nullStr(d.Serial),
		d.SizeBytes, nullStr(d.DiskType), boolToInt(d.Removable), nowStr, nowStr)
	if err != nil {
		return 0, err
	}

	// Get the actual ID (could be existing row on conflict).
	var id int64
	err = s.DB.QueryRowContext(ctx,
		`SELECT id FROM disks WHERE system_id = ? AND name = ?`, d.SystemID, d.Name).Scan(&id)
	if err != nil {
		return 0, err
	}
	d.ID = id
	return id, nil
}

// UpsertDiskSMART inserts or updates SMART attributes for a disk.
func (s *Store) UpsertDiskSMART(ctx context.Context, smart *DiskSMART) error {
	now := time.Now().UTC().Format(time.RFC3339)

	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO disk_smart_attributes (disk_id, overall_health, temperature_c, power_on_hours,
		    power_cycle_count, reallocated_sectors, pending_sectors, uncorrectable_errors,
		    media_wearout_pct, percentage_used, available_spare_pct, data_units_read_gb,
		    data_units_written_gb, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(disk_id) DO UPDATE SET
		    overall_health=excluded.overall_health, temperature_c=excluded.temperature_c,
		    power_on_hours=excluded.power_on_hours, power_cycle_count=excluded.power_cycle_count,
		    reallocated_sectors=excluded.reallocated_sectors, pending_sectors=excluded.pending_sectors,
		    uncorrectable_errors=excluded.uncorrectable_errors, media_wearout_pct=excluded.media_wearout_pct,
		    percentage_used=excluded.percentage_used, available_spare_pct=excluded.available_spare_pct,
		    data_units_read_gb=excluded.data_units_read_gb, data_units_written_gb=excluded.data_units_written_gb,
		    updated_at=excluded.updated_at`,
		smart.DiskID, nullStr(smart.OverallHealth), smart.TemperatureC,
		smart.PowerOnHours, smart.PowerCycleCount, smart.ReallocatedSectors,
		smart.PendingSectors, smart.UncorrectableErrors, smart.MediaWearoutPct,
		smart.PercentageUsed, smart.AvailableSparePct, smart.DataUnitsReadGB,
		smart.DataUnitsWrittenGB, now)
	return err
}
