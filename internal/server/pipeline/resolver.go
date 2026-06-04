// Package pipeline implements the 5-stage entity resolution and observation pipeline.
package pipeline

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/server/store"
)

// Resolver resolves incoming observations to entity identities.
// A fresh Resolver should be constructed per batch to keep a warm cache
// of MACs resolved during the batch (reducing duplicate DB lookups).
type Resolver struct {
	store    *store.Store
	macCache map[string]*store.Interface // MAC → Interface (batch-local)
}

// NewResolver creates a Resolver backed by the given store.
func NewResolver(s *store.Store) *Resolver {
	return &Resolver{
		store:    s,
		macCache: make(map[string]*store.Interface),
	}
}

// ResolveInterface resolves a MAC address to an Interface entity using
// fingerprint-based device resolution. It checks all provided fingerprints
// against known devices before creating a new one, and registers any new
// fingerprints it discovers.
//
// The resolution flow:
//  1. Check batch-local MAC cache
//  2. Look up all fingerprints — if any match, use that hardware_id
//  3. If fingerprints point to multiple hardware_ids, merge them
//  4. If no match, create new hardware
//  5. Upsert the interface against the resolved hardware_id
//  6. Register any new fingerprints for this hardware
func (r *Resolver) ResolveInterface(ctx context.Context, mac string, hints *InterfaceHints, fingerprints []Fingerprint) (*store.Interface, error) {
	if mac == "" {
		return nil, fmt.Errorf("resolver: empty MAC address")
	}

	mac = NormalizeMAC(mac)

	// Check batch cache first.
	if cached, ok := r.macCache[mac]; ok {
		return cached, nil
	}

	// Build the full fingerprint set: always include the MAC itself.
	fps := make([]store.FingerprintInput, 0, len(fingerprints)+1)
	fps = append(fps, store.FingerprintInput{Kind: "mac", Value: mac, Source: sourceFromFingerprints(fingerprints)})
	for _, fp := range fingerprints {
		if fp.Value == "" {
			continue
		}
		fps = append(fps, store.FingerprintInput{Kind: fp.Kind, Value: fp.Value, Source: fp.Source})
	}

	// Look up all fingerprints to find an existing device.
	hits, err := r.store.LookupFingerprints(ctx, fps)
	if err != nil {
		return nil, fmt.Errorf("resolver: fingerprint lookup: %w", err)
	}

	var hardwareID string

	switch len(hits) {
	case 0:
		// No fingerprint matched — create a new device.
		hw := &store.Hardware{}
		if hints != nil {
			hw.SystemSerial = hints.SystemSerial
			hw.BoardSerial = hints.BoardSerial
			hw.SystemVendor = hints.SystemVendor
			hw.SystemModel = hints.SystemModel
		}
		hardwareID, err = r.store.UpsertHardware(ctx, hw)
		if err != nil {
			return nil, fmt.Errorf("resolver: create hardware for MAC %s: %w", mac, err)
		}

	case 1:
		// Exactly one device matched — use it.
		for hwID := range hits {
			hardwareID = hwID
		}

	default:
		// Multiple devices matched — merge them into the oldest one.
		hardwareID, err = r.mergeDevices(ctx, hits)
		if err != nil {
			return nil, fmt.Errorf("resolver: merge devices for MAC %s: %w", mac, err)
		}
	}

	// Register all fingerprints for this device (idempotent — existing ones
	// just get their last_seen_at touched).
	if err := r.store.RegisterFingerprints(ctx, hardwareID, fps); err != nil {
		return nil, fmt.Errorf("resolver: register fingerprints for MAC %s: %w", mac, err)
	}

	// Upsert the interface against the resolved hardware.
	iface := &store.Interface{
		HardwareID: hardwareID,
		MAC:        mac,
	}
	if hints != nil {
		iface.Name = hints.InterfaceName
		iface.SystemID = hints.SystemID
	}
	_, err = r.store.UpsertInterface(ctx, iface)
	if err != nil {
		return nil, fmt.Errorf("resolver: upsert interface for MAC %s: %w", mac, err)
	}

	// Re-fetch to ensure cached struct reflects DB truth.
	iface, err = r.store.GetInterfaceByMAC(ctx, mac)
	if err != nil {
		return nil, fmt.Errorf("resolver: re-fetch MAC %s: %w", mac, err)
	}

	// Touch last_seen_at.
	_ = r.store.TouchInterface(ctx, iface.ID)

	r.macCache[mac] = iface
	return iface, nil
}

