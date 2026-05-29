// Package duration provides human-friendly duration parsing and formatting.
//
// Format: "1d 3h 5m 30s" — any combination of d/h/m/s tokens, whitespace
// optional. Formatting always rolls up to the largest whole unit and omits
// zero-value components.
package duration

import (
	"fmt"
	"strings"
	"time"
	"unicode"
)

var units = []struct {
	suffix byte
	dur    time.Duration
}{
	{'d', 24 * time.Hour},
	{'h', time.Hour},
	{'m', time.Minute},
	{'s', time.Second},
}

// Parse converts a human duration string into a time.Duration.
// Accepted formats:
//   - "1d 3h 5m 30s" (any subset, any order, whitespace optional)
//   - "48h", "5m30s"  (Go duration strings without days)
//   - Tokens: d=days(24h), h=hours, m=minutes, s=seconds
func Parse(s string) (time.Duration, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		return 0, fmt.Errorf("empty duration string")
	}

	var total time.Duration
	var numBuf strings.Builder
	found := false

	for i := 0; i < len(s); i++ {
		c := s[i]
		if c >= '0' && c <= '9' {
			numBuf.WriteByte(c)
			continue
		}
		if unicode.IsSpace(rune(c)) {
			continue
		}

		if numBuf.Len() == 0 {
			return 0, fmt.Errorf("unexpected character %q at position %d", c, i)
		}

		var n int
		for _, ch := range []byte(numBuf.String()) {
			n = n*10 + int(ch-'0')
		}
		numBuf.Reset()

		matched := false
		for _, u := range units {
			if c == u.suffix {
				total += time.Duration(n) * u.dur
				matched = true
				found = true
				break
			}
		}
		if !matched {
			return 0, fmt.Errorf("unknown unit %q at position %d (expected d, h, m, or s)", c, i)
		}
	}

	if numBuf.Len() > 0 {
		return 0, fmt.Errorf("trailing number %q without a unit (expected d, h, m, or s)", numBuf.String())
	}
	if !found {
		return 0, fmt.Errorf("no duration tokens found in %q", s)
	}
	return total, nil
}

// Format renders a time.Duration as a human-friendly string, rolling up to
// the largest whole unit and omitting zero-value components.
// Examples: 90d, 1d 3h, 2h 30m, 45s, 1d 30s.
func Format(d time.Duration) string {
	if d <= 0 {
		return "0s"
	}

	rem := d
	var parts []string
	for _, u := range units {
		if rem >= u.dur {
			n := rem / u.dur
			rem -= n * u.dur
			parts = append(parts, fmt.Sprintf("%d%c", n, u.suffix))
		}
	}
	if len(parts) == 0 {
		return "0s"
	}
	return strings.Join(parts, " ")
}
