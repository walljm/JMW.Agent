// Package store wraps the SQLite database and provides repositories.
package store

import (
	"context"
	"database/sql"
	"embed"
	"fmt"
	"io/fs"
	"log/slog"
	"sort"
	"strconv"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

//go:embed migrations/*.sql
var migrationFS embed.FS

// Store is the database handle.
type Store struct {
	DB *sql.DB
}

// Open opens (or creates) the SQLite database at path and applies migrations.
func Open(ctx context.Context, path string) (*Store, error) {
	dsn := fmt.Sprintf("file:%s?_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)", path)
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	db.SetMaxOpenConns(1) // simplest; WAL allows concurrent reads via SetMaxOpenConns>1 but we keep it simple
	db.SetConnMaxLifetime(0)

	if err := db.PingContext(ctx); err != nil {
		return nil, fmt.Errorf("ping sqlite: %w", err)
	}

	s := &Store{DB: db}
	if err := s.migrate(ctx); err != nil {
		return nil, fmt.Errorf("migrate: %w", err)
	}
	return s, nil
}

// Close closes the database.
func (s *Store) Close() error {
	return s.DB.Close()
}

// DBSize returns the on-disk size of the SQLite database in bytes,
// computed as page_count * page_size. This includes the WAL only after a
// checkpoint, but is accurate enough for dashboard display.
func (s *Store) DBSize(ctx context.Context) (int64, error) {
	var pages, pageSize int64
	if err := s.DB.QueryRowContext(ctx, `PRAGMA page_count`).Scan(&pages); err != nil {
		return 0, err
	}
	if err := s.DB.QueryRowContext(ctx, `PRAGMA page_size`).Scan(&pageSize); err != nil {
		return 0, err
	}
	return pages * pageSize, nil
}

func (s *Store) migrate(ctx context.Context) error {
	// Bootstrap migration table.
	if _, err := s.DB.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)`); err != nil {
		return err
	}

	applied := map[int]bool{}
	rows, err := s.DB.QueryContext(ctx, `SELECT version FROM schema_migrations`)
	if err != nil {
		return err
	}
	for rows.Next() {
		var v int
		if err := rows.Scan(&v); err != nil {
			rows.Close()
			return err
		}
		applied[v] = true
	}
	rows.Close()

	entries, err := fs.ReadDir(migrationFS, "migrations")
	if err != nil {
		return err
	}

	type migration struct {
		version int
		name    string
		body    string
	}
	migs := make([]migration, 0, len(entries))
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".sql") {
			continue
		}
		// 0001_initial.sql -> version=1
		under := strings.IndexByte(e.Name(), '_')
		if under < 1 {
			return fmt.Errorf("bad migration filename: %s", e.Name())
		}
		v, err := strconv.Atoi(e.Name()[:under])
		if err != nil {
			return fmt.Errorf("bad migration version in %s: %w", e.Name(), err)
		}
		body, err := fs.ReadFile(migrationFS, "migrations/"+e.Name())
		if err != nil {
			return err
		}
		migs = append(migs, migration{version: v, name: e.Name(), body: string(body)})
	}
	sort.Slice(migs, func(i, j int) bool { return migs[i].version < migs[j].version })

	for _, m := range migs {
		if applied[m.version] {
			continue
		}
		slog.Info("applying migration", "version", m.version, "name", m.name)
		tx, err := s.DB.BeginTx(ctx, nil)
		if err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, m.body); err != nil {
			tx.Rollback()
			return fmt.Errorf("apply %s: %w", m.name, err)
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO schema_migrations(version, applied_at) VALUES(?, ?)`, m.version, time.Now().UTC().Format(time.RFC3339)); err != nil {
			tx.Rollback()
			return err
		}
		if err := tx.Commit(); err != nil {
			return err
		}
	}
	return nil
}
