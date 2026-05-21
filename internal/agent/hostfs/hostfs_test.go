package hostfs

import "testing"

func TestPath(t *testing.T) {
	tests := []struct {
		name, root, in, want string
	}{
		{"empty root passthrough", "", "/proc/meminfo", "/proc/meminfo"},
		{"prefix applied", "/host", "/proc/meminfo", "/host/proc/meminfo"},
		{"trailing slash trimmed", "/host/", "/sys/block", "/host/sys/block"},
		{"non-absolute returned unchanged", "/host", "relative", "relative"},
		{"empty input returned unchanged", "/host", "", ""},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// Override package state for this test.
			saved := root
			defer func() { root = saved }()
			root = trimRight(tt.root)
			if got := Path(tt.in); got != tt.want {
				t.Fatalf("Path(%q) with root=%q = %q, want %q", tt.in, tt.root, got, tt.want)
			}
		})
	}
}

// trimRight mirrors the package-init normalization without re-reading the env.
func trimRight(s string) string {
	for len(s) > 0 && s[len(s)-1] == '/' {
		s = s[:len(s)-1]
	}
	return s
}
