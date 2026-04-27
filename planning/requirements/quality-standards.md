---
agent: sdev-01a-requirements-analyst
date: 2026-04-27
status: draft
---

# Quality Standards

These are project-wide quality gates. Per-requirement acceptance criteria live in their respective REQ files.

## Test Coverage

- **Line coverage:** ≥ 80% on production Go code (excluding generated code, vendored deps, and trivial DTO/struct files).
- **Branch coverage:** target ≥ 70% on packages with non-trivial control flow (alerting engine, discovery dedup, retention/downsampling, transport/auth). The 80% line target stays primary; branch is a secondary signal where the metric is meaningful.
- **Critical paths:** Full test coverage (line + relevant edge cases) on:
  - Bootstrap flow
  - Authentication and session lifecycle
  - Agent registration and approval
  - Pre-shared key validation
  - Heartbeat ingestion and offline detection
  - Threshold evaluation and alert lifecycle
  - Notification dispatch (per-channel)
  - Discovery deduplication
  - Tiered metric downsampling and retention pruning
  - SQLite online-backup snapshot/restore
- **Test types:** unit + integration. End-to-end smoke tests for bootstrap, agent register/approve, heartbeat, and one alert-to-notification round trip.

## Performance Budgets

- **Heartbeat ingestion:** server must accept and process a heartbeat in well under the heartbeat interval, even under simultaneous load from the full expected fleet (75 agents).
- **Dashboard responsiveness:** primary views (client list, detail view, summary cards) load in under 1 second on Boss's LAN with the expected 75-device dataset.
- **Chart rendering:** sparklines and expanded charts render in under 500 ms for any retention tier.
- **Agent footprint:** best-effort, no hard target. Resource use is monitored during implementation; egregious regressions are treated as bugs.

## Alert Latency

- **Trigger-to-notification latency budget:** under 5 minutes from condition onset (or condition sustained-duration completion) to notification dispatch.
- This budget includes: detection delay + threshold evaluation cycle + dedup window check + channel send latency.

## Availability

- **Server:** best-effort home-lab availability. No formal SLA. Restart-tolerance is required (state survives restart, agents reconnect cleanly), but planned downtime for upgrades is acceptable.
- **Agent reconnection:** an agent that loses connectivity for any duration must reconnect cleanly without manual intervention when connectivity returns.

## Data Retention & Integrity

- **Metrics:** 7 days raw / 30 days at 5-min aggregates / 1 year at hourly aggregates.
- **Event log:** at least 30 days; older events may be pruned but the policy is configurable.
- **Reboot history:** at least 1 year per device.
- **SMART history:** at least 1 year.
- **Backups:** scheduled local snapshots are taken using SQLite online-backup; the snapshot must be restorable.

## Browser Support

- Modern desktop browsers (Chrome, Firefox, Safari, Edge — latest two stable releases).
- Mobile-responsive UI (phones, tablets) on the same browser baseline.
- Legacy IE and pre-ES2017 browsers are explicitly unsupported.

## Accessibility

- The dashboard targets WCAG 2.1 AA for color contrast, keyboard navigation, and form labeling. Detailed accessibility audit happens in Step 5f. (No legal compliance driver — this is "do it right by default" for a tool Boss uses daily.)

## Code Quality Gates

- **Linting:** `go vet` and `staticcheck` clean on every PR.
- **Format:** `gofmt -s` clean on every PR.
- **Vulnerability scan:** `govulncheck` runs on every PR; high-severity findings block.
- **Dependency justification:** every non-stdlib dependency is documented in a DEP record (per DEC-006).

## Operational Quality

- **Logging:** structured logs (JSON) at sensible levels. No secrets in logs.
- **Configuration validation:** server refuses to start on invalid config with a clear error message.
- **Migrations:** schema migrations are applied automatically on server start; a failed migration leaves the prior schema intact and reports the failure.