// mergeDevices merges multiple hardware records into one. Picks the hardware
// with the earliest first_seen_at as the survivor, falling back to insertion
// order when old whole-second timestamps tie. Reassigns all fingerprints,
// interfaces, and systems from the losers to the survivor, then deletes the losers.
func (r *Resolver) mergeDevices(ctx context.Context, hits map[string]store.FingerprintInput) (string, error) {
	// Collect all hardware IDs and find the oldest.
	hwIDs := make([]string, 0, len(hits))
	for hwID := range hits {
		hwIDs = append(hwIDs, hwID)
	}

	type mergeCandidate struct {
		id        string
		firstSeen time.Time
		rowID     int64
	}

	var survivor mergeCandidate
	for _, hwID := range hwIDs {
		hw, err := r.store.GetHardware(ctx, hwID)
		if err != nil {
			return "", fmt.Errorf("get hardware %s: %w", hwID, err)
		}
		rowID, err := hardwareRowID(ctx, r.store, hwID)
		if err != nil {
			return "", fmt.Errorf("get hardware rowid %s: %w", hwID, err)
		}
		candidate := mergeCandidate{id: hwID, firstSeen: hw.FirstSeenAt, rowID: rowID}
		if survivor.id == "" || candidate.firstSeen.Before(survivor.firstSeen) ||
			(candidate.firstSeen.Equal(survivor.firstSeen) && candidate.rowID < survivor.rowID) {
			survivor = candidate
		}
	}
	survivorID := survivor.id

	// Reassign everything from the losers to the survivor.
	for _, hwID := range hwIDs {
		if hwID == survivorID {
			continue
		}

		slog.Info("resolver: merging device",
			"from", hwID, "to", survivorID,
			"trigger", hits[hwID].Kind+":"+hits[hwID].Value)

		if err := r.store.MergeHardware(ctx, survivorID, hwID); err != nil {
			return "", fmt.Errorf("merge hardware %s into %s: %w", hwID, survivorID, err)
		}
	}

	// Invalidate any cached interfaces that pointed to merged hardware.
	for mac, iface := range r.macCache {
		for _, hwID := range hwIDs {
			if hwID != survivorID && iface.HardwareID == hwID {
				delete(r.macCache, mac)
			}
		}
	}

	return survivorID, nil
}

func hardwareRowID(ctx context.Context, st *store.Store, hardwareID string) (int64, error) {
	var rowID int64
	err := st.DB.QueryRowContext(ctx, `SELECT rowid FROM hardware WHERE id = ?`, hardwareID).Scan(&rowID)
	return rowID, err
}

// ResolveByAgent resolves an agent ID to its System entity.
// Returns nil (no error) if the agent has no known system yet.
func (r *Resolver) ResolveByAgent(ctx context.Context, agentID string) (*store.System, error) {
	sys, err := r.store.GetSystemByAgent(ctx, agentID)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("resolver: lookup agent %s: %w", agentID, err)
	}
	return sys, nil
}

// InterfaceHints provides optional context when creating new interfaces.
type InterfaceHints struct {
	SystemID      string
	SystemSerial  string
	BoardSerial   string
	SystemVendor  string
	SystemModel   string
	InterfaceName string
}

// NormalizeMAC converts a MAC address to lowercase colon-separated form.
// Handles input formats: AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, AABB.CCDD.EEFF.
func NormalizeMAC(mac string) string {
	// Strip separators.
	mac = strings.ReplaceAll(mac, ":", "")
	mac = strings.ReplaceAll(mac, "-", "")
	mac = strings.ReplaceAll(mac, ".", "")
	mac = strings.ToLower(mac)

	if len(mac) != 12 {
		return mac
	}

	return mac[0:2] + ":" + mac[2:4] + ":" + mac[4:6] + ":" +
		mac[6:8] + ":" + mac[8:10] + ":" + mac[10:12]
}

// sourceFromFingerprints extracts a source string from the fingerprint list,
// falling back to empty string if none provided.
func sourceFromFingerprints(fps []Fingerprint) string {
	for _, fp := range fps {
		if fp.Source != "" {
			return fp.Source
		}
	}
	return ""
}
