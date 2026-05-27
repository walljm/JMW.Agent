package pipeline

import (
	"testing"
	"time"
)

func TestHostnamePriority(t *testing.T) {
	cases := []struct {
		kind     string
		expected int
	}{
		{"agent", 10},
		{"user-input", 20},
		{"terrain-dns", 30},
		{"terrain-dhcp", 40},
		{"snmp-poller", 50},
		{"nmap-scanner", 60},
		{"unknown-source", 100},
	}
	for _, tc := range cases {
		got := HostnamePriority(tc.kind)
		if got != tc.expected {
			t.Errorf("HostnamePriority(%q) = %d, want %d", tc.kind, got, tc.expected)
		}
	}
}

func TestHostnamePriority_AgentWins(t *testing.T) {
	// Agent (10) should be lower (higher priority) than DHCP (40).
	if HostnamePriority("agent") >= HostnamePriority("terrain-dhcp") {
		t.Fatal("agent priority should be numerically lower than terrain-dhcp")
	}
}

func TestIsStale(t *testing.T) {
	now := time.Now()

	// Fresh — seen 1 hour ago.
	if IsStale(now.Add(-1 * time.Hour)) {
		t.Fatal("1 hour ago should not be stale")
	}

	// Stale — seen 8 days ago.
	if !IsStale(now.Add(-8 * 24 * time.Hour)) {
		t.Fatal("8 days ago should be stale")
	}

	// Boundary — seen just under 7 days ago (not stale).
	justUnder := now.Add(-StaleThreshold + time.Minute)
	if IsStale(justUnder) {
		t.Fatal("just under 7 days should not be stale")
	}
}
