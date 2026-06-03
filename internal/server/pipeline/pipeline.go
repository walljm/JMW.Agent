package pipeline

import (
	"context"
	"fmt"
	"log/slog"
	"time"

	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/hostname"
)

// ObservationInput is the normalized input to the pipeline from an adapter.
type ObservationInput struct {
	MAC        string
	SourceID   string
	SourceKind string
	ObsType    string
	ObservedAt time.Time
	RawJSON    string

	// Optional enrichment data.
	//
	// Hostname + SourceKind: single-source adapters (e.g. terrain-dns) set
	// these directly. Normalization is applied before storage.
	//
	// HostnameSources: multi-source adapters (e.g. agent-discovery) populate
	// this map instead. Each entry is stored as a separate alias with the
	// priority of its own source kind, enabling accurate per-source ranking.
	Hostname        string
	HostnameSources map[string]string // source kind → raw hostname
	Addresses       []AddressInput
	Hints           *InterfaceHints

	// Fingerprints are identity markers extracted by the adapter. The resolver
	// uses these to find existing devices before creating new ones, and
	// registers any new fingerprints it discovers.
	Fingerprints []Fingerprint
}

// Fingerprint is a single identity marker for device resolution.
type Fingerprint struct {
	Kind   string // "mac", "serial:system", "serial:board", "docker_engine_id"
	Value  string // normalized value
	Source string // what reported it: "agent", "discovery", "terrain-dhcp", etc.
}

// AddressInput describes an IP address to associate with the interface.
type AddressInput struct {
	Address string // CIDR notation
	Family  string // ipv4 | ipv6
	Scope   string
}

// Result is returned after pipeline processing.
type Result struct {
	InterfacesResolved int
	ObservationsStored int
	HostnamesUpdated   int
	AddressesUpdated   int
	Failures           int
}

// Pipeline processes batches of observations through 5 stages:
//  1. Resolve — map MAC → Interface (creating Hardware if needed)
//  2. Observe — persist observation records
//  3. Enrich — attach addresses, hostnames, services
//  4. Derive — compute canonical hostname, update interface profile
//  5. Mark — update last_seen timestamps (already done via upsert)
type Pipeline struct {
	store *store.Store
}

// New creates a Pipeline backed by the given store.
func New(s *store.Store) *Pipeline {
	return &Pipeline{store: s}
}

// Process runs a batch of observations through all pipeline stages.
func (p *Pipeline) Process(ctx context.Context, inputs []ObservationInput) (*Result, error) {
	if len(inputs) == 0 {
		return &Result{}, nil
	}

	resolver := NewResolver(p.store)
	result := &Result{}
	touched := make(map[string]struct{})

	for i := range inputs {
		obs := &inputs[i]
		hardwareID, err := p.processOne(ctx, resolver, obs, result)
		if err != nil {
			slog.Warn("pipeline: observation failed",
				"mac", obs.MAC, "source", obs.SourceID, "error", err)
			result.Failures++
			// Continue processing remaining observations — don't fail the batch.
			continue
		}
		if hardwareID != "" {
			touched[hardwareID] = struct{}{}
		}
	}

	// Stage 4: Derive — runs once per hardware after the batch completes so
	// the classifier sees the latest signals all at once.
	for hwID := range touched {
		if err := DeriveDeviceKindForHardware(ctx, p.store, hwID); err != nil {
			slog.Warn("pipeline: derive device kind failed",
				"hardware_id", hwID, "error", err)
		}
	}

	return result, nil
}

func (p *Pipeline) processOne(ctx context.Context, resolver *Resolver, obs *ObservationInput, result *Result) (string, error) {
	// Stage 1: Resolve
	iface, err := resolver.ResolveInterface(ctx, obs.MAC, obs.Hints, obs.Fingerprints)
	if err != nil {
		return "", fmt.Errorf("stage resolve: %w", err)
	}
	result.InterfacesResolved++

	// Default ObservedAt to now if not set.
	observedAt := obs.ObservedAt
	if observedAt.IsZero() {
		observedAt = time.Now().UTC()
	}

	// Stage 2: Observe
	obsRecord := &store.Observation{
		InterfaceID: iface.ID,
		SourceID:    obs.SourceID,
		ObservedAt:  observedAt,
		ObsType:     obs.ObsType,
		RawJSON:     obs.RawJSON,
	}
	_, err = p.store.InsertObservation(ctx, obsRecord)
	if err != nil {
		return "", fmt.Errorf("stage observe: %w", err)
	}
	result.ObservationsStored++

	// Stage 3: Enrich — addresses and hostnames.
	for _, addr := range obs.Addresses {
		err = p.store.UpsertInterfaceAddress(ctx, &store.InterfaceAddress{
			InterfaceID: iface.ID,
			Address:     addr.Address,
			Family:      addr.Family,
			Scope:       addr.Scope,
		})
		if err != nil {
			return "", fmt.Errorf("stage enrich address %s: %w", addr.Address, err)
		}
		result.AddressesUpdated++
	}

	// Single-source hostname path (terrain-dns, agent-inventory, etc.)
	if n := hostname.Normalize(obs.Hostname); n != "" {
		if err = p.store.UpsertHostnameAlias(ctx, &store.EntityHostnameAlias{
			InterfaceID: iface.ID,
			Hostname:    n,
			SourceKind:  obs.SourceKind,
			Priority:    HostnamePriority(obs.SourceKind),
		}); err != nil {
			return "", fmt.Errorf("stage enrich hostname: %w", err)
		}
		result.HostnamesUpdated++
	}

	// Multi-source hostname path (agent-discovery with HostnameSources map).
	for srcKind, raw := range obs.HostnameSources {
		n := hostname.Normalize(raw)
		if n == "" {
			continue
		}
		if err = p.store.UpsertHostnameAlias(ctx, &store.EntityHostnameAlias{
			InterfaceID: iface.ID,
			Hostname:    n,
			SourceKind:  srcKind,
			Priority:    HostnamePriority(srcKind),
		}); err != nil {
			return "", fmt.Errorf("stage enrich hostname source %s: %w", srcKind, err)
		}
		result.HostnamesUpdated++
	}

	// Stage 4: Derive — handled once per hardware after the full batch (see
	// derive_kind.go). Stage 5: Mark — timestamps already updated by upsert
	// methods.

	return iface.HardwareID, nil
}
