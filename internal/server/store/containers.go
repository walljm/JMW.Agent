package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"strings"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// Container is one container reported by a host agent.
//
// The PK is (AgentID, ID): containers are scoped to the host that runs them.
// They are not agents — no PSK, no registration — but they are first-class
// entities in the UI and store.
type Container struct {
	AgentID        string
	ID             string
	Name           string
	Image          string
	ImageID        string
	State          string
	Status         string
	Health         string
	ComposeProject string
	ComposeService string
	CreatedAt      time.Time
	StartedAt      time.Time
	FinishedAt     time.Time
	FirstSeenAt    time.Time
	LastSeenAt     time.Time
	RecordJSON     string // full proto.DockerContainer

	// Joined from the agents table when listing across hosts.
	HostHostname string
}

// DockerEngine is the per-host engine record.
type DockerEngine struct {
	AgentID       string
	Reachable     bool
	Version       string
	APIVersion    string
	EngineID      string
	OSType        string
	Architecture  string
	StorageDriver string
	CgroupDriver  string
	SwarmState    string
	RecordJSON    string
	UpdatedAt     time.Time
}

// SyncContainers replaces the set of containers known for one agent with
// the snapshot supplied. Containers that disappear from successive reports
// are deleted (lifecycle follows the host).
//
// Returns (added, updated, removed) counts for event logging.
func (s *Store) SyncContainers(ctx context.Context, agentID string, containers []proto.DockerContainer, observed time.Time) (added, updated, removed int, err error) {
	if observed.IsZero() {
		observed = time.Now().UTC()
	}
	ts := observed.UTC().Format(time.RFC3339)

	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return 0, 0, 0, err
	}
	defer tx.Rollback()

	// Read existing IDs so we can compute added/updated/removed.
	existing := map[string]bool{}
	rows, err := tx.QueryContext(ctx, `SELECT container_id FROM containers WHERE agent_id = ?`, agentID)
	if err != nil {
		return 0, 0, 0, err
	}
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			rows.Close()
			return 0, 0, 0, err
		}
		existing[id] = true
	}
	rows.Close()

	seen := make(map[string]bool, len(containers))
	for _, c := range containers {
		blob, mErr := json.Marshal(c)
		if mErr != nil {
			return 0, 0, 0, mErr
		}
		seen[c.ID] = true
		_, execErr := tx.ExecContext(ctx,
			`INSERT INTO containers(
				agent_id, container_id, name, image, image_id, state, status, health,
				compose_project, compose_service,
				created_at, started_at, finished_at,
				first_seen_at, last_seen_at, record_json)
			 VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
			 ON CONFLICT(agent_id, container_id) DO UPDATE SET
				name = excluded.name,
				image = excluded.image,
				image_id = excluded.image_id,
				state = excluded.state,
				status = excluded.status,
				health = excluded.health,
				compose_project = excluded.compose_project,
				compose_service = excluded.compose_service,
				created_at = excluded.created_at,
				started_at = excluded.started_at,
				finished_at = excluded.finished_at,
				last_seen_at = excluded.last_seen_at,
				record_json = excluded.record_json`,
			agentID, c.ID, c.Name, c.Image, c.ImageID, c.State, c.Status, c.Health,
			c.ComposeProject, c.ComposeService,
			fmtTime(c.CreatedAt), fmtTime(c.StartedAt), fmtTime(c.FinishedAt),
			ts, ts, string(blob),
		)
		if execErr != nil {
			return 0, 0, 0, execErr
		}
		if existing[c.ID] {
			updated++
		} else {
			added++
		}
	}

	// Remove containers that vanished from this agent's report.
	if len(existing) > 0 {
		var stale []string
		for id := range existing {
			if !seen[id] {
				stale = append(stale, id)
			}
		}
		if len(stale) > 0 {
			placeholders := strings.TrimRight(strings.Repeat("?,", len(stale)), ",")
			args := make([]any, 0, len(stale)+1)
			args = append(args, agentID)
			for _, id := range stale {
				args = append(args, id)
			}
			res, dErr := tx.ExecContext(ctx,
				`DELETE FROM containers WHERE agent_id = ? AND container_id IN (`+placeholders+`)`,
				args...)
			if dErr != nil {
				return 0, 0, 0, dErr
			}
			n, _ := res.RowsAffected()
			removed = int(n)
		}
	}

	if err := tx.Commit(); err != nil {
		return 0, 0, 0, err
	}
	return added, updated, removed, nil
}

