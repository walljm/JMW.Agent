package store

import (
	"context"
	"database/sql"
	"time"
)

// Fingerprint is a single identity marker linked to a hardware (device) record.
type Fingerprint struct {
	ID          int64
	HardwareID  string
	Kind        string // "mac", "serial:system", "serial:board", "docker_engine_id"
	Value       string // normalized fingerprint value
	Source      string // what reported it
	FirstSeenAt time.Time
	LastSeenAt  time.Time
}

// LookupFingerprints queries the device_fingerprints table for any of the given
// (kind, value) pairs and returns matching hardware IDs. The returned map is
// keyed by hardware_id; the value is the first matching fingerprint for logging.
func (s *Store) LookupFingerprints(ctx context.Context, fps []FingerprintInput) (map[string]FingerprintInput, error) {
	if len(fps) == 0 {
		return nil, nil
	}

	hits := make(map[string]FingerprintInput)
	for _, fp := range fps {
		var hwID string
		err := s.DB.QueryRowContext(ctx,
			`SELECT hardware_id FROM device_fingerprints WHERE kind = ? AND value = ?`,
			fp.Kind, fp.Value).Scan(&hwID)
		if err != nil {
			if err == sql.ErrNoRows {
				continue
			}
			return nil, err
		}
		if _, exists := hits[hwID]; !exists {
			hits[hwID] = fp
		}
	}
	return hits, nil
}

// RegisterFingerprints inserts new fingerprints for a hardware record, ignoring
// any that already exist (ON CONFLICT IGNORE). For existing fingerprints that
// already point to this hardware, it touches last_seen_at.
func (s *Store) RegisterFingerprints(ctx context.Context, hardwareID string, fps []FingerprintInput) error {
	now := time.Now().UTC().Format(time.RFC3339)
	for _, fp := range fps {
		_, err := s.DB.ExecContext(ctx,
			`INSERT INTO device_fingerprints (hardware_id, kind, value, source, first_seen_at, last_seen_at)
			 VALUES (?, ?, ?, ?, ?, ?)
			 ON CONFLICT(kind, value) DO UPDATE SET
			    last_seen_at = excluded.last_seen_at`,
			hardwareID, fp.Kind, fp.Value, fp.Source, now, now)
		if err != nil {
			return err
		}
	}
	return nil
}

// ReassignFingerprints moves all fingerprints from one hardware to another.
// Used during device merge when two hardware records turn out to be the same device.
func (s *Store) ReassignFingerprints(ctx context.Context, fromHardwareID, toHardwareID string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE device_fingerprints SET hardware_id = ? WHERE hardware_id = ?`,
		toHardwareID, fromHardwareID)
	return err
}

// ReassignInterfaces moves all interfaces from one hardware to another.
func (s *Store) ReassignInterfaces(ctx context.Context, fromHardwareID, toHardwareID string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE interfaces SET hardware_id = ? WHERE hardware_id = ?`,
		toHardwareID, fromHardwareID)
	return err
}

// ReassignSystems moves all systems from one hardware to another.
func (s *Store) ReassignSystems(ctx context.Context, fromHardwareID, toHardwareID string) error {
	_, err := s.DB.ExecContext(ctx,
		`UPDATE systems SET hardware_id = ? WHERE hardware_id = ?`,
		toHardwareID, fromHardwareID)
	return err
}

// MergeHardwareMetadata copies non-null fields from the source hardware into the
// target, filling any gaps without overwriting existing values. This ensures no
// metadata is lost when two hardware records are merged.
func (s *Store) MergeHardwareMetadata(ctx context.Context, targetID, sourceID string) error {
	target, err := s.GetHardware(ctx, targetID)
	if err != nil {
		return err
	}
	source, err := s.GetHardware(ctx, sourceID)
	if err != nil {
		return err
	}
	firstSeenAt := target.FirstSeenAt
	if source.FirstSeenAt.Before(firstSeenAt) {
		firstSeenAt = source.FirstSeenAt
	}

	_, err = s.DB.ExecContext(ctx,
		`UPDATE hardware SET
		    system_serial    = COALESCE(hardware.system_serial,    (SELECT system_serial    FROM hardware h2 WHERE h2.id = ?)),
		    board_serial     = COALESCE(hardware.board_serial,     (SELECT board_serial     FROM hardware h2 WHERE h2.id = ?)),
		    system_vendor    = COALESCE(hardware.system_vendor,    (SELECT system_vendor    FROM hardware h2 WHERE h2.id = ?)),
		    system_model     = COALESCE(hardware.system_model,     (SELECT system_model     FROM hardware h2 WHERE h2.id = ?)),
		    board_vendor     = COALESCE(hardware.board_vendor,     (SELECT board_vendor     FROM hardware h2 WHERE h2.id = ?)),
		    board_model      = COALESCE(hardware.board_model,      (SELECT board_model      FROM hardware h2 WHERE h2.id = ?)),
		    cpu_model        = COALESCE(hardware.cpu_model,        (SELECT cpu_model        FROM hardware h2 WHERE h2.id = ?)),
		    cpu_cores        = COALESCE(hardware.cpu_cores,        (SELECT cpu_cores        FROM hardware h2 WHERE h2.id = ?)),
		    cpu_logical_cores= COALESCE(hardware.cpu_logical_cores,(SELECT cpu_logical_cores FROM hardware h2 WHERE h2.id = ?)),
		    total_mem_bytes  = COALESCE(hardware.total_mem_bytes,  (SELECT total_mem_bytes  FROM hardware h2 WHERE h2.id = ?)),
		    virtualization   = COALESCE(hardware.virtualization,   (SELECT virtualization   FROM hardware h2 WHERE h2.id = ?)),
		    chassis_type     = COALESCE(hardware.chassis_type,     (SELECT chassis_type     FROM hardware h2 WHERE h2.id = ?)),
		    first_seen_at    = ?,
		    updated_at       = ?
		 WHERE id = ?`,
		sourceID, sourceID, sourceID, sourceID,
		sourceID, sourceID, sourceID, sourceID,
		sourceID, sourceID, sourceID, sourceID,
		firstSeenAt.Format(time.RFC3339Nano),
		time.Now().UTC().Format(time.RFC3339),
		targetID)
	return err
}

// DeleteHardware removes a hardware record. Fingerprints, interfaces, and
// systems should be reassigned and metadata merged before calling this.
func (s *Store) DeleteHardware(ctx context.Context, hardwareID string) error {
	_, err := s.DB.ExecContext(ctx,
		`DELETE FROM hardware WHERE id = ?`, hardwareID)
	return err
}

// FingerprintInput is the data needed to register or look up a fingerprint.
type FingerprintInput struct {
	Kind   string
	Value  string
	Source string
}
