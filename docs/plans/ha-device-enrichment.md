# Plan: Home Assistant device fact enrichment

> **Status:** IMPLEMENTED (2026-07-17). All of §4a/4b/4c, §5 (IP-join), and §6 (promotion)
> shipped in one pass — see §9 for what changed from the original proposal during
> implementation (a few real discoveries against `docs/scratch/ha-raw-dump.json`, and a
> larger-than-planned foundational fix: `GetKnownMacsForIp`'s IP→MAC lookup had no LAN
> scoping at all, so §5 required adding agent-scoping to the ingest pipeline itself — see §9.2).
> **Companion:** `docs/plans/architecture-identity-facts.md` §10 (promotion completeness). Not
> a blocker in the end: firmware got its own new `proj_hardware` column, WifiIp reused the
> existing `last_seen_ip` upsert, and battery percent turned out to have no projection column
> at all (`proj_batteries` was dropped years ago) so it routes through the real ingest pipeline
> directly — see §6.
>
> Every entity/attribute named below was verified against `docs/scratch/ha-raw-dump.json`
> (a live `config/device_registry/list` + `config/entity_registry/list` +
> `config/area_registry/list` + `get_states` capture).

---

## 1. Goal

The Home Assistant collector already mints a JMW device for each fingerprintable HA
device-registry entry and promotes a thin slice of identity (manufacturer, model, name, area).
HA also exposes — through the **entity registry** and **entity states** we already fetch — a
large amount of genuinely useful per-device data that we throw away today: firmware versions,
LAN IP / associated AP, battery levels, printer ink, camera streams, doorbell/motion events,
and WAN status/speeds. This plan enumerates the useful subset and how to emit it.

## 2. Scope and guardrails

- **Only devices we already mint** — now including devices the IP-join (§5) can resolve.
  Enrichment attaches to HA devices that pass the collector's fingerprint gate: MAC in
  `connections`, UPnP UUID, allowed identifier domain, **or a resolvable Wi-Fi IP** (the gate
  admits this specifically so the server-side IP-join has something to work with — see §5 and
  §9.1). We still do **not** emit facts for HA-only entities with no admission reason at all.
  (Boss: "I only care about the states that apply to devices we plan to emit facts for.")
