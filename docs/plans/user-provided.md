---
agent: sdev-requirements
date: 2026-07-15
status: approved
mode: standalone
---

# Unified Operator-Authored Device Facts — Requirements

> File location note: `docs/plans/user-provided.md` is already referenced by name in six places in
> the codebase (`ManualFactCatalog.cs`, `CustomFieldViewMerger.cs`, `FactViewDef.cs`,
> `FactViewLibrary.cs`, `DeviceFactsApi.cs`, `CustomFieldsApi.cs`, `ManualFactQueries.cs`) as the
> design doc for exactly this feature area, but the file did not exist on disk before this pass.
> This document fills that gap. It covers **requirements only** — the UX rebuild of the "Manual
> Overrides" tab and the architecture/implementation design are separate, later passes.
>
> **Revision note (2026-07-15):** this document went through two rounds of confusion during
> drafting — a block of fabricated "resolved" content appeared once, and was caught and reverted;
> a second correction reconciled decisions made directly with Boss in the main working session
> against decisions made in a separately-resumed background agent session. This version is the
> reconciled result, cross-checked against both real conversations. Every decision below is either
> (a) confirmed directly with Boss via explicit question, or (b) verified against the actual
> codebase by reading the relevant source. No item in this document rests on an unverified claim.

## 1. Summary / Problem Statement

JMW.Agent currently has two separate, overlapping mechanisms that let an operator author facts
about a device by hand instead of (or on top of) what collectors report:

1. **Manual Fact Overrides** — override the value of an *existing* `FactPaths` catalog constant,
   but only for facts scoped to the whole device (a single `Device[]` list-dimension). Facts
   scoped to a child collection (a NIC, a disk, a thermal zone, etc.) cannot be overridden today,
   because the API/UI has no way to capture which child-collection instance the override applies
   to.
2. **Custom Fields** — define an arbitrary named field once, then set its value per device. Also
   device-scoped only; no child-collection scope.

Neither mechanism lets an operator author a value scoped to a specific NIC, disk, or other child
collection item, and neither generalizes to "any fact ID, any scope." Boss wants a single
mechanism that does both jobs mechanism 1 and mechanism 2 do today, plus the two things neither
can do: override any existing catalog fact regardless of how many list dimensions it has, and
create brand-new fact IDs that aren't in the catalog at all, scoped to either the whole device or
a specific child-collection instance.

## 2. Background: Current State

- **Manual Fact Overrides** (`Api/Admin/DeviceFactsApi.cs`, `ManualFacts/ManualFactCatalog.cs`):
  restricted to `FactPaths` constants with exactly one list-dimension (`Device[]` root). Any const
  with a second list dimension (e.g. `Device[].Interface[].SpeedBps`) is explicitly excluded,
  because there's no way to capture which interface the override targets.
- **Custom Fields** (`Pages/Admin/CustomFields.cshtml`, `DeviceFactsApi.SetCustomFieldValue` /
  `ClearCustomFieldValue`): admin defines an arbitrary named, slug-identified field once; value is
  then set per device via `FactPaths.CustomFieldValue` keyed by `[deviceId, slug]`. Device-scoped
  only.
- Storage is not a limiting factor for either mechanism or for the unified replacement: fact IDs
  are plain strings throughout, and `facts_history.key_values` is JSONB — nothing schema-level
  assumes a closed catalog or a single-dimension key.
- The one real, permanent architectural constraint: `FactPathRoutingFitnessTests` requires every
  `FactPaths` const declared in code to route to a projection column or a fact view at compile
  time. A genuinely arbitrary, operator-invented fact ID can never get that treatment — it is
  permanently raw/unprojected, exactly like Custom Fields values are today (queryable only via
  `facts_history`, invisible to structured reports/views).
- Child-collection natural keys already exist per collector today (MAC for `Interface[]`, serial
  for `Disk[]`, thermal-zone name for `Temperature[]`), but ad hoc — there is no central registry
  of "what child collections exist on a given device and what their current keys are" (building
  one is now in scope — see REQ-010).
- `FactSource.ManualEntry` facts are the only facts ever hard-deleted (revert/clear), and deletes
  are scoped so they can only remove rows this feature itself wrote — collector history is never
  touched by a revert.
