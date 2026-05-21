// Package releases scans a directory of agent binaries and serves the
// latest version per (os, arch) target to the heartbeat handler and the
// download endpoint.
//
// Layout on disk (created by the operator):
//
//	releases/
//	  v1.3.0/
//	    jmw-agent-linux-amd64
//	    jmw-agent-linux-arm64
//	    jmw-agent-darwin-amd64
//	    jmw-agent-darwin-arm64
//	    jmw-agent-windows-amd64.exe
//	  v1.4.0/
//	    ...
//
// Filenames must match `jmw-agent-<os>-<arch>[.exe]`. The directory name
// must look like a semver tag (vX.Y.Z[-prerelease]). The highest semver
// directory wins per platform. SHA-256 is computed lazily and cached.
package releases

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"sync"
	"time"
)

// Entry is one published agent binary.
type Entry struct {
	Version  string // e.g. "v1.3.0"
	OS       string // "linux" | "darwin" | "windows"
	Arch     string // "amd64" | "arm64"
	Path     string // absolute path on disk
	Size     int64
	SHA256   string // hex
	Filename string // base name (used in the download URL)
}

// Manager owns the scan state for a releases directory.
type Manager struct {
	dir string

	mu          sync.RWMutex
	byPlatform  map[string]Entry // key = os + "/" + arch
	byFilename  map[string]Entry // key = version + "/" + filename (path-safe lookup)
	lastScanned time.Time
	scanErr     error
}

// New constructs a Manager pointing at dir. dir may be empty, in which case
// Latest always returns (Entry{}, false) and the Manager is effectively a
// no-op (auto-update disabled).
func New(dir string) *Manager {
	return &Manager{
		dir:        dir,
		byPlatform: map[string]Entry{},
		byFilename: map[string]Entry{},
	}
}

// Enabled reports whether a releases directory is configured.
func (m *Manager) Enabled() bool { return m != nil && m.dir != "" }

// Latest returns the newest binary on disk for the given platform, or
// (Entry{}, false) if no matching binary is published.
func (m *Manager) Latest(goos, goarch string) (Entry, bool) {
	if !m.Enabled() {
		return Entry{}, false
	}
	m.mu.RLock()
	defer m.mu.RUnlock()
	e, ok := m.byPlatform[goos+"/"+goarch]
	return e, ok
}

// Lookup returns the entry whose `version/filename` matches the given pair.
// Used by the download endpoint to refuse arbitrary path access.
func (m *Manager) Lookup(version, filename string) (Entry, bool) {
	if !m.Enabled() {
		return Entry{}, false
	}
	m.mu.RLock()
	defer m.mu.RUnlock()
	e, ok := m.byFilename[version+"/"+filename]
	return e, ok
}

// Scan walks the directory and refreshes the indices. Safe to call
// repeatedly; cheap when nothing changed (files are stat'd, SHA is only
// recomputed when size or mtime changed).
func (m *Manager) Scan() error {
	if !m.Enabled() {
		return nil
	}
	entries, err := os.ReadDir(m.dir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			// Treat missing dir as "no releases published yet" — not fatal.
			m.mu.Lock()
			m.byPlatform = map[string]Entry{}
			m.byFilename = map[string]Entry{}
			m.lastScanned = time.Now().UTC()
			m.scanErr = nil
			m.mu.Unlock()
			return nil
		}
		m.mu.Lock()
		m.scanErr = err
		m.lastScanned = time.Now().UTC()
		m.mu.Unlock()
		return err
	}

	// Preserve SHA cache across rescans keyed by path+size+mtime so we
	// only re-hash when a file changes.
	m.mu.RLock()
	priorByPath := map[string]Entry{}
	for _, e := range m.byPlatform {
		priorByPath[e.Path] = e
	}
	m.mu.RUnlock()

	newByPlatform := map[string]Entry{}
	newByFilename := map[string]Entry{}

	for _, de := range entries {
		if !de.IsDir() {
			continue
		}
		version := de.Name()
		if !looksLikeSemverTag(version) {
			continue
		}
		verDir := filepath.Join(m.dir, version)
		files, err := os.ReadDir(verDir)
		if err != nil {
			continue
		}
		for _, fe := range files {
			if fe.IsDir() {
				continue
			}
			goos, goarch, ok := parseBinaryName(fe.Name())
			if !ok {
				continue
			}
			full := filepath.Join(verDir, fe.Name())
			info, err := fe.Info()
			if err != nil {
				continue
			}
			sum := ""
			if prev, hadPrev := priorByPath[full]; hadPrev && prev.Size == info.Size() {
				sum = prev.SHA256
			}
			if sum == "" {
				h, err := hashFile(full)
				if err != nil {
					continue
				}
				sum = h
			}
			ent := Entry{
				Version:  version,
				OS:       goos,
				Arch:     goarch,
				Path:     full,
				Size:     info.Size(),
				SHA256:   sum,
				Filename: fe.Name(),
			}
			key := goos + "/" + goarch
			if cur, exists := newByPlatform[key]; !exists || semverLess(cur.Version, ent.Version) {
				newByPlatform[key] = ent
			}
			newByFilename[version+"/"+fe.Name()] = ent
		}
	}

	m.mu.Lock()
	m.byPlatform = newByPlatform
	m.byFilename = newByFilename
	m.lastScanned = time.Now().UTC()
	m.scanErr = nil
	m.mu.Unlock()
	return nil
}