- **Curated, device-class-scoped — not a telemetry firehose.** `HomeAssistantCollector`
  deliberately avoids bulk sensor telemetry today ("that's HA's job … would turn every poll into
  a firehose", `AddHealthFacts` remarks). This plan keeps that stance **except** for the specific,
  high-value device-classes the Boss called out below (battery, ink, WAN speed, doorbell/motion,
  camera stream). Those are explicit, bounded exceptions — not a general "collect every sensor".
  Verified: a plain `temperature`/`humidity` sensor is still not collected (test coverage in
  `HomeAssistantCollectorTests.Collect_BulkSensorTelemetry_StillNotCollected`).
- **No new fetches.** The collector already pulls `device_registry`, `entity_registry`,
  `area_registry`, and `get_states`, and already builds `entitiesByDevice` + `statesByEntity`.
  This is pure extraction from data in hand.

## 3. The join

```
device_registry[i].id ─┐
                       ├─ entity_registry[j].device_id  (entity → device)
get_states[k].entity_id ┘  matched to entity_registry[j].entity_id  (entity → state+attributes)
```

`entity_registry` also carries `entity_category` (`diagnostic` / `config` / null),
`original_name`, `translation_key`, `unique_id`, and `disabled_by` / `hidden_by` — useful for
filtering to the identity/inventory entities and skipping disabled ones.

## 4. Fact catalog (verified against the dump)

**Matching strategy:** prefer `attributes.device_class` (stable across integrations); fall back
to the `entity_id` domain + suffix. Skip `disabled_by`/`hidden_by` entities (implicit: a disabled
entity has no `get_states` row, so the `statesByEntity` lookup simply misses).

### 4a. Intrinsic / inventory — promote onto the device

| Fact | Source | Notes |
|---|---|---|
| Room / area | device `area_id` → `area_registry` name | Already emitted (`ServicePaths.HomeAssistantHaDeviceAreaName`). |
| Firmware / `sw_version` | device-registry `sw_version`, else `update.*_firmware` `installed_version` | Fallback restricted to `update.*` entities whose entity_id contains "firmware" (verified: `update.humidifier_firmware`, `update.home_assistant_connect_zbt_2_..._firmware`) — every other `update.*` entity in the dump belongs to an `entry_type=="service"` bookkeeping device already filtered out before this code runs, so the "firmware"-substring restriction never has to disambiguate multiple real candidates in practice. |
| LAN IP | `sensor.*_wi_fi_ip_address` | Also the **IP-join resolution** key — see §5. **mobile_app's four Wi-Fi diagnostic entities (Ip/Bssid/LinkSpeed/SignalStrength) are disabled by default** (`entity_registry.disabled_by = "integration"`) — verified present in the registry but with no state in the captured dump. They only populate once the homeowner enables them in HA. |
| Associated AP (BSSID) | `sensor.*_wi_fi_bssid` | Same disabled-by-default caveat as LAN IP. |

### 4b. Status — per device-class, periodic snapshot

| Device-class | Facts | Verified entities / matching signal |
|---|---|---|
| Battery devices | level `%` | `device_class: battery` — already emitted before this plan. |
| Printers | ink/toner `%` **per cartridge** | Matched via `attributes.marker_type == "ink-cartridge" \| "toner"` — **not** an entity_id pattern (Epson's `sensor.*_..._ink` and HP's `sensor.*_..._cartridge_hp_w1340a` share no common suffix, but both carry `marker_type`). |
| Router (OnHub) | WAN status + up/down speed | `binary_sensor.*_wan_status` (device_class `connectivity` — same class as generic device Online, so entity_id suffix is checked FIRST or every WAN status misreads as device reachability); `sensor.*_download_speed`/`_upload_speed`, unit **`KiB/s`** (verified — NOT `Mbit/s` despite the generic "speed" name), converted ×8192 to bits/sec to match `FactPaths.InterfaceSpeedBps`'s convention. A redundant `sensor.*_wan_status` text entity (same value, "Connected"/etc.) exists alongside the binary_sensor and is deliberately not read. |
| Cameras | stream/snapshot URL | `camera.front_door_live_view` → `attributes.entity_picture` = `/api/camera_proxy/<entity_id>?token=<access_token>`, resolved to an absolute URL (HA base URL + path) and stored as-is including the token (see §8). |
| Any | uptime | `sensor.*_uptime` — verified unit `s`, `device_class: duration`. |
| Wireless clients | link speed, signal strength | `sensor.*_wi_fi_link_speed` (unit `Mbit/s` per the `mobile_app` integration's own source — not independently re-verified, since these are disabled by default in the dump), `sensor.*_wi_fi_signal_strength` (unit `dBm`, same caveat). |

Ink is multi-valued per device → a **list sub-dimension**
(`Service[].HomeAssistant.HaDevice[].InkCartridge[].Level`), not a comma-joined string, matching
the repo's "genuinely multi-valued signals become a list sub-dimension" rule. The list key is the
entity_id's own suffix after the domain dot (e.g. `epson_sc_p900_series_cyan_ink`,
`hp_laserjet_black_cartridge_hp_w1340a`) — see §8 for why it isn't trimmed further.

### 4c. Events — timestamped, latest value + rolling count

| Signal | Source | State |
|---|---|---|
| Doorbell ring | `event.*_ding` (`device_class: doorbell`) | `state` = last-ring ISO timestamp — verified format `2026-07-12T21:01:57.880+00:00`. |
| Motion | `event.*_motion` (`device_class: motion`) | `state` = last-motion ISO timestamp, same format. |

Both also emit a rolling count (`RingCount`/`MotionCount` — see §8): the agent's own
`HomeAssistantCollector` instance tracks the last-seen timestamp per (HA device, ring\|motion)
in memory and increments the count only when a poll's timestamp advances past what it last
recorded.

## 5. Structural win: IP-join resolution for MAC-less HA devices

The single highest-value item, and the one that grew the most during implementation (see §9).
Many HA devices (Cast speakers, `mobile_app` phones/tablets, IoT) have **no MAC in
`connections`** and were previously skipped outright by the collector's fingerprint gate before
this plan. Their `sensor.*_wi_fi_ip_address` diagnostic (§4a) gives a reliable **IP**. The
collector's gate now admits a device on a resolvable Wi-Fi IP alone (§2), and emits
`HaDevice[].WifiIp` for it; server-side, `HomeAssistantDevicePromotion.TryRecoverMacByIpAsync`
joins that IP against this agent's own LAN data (ARP cache, both DHCP-lease projections, prior
discovery) to recover a real **MAC** → a proper fingerprint, usually merging the HA entity onto
a device some *other* collector already tracks (the same phone/speaker seen via ARP or mDNS)
rather than minting a new one.

