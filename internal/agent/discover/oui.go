package discover

import (
	"bytes"
	"compress/gzip"
	"encoding/csv"
	_ "embed"
	"io"
	"strings"
	"sync"
)

// Regenerate oui_data.csv.gz when needed:
//
//	go run ./cmd/ouigen -out internal/agent/discover/oui_data.csv.gz
//
//go:generate go run ../../../cmd/ouigen -out oui_data.csv.gz

//go:embed oui_data.csv.gz
var ouiCSVGz []byte

// Three lookup tables, one per IEEE block size. We keep them separate so a
// single Get can try the longest prefix first without having to scan keys
// of mixed lengths.
var (
	ouiOnce  sync.Once
	ouiMAS   map[string]string // 9 hex chars (MA-S, /36)
	ouiMAM   map[string]string // 7 hex chars (MA-M, /28)
	ouiMAL   map[string]string // 6 hex chars (MA-L + CID, /24)
	ouiReady bool
)

// ouiLookup returns a vendor name for a MAC address using IEEE registry
// data baked into the binary at build time. Returns "" if the MAC is
// malformed or no prefix matches. Tries the most-specific block (MA-S,
// 36-bit) first, then MA-M (28-bit), then MA-L/CID (24-bit).
func ouiLookup(mac string) string {
	ouiOnce.Do(loadOUI)
	if !ouiReady {
		return ""
	}
	hex := ouiNormalizeMAC(mac)
	if len(hex) < 6 {
		return ""
	}
	if len(hex) >= 9 {
		if v, ok := ouiMAS[hex[:9]]; ok {
			return v
		}
	}
	if len(hex) >= 7 {
		if v, ok := ouiMAM[hex[:7]]; ok {
			return v
		}
	}
	if v, ok := ouiMAL[hex[:6]]; ok {
		return v
	}
	return ""
}

// ouiNormalizeMAC strips separators and uppercases hex chars from a MAC.
func ouiNormalizeMAC(mac string) string {
	var b strings.Builder
	b.Grow(12)
	for _, r := range mac {
		switch {
		case r >= '0' && r <= '9':
			b.WriteRune(r)
		case r >= 'A' && r <= 'F':
			b.WriteRune(r)
		case r >= 'a' && r <= 'f':
			b.WriteRune(r - 32)
		}
	}
	return b.String()
}

// loadOUI decompresses the embedded CSV and populates the lookup maps.
// Called exactly once via ouiOnce. On any error the maps stay empty and
// ouiReady stays false, so ouiLookup just returns "" — discovery still
// works without vendor data.
func loadOUI() {
	if len(ouiCSVGz) == 0 {
		return
	}
	gz, err := gzip.NewReader(bytes.NewReader(ouiCSVGz))
	if err != nil {
		return
	}
	defer gz.Close()

	ouiMAS = make(map[string]string, 4096)
	ouiMAM = make(map[string]string, 4096)
	ouiMAL = make(map[string]string, 32768)

	r := csv.NewReader(gz)
	r.FieldsPerRecord = 3
	for {
		row, err := r.Read()
		if err == io.EOF {
			break
		}
		if err != nil {
			return
		}
		prefix, bits, vendor := row[0], row[1], row[2]
		switch bits {
		case "36":
			if len(prefix) == 9 {
				ouiMAS[prefix] = vendor
			}
		case "28":
			if len(prefix) == 7 {
				ouiMAM[prefix] = vendor
			}
		case "24":
			if len(prefix) == 6 {
				ouiMAL[prefix] = vendor
			}
		}
	}
	ouiReady = true
}