- RBAC on both current mechanisms is `RbacPolicies.Admin`.
- The app is in production on `core.home`. Custom Fields has live definitions and per-device
  values that must survive the transition to the unified mechanism.
- **Device identity is a separate mechanism from `Fact`/`FactPaths`, with one real intersection
  point.** Verified by reading the code directly:
  - `DeviceRegistry.ResolveWithConnectionAsync` (`src/Server.Web/Ingest/DeviceRegistry.cs`)
    resolves/merges devices from a `Fingerprint(Type, Value)` list, not from `facts_history`.
  - For **agent-managed devices**, the agent computes and sends fingerprints (MAC, machine-id,
    chassis/disk serial, SSH host key, etc.) directly in each `FactBatchElement` — this path never
    reads `facts_history` and is entirely unaffected by overrides.
  - For **agentless/discovered devices**, `DiscoveryMaterializer.cs` builds the `Fingerprint` list
    it hands to that same resolution/merge code by reading `proj_device_arp`, `proj_dhcp_leases`,
    `proj_dhcp_local_leases`, `proj_discovered`, `proj_interfaces`, `proj_hardware`, and
    `proj_systems` — projection tables that `FactPaths` catalog constants route into. Since
    overrides write through the same projection pipeline collectors use, overriding one of these
    specific columns (a MAC, a serial, a hostname used as a promotion-gap fallback key) could
    change what a future discovery/promotion pass sees and cause an incorrect device merge or a
    spurious duplicate. Confirmed example consts feeding this path (verified against
    `ProjectionLibrary.cs`): `FactPaths.InterfaceMAC`, `InterfaceObscuredMAC`, `HwSystemSerial`,
    `SystemHostname`, `ArpMac`, and the `Discovered*` family (`DiscoveredMAC`,
    `DiscoveredObscuredMAC`, `DiscoveredHostname`, `DiscoveredOnvifSerial`, `DiscoveredRokuSerial`,
    `DiscoveredSnmpSerial`, `DiscoveredHueBridgeId`, `DiscoveredOnvifHardwareId`,
    `DiscoveredSsdpUuid`, `DiscoveredWsdUuid`, `DiscoveredSshHostKey`). See NFR-8.

## 3. Goals & Success Criteria

- An admin can override the value of **any** `FactPaths` catalog constant for a device, including
  ones scoped to a child collection, by supplying whatever key value(s) the path's list dimensions
  require — except the narrow, documented set of identity-bearing consts excluded per NFR-8.
- An admin can create a **brand-new fact ID** that is not in the `FactPaths` catalog at all, scoped
  to either the whole device or a specific child-collection instance, fully free-form.
- Custom Fields (definitions + per-device values) is fully replaced — after this ships, there is
  exactly one operator-authored-fact concept in the codebase, not two.
- Existing production Custom Fields data on `core.home`, including field labels/descriptions,
  survives the transition without loss.
- Audit/history behavior (who set what, when, and the ability to revert only what this feature
  wrote) is preserved at the same level of rigor as today's Manual Overrides mechanism.
- Boss can see, fleet-wide, which devices have an operator-authored value for a given fact path —
  the same capability Custom Fields' admin table gives today.
- Device-identity/fingerprint resolution is never affected by an operator override or arbitrary
  fact (NFR-8).

## 4. Scope

### 4.1 Current Definition Slice (in scope now)

- Backend/API and data-model requirements for the unified fact-authoring mechanism (both halves:
  existing-fact override with child-collection keying, and arbitrary-new-fact creation).
- The Custom Fields replacement and its migration story, including label/description metadata.
- A fleet-wide browse view for operator-authored facts.
- A child-collection-key lookup backing a combo-box-style key entry control.
- RBAC and audit requirements for the unified mechanism.
- A narrow, documented exclusion list protecting device-identity resolution (NFR-8).
- Functional/UX-relevant requirements that the later UX rebuild of the device-detail tab must
  satisfy (**not** the UX design itself — no screens, layouts, or interaction patterns are
  specified here; see the separate UX pass artifact for that).

### 4.2 Explicitly Out of Scope (this pass)