**Safety: agent-scoped, not global.** Boss flagged, correctly, that a naive IP-only join is
unsafe — RFC1918 addresses are commonly reused across independent LANs/sites this server
ingests from, and the existing `GetKnownMacsForIp` (used by the Google Wifi obscured-MAC
reconstruction) turned out to have **no LAN scoping at all** before this plan — a systemic gap,
not something specific to HA. Fixing it required adding agent-scoping to the ingest pipeline
itself (see §9.2): every candidate MAC is now scoped to the querying agent's own reported LAN
(`agent_id`, threaded through `Fact` → `RoutedFact` → the projection write path), with legacy
unscoped rows (`agent_id IS NULL`) treated as still-matchable rather than excluded, so existing
installations don't lose recall. A second, independent signal — the candidate MAC's IEEE OUI
vendor cross-checked against the HA device's own self-reported manufacturer
(`GetKnownMacsWithVendorForIp.sql`) — disambiguates when more than one candidate exists at the
same IP; with no manufacturer to check, recovery requires the IP to resolve to exactly one
candidate at all (the same strictness the Google Wifi obscured-MAC path already applies via
`ObscuredMac.Pick`). An ambiguous result recovers nothing — it never guesses.

This is a **server-side** step (`HomeAssistantDevicePromotion`, inline during ingest — not
`DiscoveryMaterializer`, since HA device facts already resolve inline off the ingest batch's own
in-memory list; see `docs/plans/ha-inline-discovery.md`).

## 6. Where the facts land (routing obligations)

- New facts are `ServicePaths.HomeAssistantHaDevice*` paths on the existing `HaDevice[]`
  dimension (plus one nested `InkCartridge[]` sub-dimension), emitted by `HomeAssistantCollector`
  alongside the current mac/name/model facts.
