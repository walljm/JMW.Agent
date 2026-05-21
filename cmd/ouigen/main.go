// Command ouigen downloads the IEEE OUI registries (MA-L, MA-M, MA-S, IAB),
// merges them into one normalized CSV, gzips it, and writes the result to
// the path given by -out. The output is consumed by the discover package
// via go:embed.
//
// Usage:
//
//	go run ./cmd/ouigen -out internal/agent/discover/oui_data.csv.gz
//
// The file format is plain CSV (one record per line):
//
//	<prefix-hex>,<bits>,<vendor>
//
// where <prefix-hex> is uppercase hex with no separators (6, 7, or 9 chars),
// <bits> is one of {24, 28, 36}, and <vendor> is the registrant name with
// commas/newlines stripped.
//
// Sources (IEEE):
//   - MA-L : https://standards-oui.ieee.org/oui/oui.csv      (24-bit, 6 hex)
//   - MA-M : https://standards-oui.ieee.org/oui28/mam.csv    (28-bit, 7 hex)
//   - MA-S : https://standards-oui.ieee.org/oui36/oui36.csv  (36-bit, 9 hex)
//   - IAB  : https://standards-oui.ieee.org/iab/iab.csv      (36-bit, 9 hex)
//
// IAB (Individual Address Block) is the legacy predecessor to MA-S — IEEE
// carved 36-bit assignments out of two parent OUIs (00:50:C2 and 00:1B:C5)
// before MA-S existed. Still actively in use, so we ingest it for coverage.
package main

import (
	"compress/gzip"
	"context"
	"encoding/csv"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"sort"
	"strings"
	"time"
)

type source struct {
	name string
	url  string
	bits int
}

var sources = []source{
	{"MA-L", "https://standards-oui.ieee.org/oui/oui.csv", 24},
	{"MA-M", "https://standards-oui.ieee.org/oui28/mam.csv", 28},
	{"MA-S", "https://standards-oui.ieee.org/oui36/oui36.csv", 36},
	{"IAB", "https://standards-oui.ieee.org/iab/iab.csv", 36},
}

type record struct {
	prefix string // uppercase hex, no separators
	bits   int
	vendor string
}

func main() {
	out := flag.String("out", "", "output path for gzipped CSV (required)")
	flag.Parse()
	if *out == "" {
		log.Fatal("-out is required")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()

	cli := &http.Client{Timeout: 60 * time.Second}
	all := make([]record, 0, 50000)
	for _, s := range sources {
		recs, err := fetch(ctx, cli, s)
		if err != nil {
			log.Fatalf("fetch %s: %v", s.name, err)
		}
		log.Printf("%s: %d entries", s.name, len(recs))
		all = append(all, recs...)
	}

	// Stable, prefix-sorted output so the embedded file diffs cleanly across
	// regenerations.
	sort.Slice(all, func(i, j int) bool {
		if all[i].prefix == all[j].prefix {
			return all[i].bits < all[j].bits
		}
		return all[i].prefix < all[j].prefix
	})

	if err := writeGzippedCSV(*out, all); err != nil {
		log.Fatalf("write %s: %v", *out, err)
	}
	log.Printf("wrote %s (%d records)", *out, len(all))
}

func fetch(ctx context.Context, cli *http.Client, s source) ([]record, error) {
	req, err := http.NewRequestWithContext(ctx, "GET", s.url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "jmw-ouigen/1.0")
	resp, err := cli.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("status %d", resp.StatusCode)
	}
	return parseIEEECSV(resp.Body, s.bits)
}

// parseIEEECSV reads the IEEE CSV format. Columns:
//
//	Registry, Assignment, Organization Name, Organization Address
//
// We only care about Assignment (column 1) and Organization Name (column 2).
// "Assignment" is hex without separators; for MA-S it's 9 chars, MA-M 7,
// MA-L/CID 6.
func parseIEEECSV(r io.Reader, bits int) ([]record, error) {
	rd := csv.NewReader(r)
	rd.FieldsPerRecord = -1 // tolerate extra columns
	header, err := rd.Read()
	if err != nil {
		return nil, fmt.Errorf("read header: %w", err)
	}
	asgIdx, orgIdx := -1, -1
	for i, h := range header {
		switch strings.TrimSpace(strings.ToLower(h)) {
		case "assignment":
			asgIdx = i
		case "organization name":
			orgIdx = i
		}
	}
	if asgIdx < 0 || orgIdx < 0 {
		return nil, fmt.Errorf("unexpected columns: %v", header)
	}

	expectLen := bits / 4 // hex chars
	var out []record
	seen := make(map[string]bool)
	for {
		row, err := rd.Read()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, err
		}
		if len(row) <= asgIdx || len(row) <= orgIdx {
			continue
		}
		prefix := normalize(row[asgIdx])
		vendor := cleanVendor(row[orgIdx])
		if len(prefix) != expectLen || vendor == "" {
			continue
		}
		// Dedupe (some registries occasionally have duplicate rows).
		key := prefix + "/" + vendor
		if seen[key] {
			continue
		}
		seen[key] = true
		out = append(out, record{prefix: prefix, bits: bits, vendor: vendor})
	}
	return out, nil
}

func normalize(s string) string {
	var b strings.Builder
	for _, r := range s {
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

func cleanVendor(s string) string {
	s = strings.ReplaceAll(s, "\n", " ")
	s = strings.ReplaceAll(s, "\r", " ")
	s = strings.ReplaceAll(s, ",", " ")
	s = strings.Join(strings.Fields(s), " ")
	if len(s) > 80 {
		s = s[:80]
	}
	return s
}

func writeGzippedCSV(path string, recs []record) error {
	f, err := os.Create(path)
	if err != nil {
		return err
	}
	defer f.Close()
	gz, err := gzip.NewWriterLevel(f, gzip.BestCompression)
	if err != nil {
		return err
	}
	w := csv.NewWriter(gz)
	for _, r := range recs {
		if err := w.Write([]string{r.prefix, fmt.Sprintf("%d", r.bits), r.vendor}); err != nil {
			return err
		}
	}
	w.Flush()
	if err := w.Error(); err != nil {
		return err
	}
	if err := gz.Close(); err != nil {
		return err
	}
	return f.Close()
}