- UI/UX design itself — separate UX pass (`docs/plans/ux-operator-facts.md`).
- Architecture/implementation design (API shapes, storage layout beyond what's already confirmed,
  exact validation code, exact exclusion-list enumeration mechanics) — separate architecture pass.
- Any change to how collectors themselves assign natural keys to child-collection items.
- Multi-language/multi-locale support — single-locale, N/A.
- New deployment model, availability SLA, or scalability targets — no change to this app's
  existing single-server, self-hosted, single-admin-operator characteristics.
- Integration with any external system — this feature is entirely internal to JMW.Agent.
- New device/browser/platform support — used through the existing admin web UI by the existing
  single admin user only.

## 5. Stakeholders & Personas

**Admin Operator ("Boss")** — the sole admin user of the JMW.Agent instance running on
`core.home`. Uses manual fact overrides and Custom Fields today to annotate devices with
information collectors can't discover on their own. Also the sole decision-maker and approver for
this feature.

## 6. Glossary

| Term | Definition |
|---|---|
| **Fact** | A single (path, key values, value) tuple recorded in `facts_history`, either collector-observed or operator-authored. |
| **Fact path / fact ID** | The dotted, bracket-keyed string identifying a fact, e.g. `Interface[eth0].SpeedBps`. |
| **`FactPaths` catalog** | The closed set of `FactPaths` constants declared in code; each one is guaranteed to route to a projection column or fact view. |
| **List dimension** | A bracketed, keyed segment in a fact path representing a repeating collection, e.g. `Device[]`, `Interface[]`. A path can have more than one (device + child collection). |
| **Child collection** | A list dimension nested under the device dimension — a NIC, disk, thermal zone, etc. |
| **Natural key** | The value a collector already uses to identify a specific child-collection instance (MAC for interfaces, serial for disks, zone name for thermal zones). |
| **Manual override** | Operator-authored value that replaces the value the system would otherwise show for an *existing* catalog fact path. |
| **Operator-authored fact** | Umbrella term for both a manual override and an arbitrary fact. |
| **Arbitrary fact** | Operator-authored fact whose path is not in the `FactPaths` catalog at all. Inherently unprojected (NFR-4). |
| **Custom Field** | The mechanism being replaced by this feature. |
| **Revert / clear** | Hard-deleting an operator-authored fact row, restoring visibility of the collector-observed value (if any) underneath it. |
| **Fingerprint** | A separate `(Type, Value)` identity signal that `DeviceRegistry` uses to resolve/merge devices. Distinct from `Fact` — see §2 and NFR-8. |
| **Identity-bearing fact** | One of the narrow, documented set of `FactPaths` consts that feed device-fingerprint resolution for agentless/discovered devices (§2, NFR-8) — excluded from override eligibility. |

## 7. Functional Requirements

**Inputs and outputs:** the operator's input to every requirement below is a fact path/ID, zero or
more list-dimension key values (device ID implicit/first; a child-collection key is additional
when the path needs one), and a value to store. The output is a stored, retrievable, editable,
revertable fact record, and (for overrides on catalog-const paths, per NFR-4) a change in what
downstream reports/views display for that fact.

### REQ-001 — Override any non-identity-bearing catalog fact, including child-collection-scoped ones
**Priority:** must-have · **Category:** functional

An admin can override the value of any `FactPaths` catalog constant for a device, regardless of
how many list dimensions the path has beyond `Device[]`, **except** the identity-bearing consts
excluded per NFR-8. For a path with a child-collection dimension, the admin supplies the key
value(s) needed to identify which instance of that collection the override applies to.

**Acceptance criteria:**
- Given any non-excluded `FactPaths` const, an admin can successfully set an override value for
  it, supplying exactly as many key values as the path has list dimensions beyond the device.
- An override attempt against an NFR-8-excluded const is rejected with a clear error naming the
  reason (identity-protected), not silently accepted or silently ignored.
- An override attempt that supplies the wrong number of key values for the target path is rejected
  with a clear error, not silently accepted or silently misapplied to the wrong dimension.
- Once set, the override value is what's shown wherever that fact is displayed, taking precedence
  over the collector-observed value (A-1).
- The override can later be reverted (REQ-005), restoring the collector-observed value if one
  exists.

