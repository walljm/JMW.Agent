package pipeline

import "context"

// Adapter translates a source-specific payload into pipeline ObservationInputs.
// Each adapter handles one source kind (e.g., "agent-discovery", "terrain-dhcp").
type Adapter interface {
	// Kind returns the source kind this adapter handles.
	Kind() string

	// Adapt translates a source-specific payload into ObservationInputs.
	// The sourceID identifies which Source row produced this data.
	Adapt(ctx context.Context, sourceID string, payload any) ([]ObservationInput, error)
}