// UpsertDockerEngine writes the engine summary for one agent.
func (s *Store) UpsertDockerEngine(ctx context.Context, agentID string, info *proto.DockerInfo, observed time.Time) error {
	if observed.IsZero() {
		observed = time.Now().UTC()
	}
	ts := observed.UTC().Format(time.RFC3339)

	if info == nil {
		_, err := s.DB.ExecContext(ctx,
			`DELETE FROM docker_engines WHERE agent_id = ?`, agentID)
		return err
	}

	e := info.Engine
	if e == nil {
		e = &proto.DockerEngine{Version: info.Version}
	}
	blob, err := json.Marshal(e)
	if err != nil {
		return err
	}
	reachable := 0
	if info.Reachable {
		reachable = 1
	}
	_, err = s.DB.ExecContext(ctx,
		`INSERT INTO docker_engines(
			agent_id, reachable, version, api_version, engine_id, os_type,
			architecture, storage_driver, cgroup_driver, swarm_state,
			record_json, updated_at)
		 VALUES(?,?,?,?,?,?,?,?,?,?,?,?)
		 ON CONFLICT(agent_id) DO UPDATE SET
			reachable = excluded.reachable,
			version = excluded.version,
			api_version = excluded.api_version,
			engine_id = excluded.engine_id,
			os_type = excluded.os_type,
			architecture = excluded.architecture,
			storage_driver = excluded.storage_driver,
			cgroup_driver = excluded.cgroup_driver,
			swarm_state = excluded.swarm_state,
			record_json = excluded.record_json,
			updated_at = excluded.updated_at`,
		agentID, reachable, e.Version, e.APIVersion, e.ID, e.OSType,
		e.Architecture, e.StorageDriver, e.CgroupDriver, e.SwarmState,
		string(blob), ts)
	return err
}