### REQ-002 — Create an arbitrary new fact scoped to a device or a child-collection instance
**Priority:** must-have · **Category:** functional

An admin can create a fact under a path that is **not** in the `FactPaths` catalog, scoped to
either the whole device or a specific child-collection instance, fully free-form — not
constrained to collection types a collector already produces on that device (confirmed by Boss).

**Acceptance criteria:**
- An admin can create a fact with an arbitrary path string, scoped to `Device[]` alone or to
  `Device[].<Collection>[<key>]`, where `<Collection>` need not match any collector-known type.
- The created fact is independently retrievable, editable, and revertable by its
  (device, path, key) combination.
- Path length and segment-count limits match the existing `Fact`/`FactSegment` bounds
  (`MaxIdLength=512`, `MaxSegments=32`); attempts beyond those bounds are rejected with a clear
  error, not truncated silently.

### REQ-003 — Disambiguate "override" from "create new" at write time
**Priority:** must-have · **Category:** functional

The system must determine, at the point an admin submits a fact path, whether that path matches an
existing `FactPaths` catalog constant (→ REQ-001 override behavior applies) or does not (→ REQ-002
arbitrary-fact behavior applies), and must not let the same (device, path, key) combination end up
represented by two disconnected mechanisms.

**Acceptance criteria:**
- Submitting a path that exactly matches a catalog constant's path pattern (including the correct
  number of list-dimension slots) is always treated as an override of that constant, never as a
  parallel arbitrary fact under the same string.
- Submitting a path that does not match any catalog constant is always treated as an arbitrary
  fact.
- Submitting a path that is a near-miss of a real catalog path (e.g. a typo) surfaces a
  warn-and-confirm prompt ("did you mean to override `<catalog path>`?") rather than silently
  becoming a second, disconnected arbitrary fact or being auto-corrected without confirmation. The
  admin can proceed with either interpretation after seeing the prompt.

### REQ-004 — Retire Custom Fields as a distinct concept
**Priority:** must-have · **Category:** functional

After this feature ships, Custom Fields (field definitions UI, `SetCustomFieldValue` /
`ClearCustomFieldValue`, and the `custom_field_definitions` concept) no longer exists as a separate
mechanism. Every capability Custom Fields provided is available through the unified mechanism
(see §9 for migration).

**Acceptance criteria:**
- There is exactly one operator-authored-fact concept in the product after this ships.
- Every capability an admin had via Custom Fields (define a named field with label/description,
  set/clear its value per device) has an equivalent path through the unified mechanism.

### REQ-005 — Revert/clear is scoped to operator-authored rows only
**Priority:** must-have · **Category:** functional

Reverting or clearing an operator-authored fact (override or arbitrary) removes only that specific
(device, path, key) row and never affects collector-observed history for the same or any other
path. Reverting an existing override, or clearing a value that would overwrite one already set,
requires an explicit confirmation step (fixing today's silent-overwrite gap).

**Acceptance criteria:**
- After a revert, the collector-observed value for that same path/key (if any) becomes visible
  again wherever the fact is displayed.
- A revert never deletes rows with any `FactSource` other than the operator-authored one this
  feature writes.
- Setting a value over an existing operator-authored value, or reverting/clearing one, prompts for
  confirmation before the write/delete happens.

### REQ-006 — Fleet-wide discoverability of operator-authored facts
**Priority:** must-have (confirmed by Boss) · **Category:** usability

An admin can see, across all devices at once, which devices have an operator-authored value for a
given fact path — carrying forward the capability Custom Fields' admin table provides today, not
dropping it.

**Acceptance criteria:**
- An admin can list all (device, key) combinations that currently have an operator-authored value
  for a given fact path, from one screen, without visiting each device individually.

### REQ-010 — Child-collection key suggestions from observed data
**Priority:** must-have · **Category:** functional

The system exposes, per device, the natural keys of that device's currently-observed
child-collection instances (e.g. the MACs of its known interfaces, serials of its known disks), so
a combo-box-style control can suggest/filter them as the admin types a child-collection key.
Typing a value with no match is still accepted — it creates a new key, it does not block the
action (confirmed by Boss: "why not both" — filterable suggestions plus free-text create-new).

**Acceptance criteria:**
- Given a device with N observed instances of a collection type, an admin can retrieve that
  collection's currently-known natural keys for that device, to back a filter-as-you-type control.
- Supplying a key not present in that list is not rejected — child-collection keys are not
  constrained to only currently-observed values (consistent with REQ-002's free-form stance and
  with hardware that may be temporarily absent, e.g. a disk pulled for replacement).

### REQ-011 — Preserve label/description metadata through Custom Fields migration
**Priority:** must-have · **Category:** data

Every existing Custom Field definition's label and description (and type, if one is recorded
today) survives migration and remains associated with the corresponding fact path in the unified
mechanism, viewable wherever that fact is shown — not silently dropped (confirmed by Boss).

