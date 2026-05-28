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
		{"user-input", 1},
		{"ldap", 3},
		{"terrain-dns", 5},
		{"terrain-dhcp", 8},
		{"snmp", 12},
		{"snmp-poller", 12},
		{"smb", 15},
		{"dhcp", 20},
		{"agent", 25},
		{"mdns", 40},
		{"docker", 50},
		{"rdns", 80},
		{"nmap-scanner", 90},
		{"unknown-source", 100},
	}
	for _, tc := range cases {
		got := HostnamePriority(tc.kind)
		if got != tc.expected {
			t.Errorf("HostnamePriority(%q) = %d, want %d", tc.kind, got, tc.expected)
		}
	}
}

func TestHostnamePriority_InfrastructureBeatsAgent(t *testing.T) {
	// terrain-dns (5) and terrain-dhcp (8) should beat agent (25).
	if HostnamePriority("terrain-dns") >= HostnamePriority("agent") {
		t.Fatal("terrain-dns priority should be numerically lower than agent")
	}
	if HostnamePriority("terrain-dhcp") >= HostnamePriority("agent") {
		t.Fatal("terrain-dhcp priority should be numerically lower than agent")
	}
	// mdns (40) and docker (50) should lose to agent (25).
	if HostnamePriority("mdns") <= HostnamePriority("agent") {
		t.Fatal("mdns priority should be numerically higher than agent")
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
