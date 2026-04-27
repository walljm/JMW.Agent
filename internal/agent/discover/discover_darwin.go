//go:build darwin

package discover

import (
	"os/exec"
	"regexp"
	"strings"
)

var arpLineRe = regexp.MustCompile(`\((\d+\.\d+\.\d+\.\d+)\) at ([0-9a-f:]+)`)

// scanARP shells out to arp -an.
func scanARP() []Sighting {
	out, err := exec.Command("arp", "-an").Output()
	if err != nil {
		return nil
	}
	var sightings []Sighting
	for _, line := range strings.Split(string(out), "\n") {
		m := arpLineRe.FindStringSubmatch(line)
		if len(m) != 3 {
			continue
		}
		mac := normalizeMAC(m[2])
		if mac == "" || mac == "ff:ff:ff:ff:ff:ff" {
			continue
		}
		sightings = append(sightings, Sighting{IP: m[1], MAC: mac})
	}
	return sightings
}

// normalizeMAC pads single-digit hex parts to two and lowercases.
func normalizeMAC(m string) string {
	parts := strings.Split(m, ":")
	if len(parts) != 6 {
		return ""
	}
	for i, p := range parts {
		if len(p) == 1 {
			parts[i] = "0" + p
		}
	}
	return strings.ToLower(strings.Join(parts, ":"))
}