**Acceptance criteria:**
- After migration, for every pre-existing Custom Field, its label/description is retrievable and
  displayed alongside its (now-unified-mechanism) value, for every device that had a value.
- The architecture pass decides where this metadata is stored (e.g., a lightweight path-metadata
  table) — this requirement fixes the *behavior*, not the storage shape.

## 8. Non-Functional Requirements

### NFR-1 — Audit parity with existing Manual Overrides behavior
**Priority:** must-have · **Category:** operational

Every create, override, and revert/clear action through the unified mechanism is attributable to
the admin who performed it and timestamped, recorded via the same `facts_history` /
`FactSource.ManualEntry`-style mechanism Manual Overrides uses today, with hard-delete-on-revert
semantics scoped exactly as described in REQ-005.

**Acceptance criteria:**
- For any operator-authored fact, an admin can determine who set it and when, using the same
  mechanism that answers that question for today's Manual Overrides.
- No new, separate "audit log" concept is introduced — the fact-history record *is* the audit
  trail, matching current behavior.

### NFR-2 — RBAC
**Priority:** must-have · **Category:** security

Access to every unified-mechanism operation (create, override, revert, fleet-wide browse) is
restricted to `RbacPolicies.Admin`, matching both replaced mechanisms today. No narrower role is
introduced by this feature.

**Acceptance criteria:**
- No unified-mechanism operation is reachable by a non-admin role.

### NFR-3 — No change to storage/backup/retention posture
**Priority:** must-have · **Category:** data

The unified mechanism introduces no new retention, backup, or data-ownership requirements beyond
what already applies to `facts_history`.

**Acceptance criteria:**
- Operator-authored facts are covered by whatever backup/retention policy already covers
  `facts_history` — no separate policy is defined for this feature.

### NFR-4 — Arbitrary facts are inherently unprojected (accepted limitation)
**Priority:** must-have · **Category:** functional / expectation-setting

A fact created under a path that is not a `FactPaths` catalog constant can never appear in a
structured report, dashboard column, or fact view, because `FactPathRoutingFitnessTests` only
gives that treatment to catalog constants declared in code. This applies to every arbitrary fact
created via REQ-002, exactly as it applies to Custom Fields values today.

**Acceptance criteria:**
- This limitation is documented as accepted, expected behavior — not treated as a bug during QA.
- An arbitrary fact remains visible and editable through whatever UI/API the unified mechanism
  provides for viewing operator-authored facts.
- If a future collector or projection is written for a path previously used as an arbitrary fact,
  that transition is out of scope for this feature (no automatic "promotion" is required).

### NFR-5 — Performance
**Priority:** N/A · **Category:** performance

Explicitly out of scope. Single-admin-operator, single-server application; no concurrent-user
load, high-frequency write path, or latency-sensitive read path beyond what `facts_history`
already handles for collector-observed facts.

### NFR-6 — Error handling & resilience boundary
**Priority:** must-have · **Category:** resilience

At the requirements level, error handling means: reject invalid input clearly (wrong key count,
oversized path, malformed key, NFR-8-excluded path) rather than silently accepting or misapplying
it. Deeper resilience concerns (transactional consistency, partial-write behavior) are
architecture/implementation concerns, not specified here.

**Acceptance criteria:**
- Every documented rejection case in REQ-001–REQ-003 produces a clear, admin-visible error rather
  than a silent no-op or partial write.

### NFR-7 — No new third-party dependencies expected
**Priority:** should-have · **Category:** constraint

