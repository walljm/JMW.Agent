package oui

import (
	"bytes"
	"compress/gzip"
	_ "embed"
	"encoding/csv"
	"io"
	"strings"
	"sync"
)

// Regenerate oui_data.csv.gz when needed:
//
//	go run ./cmd/ouigen -out internal/shared/oui/oui_data.csv.gz
//
//go:embed oui_data.csv.gz
var ouiCSVGz []byte

var (
	once  sync.Once
	mas   map[string]string // 9 hex chars (MA-S, /36)
	mam   map[string]string // 7 hex chars (MA-M, /28)
	mal   map[string]string // 6 hex chars (MA-L + CID, /24)
	ready bool
)

// Lookup returns a vendor name for a MAC address using IEEE registry data
// baked into the binary at build time. Returns "" if the MAC is malformed
// or no prefix matches. Tries the most-specific block (MA-S, 36-bit) first,
// then MA-M (28-bit), then MA-L/CID (24-bit).
func Lookup(mac string) string {
	once.Do(load)
	if !ready {
		return ""
	}
	hex := normalizeMAC(mac)
	if len(hex) < 6 {
		return ""
	}
	if len(hex) >= 9 {
		if v, ok := mas[hex[:9]]; ok {
			return v
		}
	}
	if len(hex) >= 7 {
		if v, ok := mam[hex[:7]]; ok {
			return v
		}
	}
	if v, ok := mal[hex[:6]]; ok {
		return v
	}
	return ""
}

func normalizeMAC(mac string) string {
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

func load() {
	if len(ouiCSVGz) == 0 {
		return
	}
	gz, err := gzip.NewReader(bytes.NewReader(ouiCSVGz))
	if err != nil {
		return
	}
	defer gz.Close()

	mas = make(map[string]string, 4096)
	mam = make(map[string]string, 4096)
	mal = make(map[string]string, 32768)

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
				mas[prefix] = vendor
			}
		case "28":
			if len(prefix) == 7 {
				mam[prefix] = vendor
			}
		case "24":
			if len(prefix) == 6 {
				mal[prefix] = vendor
			}
		}
	}
	ready = true
}
