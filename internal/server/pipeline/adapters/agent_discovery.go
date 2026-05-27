package adapters

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/walljm/jmwagent/internal/server/pipeline"
	"github.com/walljm/jmwagent/internal/shared/proto"
)

// AgentDiscovery adapts proto.DiscoveryRequest into pipeline observations.
type AgentDiscovery struct{}

func (a *AgentDiscovery) Kind() string { return "agent-discovery" }

func (a *AgentDiscovery) Adapt(_ context.Context, sourceID string, payload any) ([]pipeline.ObservationInput, error) {
	req, ok := payload.(*proto.DiscoveryRequest)
	if !ok {
		return nil, fmt.Errorf("agent-discovery adapter: expected *proto.DiscoveryRequest, got %T", payload)
	}

	inputs := make([]pipeline.ObservationInput, 0, len(req.Sightings))
	for i := range req.Sightings {
		s := &req.Sightings[i]
		if s.MAC == "" {
			continue // can't resolve without MAC
		}

		observedAt := s.SeenAt
		if observedAt.IsZero() {
			observedAt = time.Now().UTC()
		}

		raw, _ := json.Marshal(s)

		obs := pipeline.ObservationInput{
			MAC:        s.MAC,
			SourceID:   sourceID,
			SourceKind: "agent",
			ObsType:    "discovery",
			ObservedAt: observedAt,
			RawJSON:    string(raw),
			Hostname:   s.Hostname,
		}

		// Add IP as an address if present.
		if s.IP != "" {
			family := "ipv4"
			if isIPv6(s.IP) {
				family = "ipv6"
			}
			obs.Addresses = []pipeline.AddressInput{
				{Address: s.IP, Family: family},
			}
		}

		inputs = append(inputs, obs)
	}

	return inputs, nil
}

func isIPv6(ip string) bool {
	for _, c := range ip {
		if c == ':' {
			return true
		}
	}
	return false
}