- Every new `FactPaths`/`ServicePaths` const is routed via `FactViewLibrary` — extended the
  existing "Home Assistant Devices" list view (sharing its `Service|HaDevice` DimKey) with every
  new device-scoped fact, and added a separate "Home Assistant Device Ink Cartridges" view for
  the nested `Service|HaDevice|InkCartridge` DimKey (mirroring the "Discovered: NBNS Names"
  precedent — a List view's columns must share one DimKey).
- **Promoted onto the resolved device**, each via a different mechanism depending on what
  already existed:
  - **Firmware/sw_version** → a **new** `proj_hardware.firmware_version` column
    (migration `0090`), fill-only upsert (`UpsertDeviceFirmwareAsync`) — same COALESCE pattern
    as vendor/model.
  - **LAN IP** → the **existing** `UpsertDeviceSystemAsync`'s `lastSeenIp` parameter, previously
    always passed `null` for HA — now wired to `entry.WifiIp`.
  - **Battery percent** → **no projection column exists at all** for it (`proj_batteries` was
    dropped in migration `0031` in favor of a device-detail "Battery" fact view read straight
    from `facts_history` — there's nothing to promote *into*). Since that surface has no
    last-write-wins projection column to accidentally clobber (the precedence risk
    `architecture-identity-facts.md` §10.7 found for *vendor*), this one fact routes through the
    **real ingest pipeline** (`FactIngestPipeline.IngestAsync`, threaded into
    `HomeAssistantDevicePromotion.PromoteAsync`) rather than a hand-rolled SQL upsert — correct
    for this specific fact, not a general precedent for promoting everything that way.

## 7. Sequencing and dependencies

- Shipped as one pass, not staged — the collector-side extraction, the fact-view routing, the
  promotion additions, and the IP-join all landed together (see §9 for why §5 needed a
  foundational scoping fix first).
- Independent of the identity-facts reshape in the end: firmware/IP/battery promotion needed no
  changes to `architecture-identity-facts.md` §10's promotion-completeness mechanism at all.

## 8. Resolved decisions (were open questions)

- **Not** collecting general environmental telemetry (temperature, humidity, power, illuminance,
  air quality). Out of scope by the firehose guardrail — verified still true for the new code.
- **Event facts are timestamp + rolling count** (Boss's call, overriding the original
  "timestamp only" default). The count is maintained **by the agent, in memory**
  (`HomeAssistantCollector._eventTracking`), because `event.*` entities carry no count attribute
  of their own — HA only reports the last-occurrence timestamp. **Known limitation:** the count
  is scoped to this agent process's lifetime, not a durable historical total — it resets to 0 on
  every agent restart/redeploy (which happens on every `vX.Y.Z` tag per the agent's
  self-update deploy flow). It answers "how many distinct rings/motions has this agent process
  observed," not "how many rings has this doorbell ever recorded."
- **Camera URLs are stored as HA returns them**, including the rotating `access_token` (Boss's
  call, overriding the "resolve token at view time" default) — resolved to an absolute URL
  (HA base URL + the relative `entity_picture` path) since a bare relative path is unusable on
  its own regardless of the token-staleness question. A stored URL's token is only ever as
  stale as the last poll interval.
- **Ink cartridge keys are the raw entity-id suffix**, unnormalized (Boss's call) — specifically
  everything after the domain dot (`sensor.`), not a further-trimmed color/part name, since HA
  doesn't expose the device-name prefix as a separately strippable segment and guessing where it
  ends risks silently mismatching across vendors.

## 9. What changed from the original proposal during implementation

### 9.1 The collector's admission gate needed to loosen for §5 to have anything to join

The original §5 wording assumed the collector would emit `WifiIp` for the MAC-less devices the
join targets. It didn't: those devices (`mobile_app`, `google_home`, etc.) were dropped by the
gate *before* any fact emission ran at all. Fixed by adding "has a resolvable Wi-Fi IP" as a
third admission reason alongside MAC/UUID/allowed-identifier-domain (§2) — computed once per
device (`TryGetWifiIp`) and reused by both the gate check and the fact-emission switch.

### 9.2 `GetKnownMacsForIp` had no LAN scoping — a pre-existing gap, not new to this plan

Investigating §5's safety requirement turned up that neither `GetKnownMacsForIp` nor its
existing caller (the Google Wifi obscured-MAC-by-IP reconstruction) had ever been scoped to a
single LAN — a global lookup across every agent this server has ever ingested from. Fixing this
properly (Boss's call, over a narrower "just scope the new HA path" option) meant adding agent
identity to the generic projection write path:

- `Fact` gained an `AgentId` field (mirroring `Source`), stamped by `FactsEndpoint` from the
  authenticated agent's id when rewriting the placeholder root.
- `RoutedFact` and `ProjectionDef` gained a matching `AgentId`/`TracksAgentId` (opt-in per
  projection, mirroring how `updated_at` already rides along every row without being a `Columns`
  entry).
- Five projections opted in: `proj_device_arp`, `proj_dhcp_leases`, `proj_dhcp_local_leases`,
  `proj_discovered`, `proj_interfaces` (migration `0089`).
- `GetKnownMacsForIpAsync` gained an `agentId` parameter; both existing obscured-MAC call sites
  in `DiscoveryMaterializer` now pass the row's own recorded `agent_id` (read straight off
  `proj_discovered`/`proj_interfaces`, no extra lookup needed) instead of nothing.
- Legacy rows written before this shipped (`agent_id IS NULL`) are treated as unscoped/matchable
  rather than excluded, so existing installations don't regress in recall.

This is real new infrastructure, not a hack scoped to HA — any future IP/MAC-join consumer can
opt a projection in with one line.

### 9.3 Verification against the raw dump caught several spec inaccuracies before they shipped as bugs

- WAN status binary_sensor shares `device_class: connectivity` with the generic device-Online
  signal — would have silently overwritten "Online" with WAN reachability without an
  entity_id-suffix check taking priority (§4b).
- WAN speed sensors report `KiB/s`, not `Mbit/s` — the plan's original table didn't specify a
  unit and the generic "speed" framing invited the wrong assumption.
- Ink/toner sensors have no reliable entity_id pattern across vendors; `attributes.marker_type`
  does, and generalizes to any future ink-reporting integration for free.
- The four Wi-Fi diagnostic entities (`WifiIp`/`WifiBssid`/`WifiLinkSpeedMbps`/
  `WifiSignalStrengthDbm`) are disabled by default in HA's `mobile_app` integration — present in
  the registry, absent from `get_states`, so their exact state shape is documented from the
  integration's own source rather than independently re-verified against a live value in this
  dump.
