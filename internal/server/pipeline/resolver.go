// Package pipeline implements the 5-stage entity resolution and observation pipeline.
package pipeline

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"log/slog"
	"strings"

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
// with the earliest first_seen_at as the survivor. Reassigns all fingerprints,
// interfaces, and systems from the losers to the survivor, then deletes the losers.
func (r *Resolver) mergeDevices(ctx context.Context, hits map[string]store.FingerprintInput) (string, error) {
	// Collect all hardware IDs and find the oldest.
	hwIDs := make([]string, 0, len(hits))
	for hwID := range hits {
		hwIDs = append(hwIDs, hwID)
	}

	var survivorID string
	var oldestSeen string
	for _, hwID := range hwIDs {
		hw, err := r.store.GetHardware(ctx, hwID)
		if err != nil {
			return "", fmt.Errorf("get hardware %s: %w", hwID, err)
		}
		seen := hw.FirstSeenAt.Format("2006-01-02T15:04:05Z07:00")
		if survivorID == "" || seen < oldestSeen {
			survivorID = hwID
			oldestSeen = seen
		}
	}

	// Reassign everything from the losers to the survivor.
	for _, hwID := range hwIDs {
		if hwID == survivorID {
			continue
		}

		slog.Info("resolver: merging device",
			"from", hwID, "to", survivorID,
			"trigger", hits[hwID].Kind+":"+hits[hwID].Value)

		if err := r.store.MergeHardwareMetadata(ctx, survivorID, hwID); err != nil {
			return "", fmt.Errorf("merge hardware metadata from %s: %w", hwID, err)
		}
		if err := r.store.ReassignFingerprints(ctx, hwID, survivorID); err != nil {
			return "", fmt.Errorf("reassign fingerprints from %s: %w", hwID, err)
		}
		if err := r.store.ReassignInterfaces(ctx, hwID, survivorID); err != nil {
			return "", fmt.Errorf("reassign interfaces from %s: %w", hwID, err)
		}
		if err := r.store.ReassignSystems(ctx, hwID, survivorID); err != nil {
			return "", fmt.Errorf("reassign systems from %s: %w", hwID, err)
		}
		if err := r.store.DeleteHardware(ctx, hwID); err != nil {
			return "", fmt.Errorf("delete merged hardware %s: %w", hwID, err)
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
