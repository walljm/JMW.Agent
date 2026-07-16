# Plan: Home Assistant device fact enrichment

> **Status:** PROPOSED — exploration + spec only, no code. (Boss, 2026-07-16: "write #7 up
> but don't implement yet. we'll do that later.")
> **Companion:** `docs/plans/architecture-identity-facts.md` §10 (promotion completeness — the
> mechanism by which these HA-device facts reach the resolved device). That work is being
> re-explored before implementation; this enrichment can ship independently of it (see §7).
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

- **Only devices we already mint.** Enrichment attaches to HA devices that pass the collector's
  existing fingerprint gate (MAC in `connections`, UPnP UUID, allowed identifier domain, or the
  new IP-join in §5). We do **not** emit facts for HA-only entities that don't map to a device
  we track. (Boss: "I only care about the states that apply to devices we plan to emit facts
  for.")
- **Curated, device-class-scoped — not a telemetry firehose.** `HomeAssistantCollector`
  deliberately avoids bulk sensor telemetry today ("that's HA's job … would turn every poll into
  a firehose", `AddHealthFacts` remarks). This plan keeps that stance **except** for the specific,
  high-value device-classes the Boss called out below (battery, ink, WAN speed, doorbell/motion,
  camera stream). Those are explicit, bounded exceptions — not a general "collect every sensor".
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
to the `entity_id` domain + suffix. Skip `disabled_by`/`hidden_by` entities.

### 4a. Intrinsic / inventory — promote onto the device

| Fact | Source | Notes |
|---|---|---|
| Room / area | device `area_id` → `area_registry` name | **Already emitted** (`ServicePaths.HomeAssistantHaDeviceAreaName`). |
| Firmware / `sw_version` | device-registry `sw_version`, else `update.*` `installed_version` | device-registry `sw_version` is null for a large fraction of entries; the `update.*` entity fills it (e.g. `update.humidifier_firmware`, `update.home_assistant_connect_zbt_2_..._firmware`). |
| LAN IP | `sensor.*_wi_fi_ip_address` (`sensor.pixel_tablet_wi_fi_ip_address`) | Also the **IP-join resolution** key — see §5. |
| Associated AP (BSSID) | `sensor.*_wi_fi_bssid` (`sensor.pixel_8a_wi_fi_bssid`) | L2 / mesh association; complements the OnHub mesh data. |

### 4b. Status — per device-class, periodic snapshot

| Device-class | Facts | Verified entities |
|---|---|---|
| Battery devices | level `%` (`device_class: battery`, `unit: %`) + charging state | `sensor.pixel_tablet_battery_level`, `sensor.front_door_battery`, `sensor.shed_battery` |
| Printers | ink/toner `%` **per cartridge** | `sensor.epson_sc_p900_series_{cyan,gray,matte_black,photo_black,violet,yellow,...}_ink` (10 colors), `sensor.hp_officejet_pro_6970_{black,cyan,magenta,yellow}_ink`, `sensor.hp_laserjet_m207_m212_black_cartridge_hp_w1340a` |
| Router (OnHub) | WAN status + up/down speed | `binary_sensor.kitchen_onhub_wan_status`, `sensor.kitchen_onhub_download_speed`, `sensor.kitchen_onhub_upload_speed` |
| Cameras | stream / snapshot URL + friendly name | `camera.front_door_live_view` → `attributes.entity_picture` = `/api/camera_proxy/<entity_id>?token=<access_token>` |
| Any | uptime | `sensor.*_uptime` (`sensor.kitchen_onhub_uptime`, `sensor.hp_officejet_pro_6970_uptime`, `sensor.epson_sc_p900_series_uptime`) |
| Wireless clients | link speed, signal strength | `sensor.*_wi_fi_link_speed`, `sensor.*_wi_fi_signal_strength` (signal strength is a Link/sighting value, not device-intrinsic) |

Ink is multi-valued per device → a **list sub-dimension** (`HaDevice[].InkCartridge[color].Level`),
not a comma-joined string, matching the repo's "genuinely multi-valued signals become a list
sub-dimension" rule.

### 4c. Events — timestamped, latest value

| Signal | Source | State |
|---|---|---|
| Doorbell ring | `event.*_ding` (`device_class: doorbell`) | `state` = last-ring ISO timestamp (`event.front_door_ding` → `event_type: ring`) |
| Motion | `event.*_motion` (`device_class: motion`) | `state` = last-motion ISO timestamp (`event.{front_door,driveway,garden,shed}_motion`) |

## 5. Structural win: IP-join resolution for MAC-less HA devices

The single highest-value item. Many HA devices (Cast speakers, `mobile_app` phones/tablets,
IoT) have **no MAC in `connections`** and are skipped or resolved only weakly today. Their
`sensor.*_wi_fi_ip_address` diagnostic gives a reliable **IP**, which the server can join
against the ARP / DHCP tables (the same `GetKnownMacsForIp` path the Google Wifi obscured-MAC
reconstruction already uses) to recover the **real MAC** → a proper fingerprint. This turns a
pile of unresolved HA entities into correctly-identified, merge-eligible network devices.

This is a **server-side** step (the agent emits `HaDevice[].WifiIp`; `DiscoveryMaterializer`
does the IP→MAC join), so it slots next to the existing obscured-MAC reconstruction rather than
into the collector.

## 6. Where the facts land (routing obligations)

- New facts are `ServicePaths.HomeAssistantHaDevice*` paths on the existing `HaDevice[]`
  dimension, emitted by `HomeAssistantCollector` alongside the current mac/name/model facts.
- **Every new `FactPaths`/`ServicePaths` const needs a home** or the routing fitness test fails
  (AGENTS.md: "a new constant with no projection column and no fact view fails the build").
  Proposed split:
  - **Fact views** (single-device detail, no cross-device query) for most: ink levels, camera
    URL, doorbell/motion timestamps, WAN speeds, uptime, link speed.
  - **Promoted-to-device** (per architecture-identity-facts §10) for the intrinsic set: firmware
    → `sw_version`, LAN IP, BSSID, battery level. These want to appear on the resolved device's
    own tabs, not only under the service.
- Reaching the resolved device is the promotion-completeness mechanism (§10 of the identity-facts
  plan). Until that lands, these can surface on the **service** detail (Home Assistant) and the
  intrinsic ones can be promoted with the same direct-SQL upserts the HA promotion already uses
  (`HomeAssistantDevicePromotion`).

## 7. Sequencing and dependencies

- **Independent of the identity-facts reshape** for the collector-side extraction and the
  service-detail surfacing. Can ship on its own.
- The IP-join resolution (§5) reuses existing `DiscoveryMaterializer` machinery — independent.
- Full "appears on the device's own All Facts / tabs" for the intrinsic set is nicest once
  promotion-completeness (identity-facts §10, Phase 5) exists, but is not a hard blocker (the
  existing promotion upserts cover firmware/ip/battery in the interim).

## 8. Non-goals / open questions

- **Not** collecting general environmental telemetry (temperature, humidity, power, illuminance,
  air quality). Out of scope by the firehose guardrail.
- Event facts (`ding`/`motion`) are **last-occurrence timestamps**, not a stream — we record the
  most recent, we are not an event bus. Confirm this is the desired shape vs. a rolling count.
- Camera stream URLs contain a per-session `access_token` that rotates; store the entity path and
  resolve the token at view time rather than persisting a stale tokenised URL. (Decide at
  implementation.)
- Ink cartridge naming varies by vendor (Epson color names vs HP `..._cartridge_hp_w1340a`);
  normalize the cartridge key or keep the raw entity suffix.
