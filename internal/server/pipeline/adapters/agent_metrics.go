package adapters

import (
	"context"
	"fmt"
	"time"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/server/store"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// AgentMetrics is a direct-write adapter: it writes metric snapshots directly
// to the store rather than producing pipeline Observations (metrics are
// time-series data, not entity-discovery events).
type AgentMetrics struct {
	Store *store.Store
}

func (a *AgentMetrics) Kind() string { return "agent-metrics" }

// Adapt writes snapshots directly and returns nil observations.
// The payload must be *AgentMetricsPayload.
func (a *AgentMetrics) Adapt(ctx context.Context, sourceID string, payload any) ([]pipeline.ObservationInput, error) {
	p, ok := payload.(*AgentMetricsPayload)
	if !ok {
		return nil, fmt.Errorf("agent-metrics adapter: expected *AgentMetricsPayload, got %T", payload)
	}

	// Write main + disk + interface snapshots (existing path).
	if err := a.Store.InsertSnapshots(ctx, p.AgentID, p.Snapshots); err != nil {
		return nil, fmt.Errorf("agent-metrics: insert snapshots: %w", err)
	}

	// Write expanded metrics (temperature, battery) from the latest snapshot.
	if len(p.Snapshots) > 0 {
		last := p.Snapshots[len(p.Snapshots)-1]
		ts := last.Timestamp
		if ts.IsZero() {
			ts = time.Now().UTC()
		}

		// Temperature readings (if the agent sent them alongside metrics).
		if len(p.Temperatures) > 0 {
			temps := make([]store.TempSnapshot, len(p.Temperatures))
			for i, t := range p.Temperatures {
				temps[i] = store.TempSnapshot{Sensor: t.Name, Celsius: t.Celsius}
			}
			_ = a.Store.InsertTemperatureSnapshots(ctx, p.AgentID, ts, temps)
		}

		// Battery reading.
		if p.Battery != nil {
			_ = a.Store.InsertBatterySnapshot(ctx, p.AgentID, ts,
				p.Battery.ChargePercent, p.Battery.State, p.Battery.HealthPercent)
		}
	}

	// Direct-write adapters return no observations.
	return nil, nil
}

// AgentMetricsPayload bundles metrics data for the adapter.
type AgentMetricsPayload struct {
	AgentID      string
	Snapshots    []proto.MetricSnapshot
	Temperatures []proto.TempReading
	Battery      *proto.Battery
}
