package hostname

import "testing"

func TestPriority_Ordering(t *testing.T) {
	// Infra records beat self-reported, which beats broadcast/passive.
	cases := []struct{ better, worse string }{
		{"user-input", "ldap"},
		{"ldap", "terrain-dns"},
		{"terrain-dns", "terrain-dhcp"},
		{"terrain-dhcp", "snmp"},
		{"snmp", "smb"},
		{"smb", "dhcp"},
		{"dhcp", "agent"},
		{"agent", "llmnr"},
		{"llmnr", "mdns"},
		{"mdns", "docker"},
		{"docker", "rdns"},
		{"rdns", "http"},
	}
	for _, tc := range cases {
		b, w := Priority(tc.better), Priority(tc.worse)
		if b >= w {
			t.Errorf("Priority(%q)=%d should be < Priority(%q)=%d", tc.better, b, tc.worse, w)
		}
	}
}

func TestPriority_Unknown(t *testing.T) {
	if p := Priority("totally-unknown"); p != 100 {
		t.Errorf("unknown source priority = %d, want 100", p)
	}
}

func TestNormalize(t *testing.T) {
	cases := []struct {
		in   string
		want string
	}{
		// .local stripping
		{"mydevice.local", "mydevice"},
		{"MyDevice.local.", "mydevice"},
		{"MacBook.local", "macbook"},
		// Docker-internal rejection
		{"host.docker.internal", ""},
		{"gateway.docker.internal", ""},
		{"docker.internal", ""},
		// Generic OS names
		{"localhost", ""},
		{"ubuntu", ""},
		{"raspberrypi", ""},
		{"homeassistant", ""},
		// Docker container ID (12-char hex)
		{"a1b2c3d4e5f6", ""},
		// Short names
		{"x", ""},
		{"", ""},
		// IP address
		{"192.168.1.1", ""},
		{"::1", ""},
		// Trailing dot (DNS FQDN)
		{"server01.", "server01"},
		// Normal names
		{"webserver01", "webserver01"},
		{"walljm-win-tryc", "walljm-win-tryc"},
		// Uppercase normalized
		{"MYPC", "mypc"},
		// Whitespace trimmed
		{"  server  ", "server"},
		// HTML entities decoded
		{"hp laserjet m209dw&nbsp;&nbsp;&nbsp;192.168.1.242", "hp laserjet m209dw   192.168.1.242"},
		{"&amp;router", "&router"},
		{"My&nbsp;Printer", "my printer"},
	}
	for _, tc := range cases {
		got := Normalize(tc.in)
		if got != tc.want {
			t.Errorf("Normalize(%q) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

func TestIsUsable(t *testing.T) {
	if !IsUsable("myserver") {
		t.Error("myserver should be usable")
	}
	if IsUsable("localhost") {
		t.Error("localhost should not be usable")
	}
	if IsUsable("host.docker.internal") {
		t.Error("host.docker.internal should not be usable")
	}
}