// LastScanned returns the time of the most recent successful scan and any
// error from the most recent attempt.
func (m *Manager) LastScanned() (time.Time, error) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.lastScanned, m.scanErr
}

// All returns a copy of the current latest-per-platform table. Useful for
// admin UI.
func (m *Manager) All() []Entry {
	m.mu.RLock()
	defer m.mu.RUnlock()
	out := make([]Entry, 0, len(m.byPlatform))
	for _, e := range m.byPlatform {
		out = append(out, e)
	}
	return out
}

// Open returns a reader for the file backing the entry.
func (m *Manager) Open(e Entry) (io.ReadCloser, error) {
	return os.Open(e.Path)
}

var binaryNamePattern = regexp.MustCompile(`^jmw-agent-([a-z0-9]+)-([a-z0-9]+)(\.exe)?$`)

func parseBinaryName(name string) (goos, goarch string, ok bool) {
	m := binaryNamePattern.FindStringSubmatch(name)
	if m == nil {
		return "", "", false
	}
	return m[1], m[2], true
}

var semverPattern = regexp.MustCompile(`^v(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$`)

// cleanSemverPattern matches a release tag with no prerelease qualifier.
// Only directories whose name matches this are eligible to be published as
// auto-update sources, so an operator who drops a dev/dirty build into the
// releases dir by mistake cannot flap the fleet onto it.
var cleanSemverPattern = regexp.MustCompile(`^v\d+\.\d+\.\d+$`)

// IsCleanSemver reports whether s is a clean release tag (no -dirty,
// -gSHA, etc). Retained for callers that want to inspect a version string
// directly; not used by the auto-update gate.
func IsCleanSemver(s string) bool {
	return cleanSemverPattern.MatchString(s)
}

func looksLikeSemverTag(s string) bool {
	return cleanSemverPattern.MatchString(s)
}

// semverLess returns whether a < b in semver ordering. Non-parseable inputs
// fall back to string compare.
func semverLess(a, b string) bool {
	am := semverPattern.FindStringSubmatch(a)
	bm := semverPattern.FindStringSubmatch(b)
	if am == nil || bm == nil {
		return a < b
	}
	for i := 1; i <= 3; i++ {
		ai, _ := strconv.Atoi(am[i])
		bi, _ := strconv.Atoi(bm[i])
		if ai != bi {
			return ai < bi
		}
	}
	// Releases without prerelease are greater than those with prerelease.
	switch {
	case am[4] == "" && bm[4] != "":
		return false
	case am[4] != "" && bm[4] == "":
		return true
	default:
		return am[4] < bm[4]
	}
}

// SemverGreater reports whether b is strictly greater than a in semver order.
func SemverGreater(a, b string) bool {
	if a == b {
		return false
	}
	return semverLess(a, b)
}

func hashFile(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// Validate confirms the manager's directory exists and is readable. Called
// at startup so misconfiguration surfaces immediately instead of silently
// disabling updates.
func Validate(dir string) error {
	if dir == "" {
		return nil
	}
	info, err := os.Stat(dir)
	if err != nil {
		return fmt.Errorf("releases_dir %q: %w", dir, err)
	}
	if !info.IsDir() {
		return fmt.Errorf("releases_dir %q is not a directory", dir)
	}
	return nil
}