This feature reuses existing `Fact`/`FactSegment` infrastructure and `facts_history` storage; no
new third-party library is expected to be needed. If the architecture pass identifies a need for
one, it must be flagged and justified explicitly per the project's minimal-dependency stance.

### NFR-8 — Identity-bearing facts are excluded from override eligibility
**Priority:** must-have · **Category:** functional / safety

Confirmed by Boss (2026-07-15): rather than trying to make identity resolution ignore overrides at
read time (architecturally invasive — the projection layer doesn't track value provenance today),
the unified mechanism excludes a narrow, explicitly documented list of identity-bearing
`FactPaths` consts from override eligibility entirely. This is a **short, enumerable exception**
to REQ-001's "any catalog constant" — not a return to today's broad, unexplained restriction.

The exclusion list covers `FactPaths` consts that `DiscoveryMaterializer.cs` reads when building
fingerprints for agentless/discovered-device resolution (§2). Confirmed examples:
`FactPaths.InterfaceMAC`, `InterfaceObscuredMAC`, `HwSystemSerial`, `SystemHostname`, `ArpMac`, and
the `Discovered*` family (`DiscoveredMAC`, `DiscoveredObscuredMAC`, `DiscoveredHostname`,
`DiscoveredOnvifSerial`, `DiscoveredRokuSerial`, `DiscoveredSnmpSerial`, `DiscoveredHueBridgeId`,
`DiscoveredOnvifHardwareId`, `DiscoveredSsdpUuid`, `DiscoveredWsdUuid`, `DiscoveredSshHostKey`).
The architecture pass must produce the exhaustive, final list by reading
`DiscoveryMaterializer.cs`'s actual field reads end to end — the list above is a confirmed
starting point, not necessarily complete.

**Acceptance criteria:**
- Every `FactPaths` const on the final exclusion list is rejected at write time for both override
  and (where applicable) any attempt to shadow it via an arbitrary-fact path, with a clear,
  specific error naming why (see REQ-001).
- Every `FactPaths` const NOT on the list remains fully overridable per REQ-001.
- The exclusion list is a single, discoverable, documented artifact in code (not scattered
  ad hoc checks) so it can be audited and extended if `DiscoveryMaterializer`'s read set changes.
- Verified by a test that attempts to override each excluded const and confirms rejection, plus a
  test that overrides a non-excluded const known to be adjacent (e.g. `InterfaceSpeedBps` next to
  `InterfaceMAC`) and confirms it succeeds normally.

## 9. Custom Fields Replacement & Migration

Custom Fields values are already, structurally, arbitrary device-scoped facts
(`FactPaths.CustomFieldValue` keyed by `[deviceId, slug]`) — the new mechanism is a strict
generalization of exactly that pattern, making value migration low-risk. Label/description
metadata (REQ-011) is the one piece that needs a new home, since arbitrary facts are otherwise
self-describing only by path string.

- **REQ-007** (must-have, data): Every existing Custom Field value on `core.home`, for every
  device, is present in the unified mechanism after migration, with no value loss.
- **REQ-008** (must-have, data): The migration is a one-time, verifiable step — Boss can confirm
  before and after that every (device, slug) value pair that existed under Custom Fields exists
  under the new mechanism with the same value, and every field's label/description (REQ-011)
  carried over.
- **REQ-009** (must-have, operational): The migration does not require `core.home` to lose
  availability beyond what's already normal for this project's deploys — no additional
  migration-specific outage is required.

## 10. Constraints & Assumptions

### Constraints
- RBAC must remain `RbacPolicies.Admin` for all unified-mechanism operations (NFR-2).
- Fact ID length/segment bounds (`MaxIdLength=512`, `MaxSegments=32`) are inherited, not
  renegotiated, by this feature.
- No new "audit log" concept — the existing fact-history mechanism is the audit trail (NFR-1).
- The NFR-8 exclusion list is not negotiable within this feature's scope. It is **not** the only
  carve-out from "override any catalog fact" — the architecture pass identified two further
  carve-outs, both ratified by Boss (2026-07-16):
  - **Amendment A:** `FactPaths.Derived`/`Metric` consts are non-authorable entirely (they are
    recomputed by analysis; an override would be silently clobbered on the next pass). This matches
    today's actual behavior — today's `ManualFactCatalog` already can't reach them — but is now made
    an explicit, deliberate rule rather than an accident of reflection scope.
  - **Amendment B:** the `Device[].Lease[]` dimension (backing `proj_dhcp_local_leases`) is
    non-authorable in its entirety, not just at specific consts, because its dimension *key* is a
    MAC address consumed as a device fingerprint by `DiscoveryMaterializer` — an operator-authored
    key there could inject a false fingerprint and cause a spurious device merge. See NFR-8 and the
    architecture doc (`docs/plans/architecture-operator-facts.md` §14) for the full derivation.

### Assumptions confirmed
- **A-1 (confirmed):** Operator-authored values always take precedence over collector-observed
  values for the same fact ID, matching today's Manual Overrides behavior — except NFR-8-excluded
  consts, which can't be overridden at all.
- **A-3 (confirmed):** No formal test-coverage threshold beyond the project's existing
  unit/integration test practice (`test/Unit`, `test/Integration`).

## 11. Risks & Tensions

- **Flexibility vs. reportability (NFR-4):** the more operators lean on arbitrary facts instead of
  requesting real catalog/collector support for a data point, the more operator-authored data
  accumulates that will never show up in reports or dashboards. Accepted trade-off.
- **NFR-8 list completeness is delivery risk, not requirements risk:** the confirmed example
  consts are a verified starting point, not a verified-complete list. If the architecture pass
  misses a `DiscoveryMaterializer` read path, an override could still leak into fingerprint
  resolution. QA for this feature must include a concrete test against `DiscoveryMaterializer`'s
  actual code, not just the list as currently enumerated here (see NFR-8 acceptance criteria).
- **Migration metadata loss, if REQ-011 is under-delivered:** Custom Fields' label/description
  metadata is not recoverable from `facts_history` alone once dropped — REQ-011 must be verified
  before the Custom Fields code is removed, not after.

## 12. Decisions

- **DEC-1:** Both halves of the feature are in scope together: (a) removing the single-`Device[]`-
  dimension restriction on overrides, and (b) supporting brand-new arbitrary fact IDs.
- **DEC-2:** The unified mechanism **replaces** Custom Fields outright; it does not coexist as a
  second concept.
- **DEC-3:** UI/UX design is deferred to a separate UX pass; this document captures
  functional/UX-relevant requirements only.
- **DEC-4:** Identity-bearing facts are excluded from override eligibility via a narrow, documented
  list (NFR-8), not via a read-side fix — confirmed directly by Boss after the underlying
  mechanism (`DiscoveryMaterializer` reading `FactPaths`-fed projection columns for agentless
  device resolution) was verified in code.
- **DEC-5:** Fleet-wide discoverability (REQ-006) is must-have, not provisional — Boss confirmed he
  wants to keep the capability Custom Fields gives today.
- **DEC-6:** Child-collection key entry is a combo box: filterable suggestions from observed data,
  plus free-text entry that creates a new key when there's no match (REQ-010).
- **DEC-7:** Custom Fields' label/description metadata must survive migration (REQ-011).
- **DEC-8:** Arbitrary new facts are fully free-form, not constrained to collector-known collection
  types (REQ-002).

## 13. Quality Standards / Verification Expectations

- No new coverage threshold beyond existing project practice — unit tests for fact-path
  disambiguation (REQ-003), key-count validation (REQ-001, REQ-002), and NFR-8's exclusion-list
  enforcement; integration tests (against live Postgres, per `test/Integration` convention) for
  the Custom Fields migration (§9) and revert/clear scoping (REQ-005).
- Migration verification (REQ-008) must be demonstrable — a before/after count or diff of Custom
  Field values (and labels/descriptions, REQ-011) vs. migrated unified-mechanism facts.
- NFR-8 needs an explicit test against `DiscoveryMaterializer`'s real read set, not just the
  example list in this document (see §11).

## 14. Open Questions

None. All items raised during this pass have been resolved — see §12 for the decisions and the
requirements/NFRs they produced (REQ-006 upgraded to must-have; REQ-010, REQ-011 added; NFR-8
added). Ready for handoff to the UX and architecture passes.
