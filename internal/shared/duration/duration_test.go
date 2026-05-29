package duration

import (
	"testing"
	"time"
)

func TestParse(t *testing.T) {
	tests := []struct {
		input string
		want  time.Duration
	}{
		{"30s", 30 * time.Second},
		{"5m", 5 * time.Minute},
		{"2h", 2 * time.Hour},
		{"1d", 24 * time.Hour},
		{"1d 3h 5m 30s", 24*time.Hour + 3*time.Hour + 5*time.Minute + 30*time.Second},
		{"1d3h5m30s", 24*time.Hour + 3*time.Hour + 5*time.Minute + 30*time.Second},
		{"1d 30s", 24*time.Hour + 30*time.Second},
		{"90d", 90 * 24 * time.Hour},
		{"365d", 365 * 24 * time.Hour},
		{"7d", 7 * 24 * time.Hour},
		{"48h", 48 * time.Hour},
		{"2160h", 2160 * time.Hour},
	}
	for _, tt := range tests {
		got, err := Parse(tt.input)
		if err != nil {
			t.Errorf("Parse(%q) error: %v", tt.input, err)
			continue
		}
		if got != tt.want {
			t.Errorf("Parse(%q) = %v, want %v", tt.input, got, tt.want)
		}
	}
}

func TestParseErrors(t *testing.T) {
	tests := []string{
		"",
		"abc",
		"5",
		"5x",
		"d",
		"1d 2",
	}
	for _, input := range tests {
		_, err := Parse(input)
		if err == nil {
			t.Errorf("Parse(%q) expected error, got nil", input)
		}
	}
}

func TestFormat(t *testing.T) {
	tests := []struct {
		input time.Duration
		want  string
	}{
		{0, "0s"},
		{30 * time.Second, "30s"},
		{5 * time.Minute, "5m"},
		{2 * time.Hour, "2h"},
		{24 * time.Hour, "1d"},
		{48 * time.Hour, "2d"},
		{90 * 24 * time.Hour, "90d"},
		{365 * 24 * time.Hour, "365d"},
		{7 * 24 * time.Hour, "7d"},
		{24*time.Hour + 3*time.Hour + 5*time.Minute + 30*time.Second, "1d 3h 5m 30s"},
		{24*time.Hour + 30*time.Second, "1d 30s"},
		{2*time.Hour + 30*time.Minute, "2h 30m"},
		{25 * time.Hour, "1d 1h"},
		{2160 * time.Hour, "90d"},
		{720 * time.Hour, "30d"},
	}
	for _, tt := range tests {
		got := Format(tt.input)
		if got != tt.want {
			t.Errorf("Format(%v) = %q, want %q", tt.input, got, tt.want)
		}
	}
}

func TestRoundTrip(t *testing.T) {
	durations := []time.Duration{
		30 * time.Second,
		5 * time.Minute,
		24 * time.Hour,
		90 * 24 * time.Hour,
		24*time.Hour + 3*time.Hour + 5*time.Minute + 30*time.Second,
	}
	for _, d := range durations {
		s := Format(d)
		got, err := Parse(s)
		if err != nil {
			t.Errorf("roundtrip Format(%v)=%q then Parse error: %v", d, s, err)
			continue
		}
		if got != d {
			t.Errorf("roundtrip Format(%v)=%q Parse=%v", d, s, got)
		}
	}
}
