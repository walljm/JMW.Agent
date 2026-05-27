package pipeline

import (
	"context"
	"fmt"
	"log/slog"

	"github.com/walljm/jmwagent/internal/server/store"
)

// Ingestor is the entry point for feeding data into the pipeline.
// It maps source kinds to adapters and dispatches through the pipeline.
type Ingestor struct {
	pipeline *Pipeline
	adapters map[string]Adapter
}

// NewIngestor creates an Ingestor with the given store and adapters.
// Panics if two adapters register the same Kind.
func NewIngestor(s *store.Store, adapters ...Adapter) *Ingestor {
	m := make(map[string]Adapter, len(adapters))
	for _, a := range adapters {
		if _, exists := m[a.Kind()]; exists {
			panic(fmt.Sprintf("ingestor: duplicate adapter kind %q", a.Kind()))
		}
		m[a.Kind()] = a
	}
	return &Ingestor{
		pipeline: New(s),
		adapters: m,
	}
}

// Ingest adapts a payload for the given kind and runs it through the pipeline.
func (ing *Ingestor) Ingest(ctx context.Context, kind string, sourceID string, payload any) (*Result, error) {
	adapter, ok := ing.adapters[kind]
	if !ok {
		return nil, fmt.Errorf("ingestor: no adapter for kind %q", kind)
	}

	inputs, err := adapter.Adapt(ctx, sourceID, payload)
	if err != nil {
		return nil, fmt.Errorf("ingestor: adapt %s: %w", kind, err)
	}

	if len(inputs) == 0 {
		slog.Debug("ingestor: adapter produced no observations", "kind", kind, "source", sourceID)
		return &Result{}, nil
	}

	result, err := ing.pipeline.Process(ctx, inputs)
	if err != nil {
		return nil, fmt.Errorf("ingestor: process %s: %w", kind, err)
	}

	slog.Debug("ingestor: batch complete",
		"kind", kind,
		"source", sourceID,
		"resolved", result.InterfacesResolved,
		"stored", result.ObservationsStored,
		"failures", result.Failures)

	return result, nil
}

// HasAdapter returns true if an adapter is registered for the given kind.
func (ing *Ingestor) HasAdapter(kind string) bool {
	_, ok := ing.adapters[kind]
	return ok
}