// GetDockerEngine returns the engine record for one agent, or nil.
func (s *Store) GetDockerEngine(ctx context.Context, agentID string) (*DockerEngine, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT agent_id, reachable, version, api_version, engine_id, os_type,
		        architecture, storage_driver, cgroup_driver, swarm_state,
		        record_json, updated_at
		 FROM docker_engines WHERE agent_id = ?`, agentID)
	var e DockerEngine
	var reach int
	var ts string
	if err := row.Scan(&e.AgentID, &reach, &e.Version, &e.APIVersion, &e.EngineID,
		&e.OSType, &e.Architecture, &e.StorageDriver, &e.CgroupDriver, &e.SwarmState,
		&e.RecordJSON, &ts); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	e.Reachable = reach != 0
	e.UpdatedAt, _ = time.Parse(time.RFC3339, ts)
	return &e, nil
}

// ContainerFilter narrows ListContainers. All fields are optional.
type ContainerFilter struct {
	AgentID        string
	State          string // running|exited|...
	ComposeProject string
	Search         string // substring match against name/image
	Limit          int    // default 500
}

// ListContainers returns containers matching the filter, joined with the
// agent hostname for display. Ordered: running first, then last_seen desc.
func (s *Store) ListContainers(ctx context.Context, f ContainerFilter) ([]*Container, error) {
	if f.Limit <= 0 {
		f.Limit = 500
	}
	q := `SELECT c.agent_id, c.container_id, c.name, c.image, c.image_id,
	             c.state, c.status, c.health, c.compose_project, c.compose_service,
	             c.created_at, c.started_at, c.finished_at,
	             c.first_seen_at, c.last_seen_at, c.record_json,
	             COALESCE(a.hostname,'')
	      FROM containers c
	      LEFT JOIN agents a ON a.id = c.agent_id`
	var where []string
	var args []any
	if f.AgentID != "" {
		where = append(where, "c.agent_id = ?")
		args = append(args, f.AgentID)
	}
	if f.State != "" {
		where = append(where, "c.state = ?")
		args = append(args, f.State)
	}
	if f.ComposeProject != "" {
		where = append(where, "c.compose_project = ?")
		args = append(args, f.ComposeProject)
	}
	if f.Search != "" {
		where = append(where, "(c.name LIKE ? OR c.image LIKE ?)")
		like := "%" + f.Search + "%"
		args = append(args, like, like)
	}
	if len(where) > 0 {
		q += " WHERE " + strings.Join(where, " AND ")
	}
	q += ` ORDER BY (c.state = 'running') DESC, c.last_seen_at DESC LIMIT ?`
	args = append(args, f.Limit)

	rows, err := s.DB.QueryContext(ctx, q, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []*Container
	for rows.Next() {
		c := &Container{}
		var created, started, finished, fs, ls string
		if err := rows.Scan(&c.AgentID, &c.ID, &c.Name, &c.Image, &c.ImageID,
			&c.State, &c.Status, &c.Health, &c.ComposeProject, &c.ComposeService,
			&created, &started, &finished, &fs, &ls, &c.RecordJSON,
			&c.HostHostname); err != nil {
			return nil, err
		}
		c.CreatedAt, _ = time.Parse(time.RFC3339, created)
		c.StartedAt, _ = time.Parse(time.RFC3339, started)
		c.FinishedAt, _ = time.Parse(time.RFC3339, finished)
		c.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
		c.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
		out = append(out, c)
	}
	return out, rows.Err()
}

// GetContainer returns one container.
func (s *Store) GetContainer(ctx context.Context, agentID, containerID string) (*Container, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT c.agent_id, c.container_id, c.name, c.image, c.image_id,
		        c.state, c.status, c.health, c.compose_project, c.compose_service,
		        c.created_at, c.started_at, c.finished_at,
		        c.first_seen_at, c.last_seen_at, c.record_json,
		        COALESCE(a.hostname,'')
		 FROM containers c
		 LEFT JOIN agents a ON a.id = c.agent_id
		 WHERE c.agent_id = ? AND c.container_id = ?`, agentID, containerID)
	c := &Container{}
	var created, started, finished, fs, ls string
	if err := row.Scan(&c.AgentID, &c.ID, &c.Name, &c.Image, &c.ImageID,
		&c.State, &c.Status, &c.Health, &c.ComposeProject, &c.ComposeService,
		&created, &started, &finished, &fs, &ls, &c.RecordJSON,
		&c.HostHostname); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	c.CreatedAt, _ = time.Parse(time.RFC3339, created)
	c.StartedAt, _ = time.Parse(time.RFC3339, started)
	c.FinishedAt, _ = time.Parse(time.RFC3339, finished)
	c.FirstSeenAt, _ = time.Parse(time.RFC3339, fs)
	c.LastSeenAt, _ = time.Parse(time.RFC3339, ls)
	return c, nil
}

// ContainerStats summarizes counts per state across all hosts.
type ContainerStats struct {
	Total    int
	Running  int
	Exited   int
	Paused   int
	Unhealthy int
}

// ContainersSummary returns aggregate counts useful for the dashboard.
func (s *Store) ContainersSummary(ctx context.Context) (ContainerStats, error) {
	var st ContainerStats
	row := s.DB.QueryRowContext(ctx, `
		SELECT
			COUNT(*),
			SUM(CASE WHEN state = 'running' THEN 1 ELSE 0 END),
			SUM(CASE WHEN state = 'exited'  THEN 1 ELSE 0 END),
			SUM(CASE WHEN state = 'paused'  THEN 1 ELSE 0 END),
			SUM(CASE WHEN health = 'unhealthy' THEN 1 ELSE 0 END)
		FROM containers`)
	var total, run, ex, pa, unh sql.NullInt64
	if err := row.Scan(&total, &run, &ex, &pa, &unh); err != nil {
		return st, err
	}
	st.Total = int(total.Int64)
	st.Running = int(run.Int64)
	st.Exited = int(ex.Int64)
	st.Paused = int(pa.Int64)
	st.Unhealthy = int(unh.Int64)
	return st, nil
}

// fmtTime returns the empty string for the zero value, otherwise RFC3339.
func fmtTime(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return t.UTC().Format(time.RFC3339)
}
