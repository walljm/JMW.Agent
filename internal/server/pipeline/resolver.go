// Package pipeline implements the 5-stage entity resolution and observation pipeline.
package pipeline

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
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

// ResolveInterface resolves a MAC address to an Interface entity.
// If the MAC is unknown, it creates a new Hardware + Interface pair.
// The optional hardwareID/systemID fields can pre-associate the interface.
func (r *Resolver) ResolveInterface(ctx context.Context, mac string, hints *InterfaceHints) (*store.Interface, error) {
	if mac == "" {
		return nil, fmt.Errorf("resolver: empty MAC address")
	}

	// Normalize MAC to lowercase colon-separated form.
	mac = NormalizeMAC(mac)

	// Check batch cache first.
	if cached, ok := r.macCache[mac]; ok {
		return cached, nil
	}

	// Try to find existing interface. Touch last_seen_at so the device card
	// reflects real activity rather than the creation timestamp.
	iface, err := r.store.GetInterfaceByMAC(ctx, mac)
	if err == nil {
		_ = r.store.TouchInterface(ctx, iface.ID)
		r.macCache[mac] = iface
		return iface, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return nil, fmt.Errorf("resolver: lookup MAC %s: %w", mac, err)
	}

	// Unknown MAC — create a new hardware chassis and interface.
	hw := &store.Hardware{}
	if hints != nil {
		hw.SystemSerial = hints.SystemSerial
		hw.BoardSerial = hints.BoardSerial
		hw.SystemVendor = hints.SystemVendor
		hw.SystemModel = hints.SystemModel
	}
	hwID, err := r.store.UpsertHardware(ctx, hw)
	if err != nil {
		return nil, fmt.Errorf("resolver: create hardware for MAC %s: %w", mac, err)
	}

	iface = &store.Interface{
		HardwareID: hwID,
		MAC:        mac,
	}
	if hints != nil {
		iface.Name = hints.InterfaceName
		iface.SystemID = hints.SystemID
	}
	_, err = r.store.UpsertInterface(ctx, iface)
	if err != nil {
		return nil, fmt.Errorf("resolver: create interface for MAC %s: %w", mac, err)
	}

	// Re-fetch to ensure cached struct reflects DB truth (handles upsert conflict).
	iface, err = r.store.GetInterfaceByMAC(ctx, mac)
	if err != nil {
		return nil, fmt.Errorf("resolver: re-fetch MAC %s: %w", mac, err)
	}

	r.macCache[mac] = iface
	return iface, nil
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
		// Not a valid MAC length — return as-is (lowercased).
		return mac
	}

	// Re-insert colons.
	return mac[0:2] + ":" + mac[2:4] + ":" + mac[4:6] + ":" +
		mac[6:8] + ":" + mac[8:10] + ":" + mac[10:12]
}
