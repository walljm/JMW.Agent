# Todos

## Done this session (9 commits on `main`)

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

## Outstanding / follow-ups

- [ ] **Smoke-test in a real browser.** None of the above was visually verified — the local dev
      stack was blocked on `.env`/docker access mid-session. Priority: the two new sparklines
      (Service/Device Summary tabs) and the L3/L2 diagram changes (WAN styling, wan-subnet,
      mesh nodes, layout persistence across refresh).
- [ ] **`technitium-dns`'s blank Host/IP** (from the original bug report screenshot) was never
      specifically diagnosed. Its collector already writes `Service[].DeviceId` correctly, so a
      blank row there is *probably* legitimate (non-loopback service, or agent hasn't completed a
      cycle since restart) — but worth a second look once step-ca/Home Assistant are confirmed
      fixed, to make sure it isn't a third distinct bug.
- [ ] **Verify the new `metrics_raw_path_device_time_idx` index is actually used** — added on the
      strength of the query shape, not an empirical `EXPLAIN ANALYZE` against production-scale
      data. Worth checking on core.home once deployed.
- [ ] `service_down` incident type is still explicitly deferred (not something this session
      touched) — `IncidentTypeRegistry`'s own doc comment flags it as a known fast-follow: no
      single unambiguous "service health" fact path exists yet across service types.
