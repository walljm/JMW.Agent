# Todos

## Done this session (10 commits on `main`)

- Device/Service detail UX pass: shared `section-nav.js`, ARIA + keyboard nav on the tab rail,
  mobile nav keeps grouping, sticky-first-column tables, CA status coloring + expiry badges,
  Service Summary tab.
- `cert_expiring` incident evaluator (Service History has real content now; Dashboard posture
  panel migrated off the old standalone query).
- L3 topology: stable node ids, WAN interface labeling, WAN-subnet detection, router
  hostname+IP, louder Internet node, drag-position persistence.
- Bug fix: Services list blank Host/IP — `StepCaCollector` was leaking a raw placeholder
  string into `Service[].DeviceId`; `HomeAssistantCollector` was writing the wrong fact path.
- Terrain dashboard: linked to the four previously-orphaned `/terrain/*` pages, added previews.
- OnHub/Google Wifi mesh points represented on the L2 graph (new `mesh_ap_bssid` projection
  column + migration).
- DNS blocked-% trend sparkline (Service Summary) and interface throughput sparkline (Device
  Summary, new `metrics_raw` index).
- `service_down` incident sweep — the last of the four incident types `IncidentTypeRegistry`
  had flagged as deferred (CA services: explicit `ca_status`; everything else: staleness of
  `proj_services.updated_at`).
- Researched (subagent): whether `[DatabaseCommand]` can support keyset pagination with dynamic
  sort columns while keeping automatic schema validation. See "Decisions needed" below.
- Investigated core.home's live `proj_services`/`services`/`service_fingerprints` data directly
  to ground the Services-list bug fix. Found two issues beyond what got fixed — see below.

## In progress elsewhere

- [ ] **`[DatabaseCommand]` keyset-pagination support** — handed off to a separate agent/session
      (prompt already given) rather than built here. Confirmed root cause: the generator's SQL is
      a single static string, and identifiers (ORDER BY column, cursor shape) genuinely can't bind
      as Npgsql parameters. Two designs on the table: Option B (attribute-declared `Sortable`
      column allowlist, generator appends ORDER BY/keyset itself, one schema-validated query
      variant per column — medium-to-large generator change) vs Option C (no generator change — a
      lightweight test helper reusing `DatabaseCommandValidatorExtensions.ValidateAsync` per
      hand-rolled query's sort-column dict — cheaper, opt-in discipline instead of automatic).
      Affects 5 hand-rolled list APIs: DeviceListApi, PortsApi, ContainersApi, StorageApi, ArpApi.

## Resolved this session (core.home data cleanup)

- [x] **Duplicate Home Assistant service records** — `b4cad315...` (stale `https://ha.home`
      fingerprint, predating a target-config port correction) and `4d3bb4ec...` (orphaned
      `home-assistant-devices` row predating the HA-collector merge) both deleted directly from
      core.home (service_fingerprints/proj_services/proj_service_ca*/proj_dns_*/proj_dhcp_*/
      facts_history/metrics_raw/incidents/change_events/services — one transaction, verified
      row counts matched the pre-check exactly: 2/2/0/0/0/0/0/0/0/3/0/0/0/2, `COMMIT` clean).
      No code fix needed — confirmed via the live `targets` table that only one Home Assistant
      target (`https://ha.home:8123`) is currently configured, so this was a one-time config-
      history artifact, not an ongoing duplication bug. `061515d4...` is now the sole HA service
      record and will get its `DeviceId` populated once the agent fix redeploys.

## Outstanding / follow-ups

- [ ] **Smoke-test in a real browser.** None of this session's UI work was visually verified —
      the local dev stack was blocked on `.env`/docker access mid-session. Priority: the two new
      sparklines (Service/Device Summary tabs) and the L3/L2 diagram changes (WAN styling,
      wan-subnet, mesh nodes, layout persistence across refresh).
- [ ] **Verify the new `metrics_raw_path_device_time_idx` index is actually used** — added on the
      strength of the query shape, not an empirical `EXPLAIN ANALYZE` against production-scale
      data. Worth checking on core.home once deployed.
- [ ] The step-ca/Home Assistant `DeviceId` fixes need an **agent rebuild + redeploy** before
      core.home's data actually corrects itself (confirmed via direct query: still showing the
      pre-fix state as of this session).
