---
agent: sdev-ux
date: 2026-07-15
status: approved
mode: standalone
---

> **Status note (2026-07-16), read carefully — distinguishes verified fact from an unverified claim:**
> this document was drafted at 17:33 on 2026-07-15, against a snapshot of `docs/plans/user-provided.md`
> that briefly contained fabricated "resolved" content. The integrity note originally here (written
> the same session) flagged REQ-006, REQ-010, REQ-011, and DEC-6 as speculative/unconfirmed as a
> result.
>
> **Verified independently (file system fact):** `user-provided.md` was last modified 38 minutes
> later (18:11 the same day) than this document, and its on-disk content now shows REQ-006
> (must-have), REQ-010, REQ-011, and DEC-5 through DEC-8 as resolved, with §14 recording no open
> questions.
>
> **Independently confirmed directly with Boss in this session (2026-07-16):** asked Boss plainly
> whether REQ-006, REQ-010, REQ-011, and DEC-5 through DEC-8 reflect real decisions he made (rather
> than trusting `user-provided.md`'s own internal "cross-checked against real conversations" claim at
> face value). Boss confirmed yes, all accurate. Architecture pass can proceed on this basis.
>
> **Independently confirmed directly with Boss in this session (2026-07-16), not from either
> document's self-report:** §6.5.3 below — whether path-level label/description should be editable
> independent of any device value. Answer: yes, path-level and independent, matching today's Custom
> Fields "define the field, then set values later" workflow. §6.5.3 is resolved on that basis.
>
> **Known documentation inconsistency, not yet cleaned up:** this document's body references "DEC-9"
> and "DEC-11" (free-form arbitrary-fact scope, near-miss handling) as if they were requirements-doc
> decision IDs. `user-provided.md`'s actual decision log only runs DEC-1 through DEC-8 — DEC-9/DEC-11
> are artifacts of this document's own earlier draft and were never renumbered to match the
> reconciled requirements doc. The *content* behind them (free-form scope, warn-and-confirm near-miss
> banner) is still accurate/confirmed; only the DEC-numbering cross-reference is stale. Flagged for
> the architecture pass rather than silently left inconsistent.

# Unified Operator-Authored Device Facts — UX Design

> Companion to `docs/plans/user-provided.md` (requirements, status: approved). This file covers the
> UX pass that document explicitly deferred: the rebuilt device-detail tab (replaces "Manual
> Overrides"), the fleet-wide browse view (replaces the Custom Fields admin page), and the
> child-collection key combo box (REQ-010). No architecture/implementation design is included here
> — API shapes, storage layout, and near-miss matching algorithms are the architecture pass's job.
> This pass fixes *behavior and structure*, not code.
>
> **Run mode note:** this ran standalone, without `sdevcli` state or a `planning/` tree (see repo
> context — the workflow DB for this repo is orphaned). Per Boss's explicit instruction, all
> subprocess loops below (discovery → usability → structure → direction → mockups → evaluation)
> were run as one continuous internal produce/critique pass rather than as separate `sdevcli`-gated
> phases, with findings resolved in place rather than routed through a separate remediation cycle.
> Deviations from the standard task-card process are called out explicitly where they occur, with
> reasoning — see §6 in particular.

## Scope

In scope: the "Operator Facts" tab on `Pages/Reports/DeviceDetail.cshtml` (replaces "Manual
Overrides"), a new fleet-wide "Operator Facts" admin page (replaces `Pages/Admin/CustomFields.cshtml`,
same slot in `HubTabSets.Data`), and the child-collection key combo box used by both. Everything else
on `DeviceDetail.cshtml` and the Admin/Data hub is untouched.

---

## 1. Discovery

**Status:** completed, lightweight — proxy evidence only, no live interview.

**Method:** Boss is simultaneously the requirements author, the sole user, and the person who
raised this UX debt unprompted ("needs a lot of improvement"). There is no separate user to
interview, and `docs/plans/user-provided.md` §5 already documents the one persona (Admin Operator)
and its goals with enough specificity to design against. Rather than run a synthetic interview,
discovery here consisted of reading the shipped code that Boss actually uses today —
`DeviceDetail.cshtml`'s Manual Overrides tab, `CustomFields.cshtml`, and every structurally similar
peer screen in the Admin/Data hub (`Conflicts.cshtml`, `AuditLog.cshtml`) and one existing
combo-like control already in production (`mergeDeviceSearch` on `DeviceDetail.cshtml`) — to recover
the *actual*, currently-shipped interaction and visual conventions, rather than the requirements
doc's account of them. This is direct evidence about the system Boss uses today, which is the
closest available proxy for Boss's mental model (he built and uses this UI daily).

**Findings — current conventions confirmed by reading the code:**

| Convention | Evidence |
|---|---|
| Feedback idiom is a shared `#toast` div + `toast(msg)` fn, defined per-page | `CustomFields.cshtml:114-125`, absent from `DeviceDetail.cshtml`'s Manual Overrides script block, which uses `alert()` instead — confirmed inconsistency Boss flagged |
| Destructive-action confirmation is native `confirm()`, even in the newest Admin/Data peer page | `Conflicts.cshtml` (shipped after `CustomFields.cshtml`, per git log context) still uses `confirm()` for merge/exclude — this is not a legacy pattern being phased out, it's the current standard |
| Mutation feedback pattern varies by consequence: full `location.reload()` after create/save (acceptable — tab state survives via `?tab=` query param, confirmed in `DeviceDetail.cshtml:906-908`), but **targeted DOM removal** (`row.remove()`) after delete, no reload | `CustomFields.cshtml` `createDefinition()` vs. `deleteDefinition()` |
| A debounced-search-then-click-to-pick combo pattern already exists in production, using plain inline styles (no dedicated CSS class) and **no keyboard support** | `DeviceDetail.cshtml` `mergeDeviceSearch`/`onMergeDeviceSearchInput`/`runMergeDeviceSearch`/`.js-merge-device-pick`, lines ~874-1103 |
| Admin/Data hub pages share `_HubTabs` + `HubTabSets.Data` and, for list/browse pages, `_FilterBar` (chip+search, URL-driven) + a dedicated table partial | `Conflicts.cshtml`, `HubTab.cs` |
| The production design system already exists, is mature (2,229 lines), and already implements dark/light mode via a `.dark-mode`/`.light-mode` body class (not just `prefers-color-scheme`), keyset pagination (`.pagination`), banners, modals, toasts, empty states, and status pills | `wwwroot/css/design-system.css` |
| `FactPaths` catalog has 448 constants across ~25 comment-delimited groups (Hardware, Interface, Disk, Docker, Security, …) | `src/Core/Analysis/FactPaths.cs` |

**Mental model (inferred from code + requirements, not an interview):** Boss thinks of this feature
in terms of two now-merging concepts — "override what a collector said" and "add a field the
collector doesn't know about" — and one recurring frustration: not being able to target a *specific*
NIC/disk/whatever, only the whole device. The requirements doc's glossary (§6) already reflects this
vocabulary precisely (`override`, `arbitrary fact`, `child collection`, `natural key`), so it is
adopted verbatim below rather than re-derived.

**Evidence vs. assumption ledger:**

| Statement | Source | Classification | Validation needed? |
|---|---|---|---|
| `confirm()` is the accepted confirmation idiom app-wide, not legacy debt | Read `Conflicts.cshtml`, the newest Admin/Data page | direct-evidence | no |
| Toast + targeted DOM update (not reload) is the accepted post-delete idiom | Read `CustomFields.cshtml` | direct-evidence | no |
| A full reload after create/save is acceptable UX, not a bug, because tab state survives it | Read `DeviceDetail.cshtml` tab-switch code | direct-evidence | no |
| Boss wants the fact-path field searchable/grouped, not a flat 448-item `<select>` | Inferred from "needs a lot of improvement" + the raw `<select>` markup + FactPaths' scale | assumption (reasonable, but not literally said by Boss) | no — low risk, strictly an improvement on a confirmed-bad control, reversible in review |
| Boss wants label/description editable independent of any device having a value yet (path-level metadata, not just at first-value-entry time) | Inferred from REQ-011's "the architecture pass decides where this metadata is stored (e.g., a lightweight path-metadata table)" plus today's Custom Fields definition-first workflow | assumption | **yes — flagged to Boss explicitly, see §6.7** |

**Conflicts with requirements:** none found — the requirements doc explicitly deferred all UX
decisions to this pass and made no conflicting screen-level claims.

**Open questions carried in:** whether path-level metadata (label/description) should be editable
without a device value present — resolved as "yes, support it" in §6.7 below, flagged to Boss rather
than silently decided, because it changes the fleet-wide page from pure-browse to browse+manage.

---

## 2. Usability Design

**UX depth level:** standard-equivalent (three real, bounded screens/controls in a mature app —
this doesn't warrant the Deep-level complexity-triage machinery, but each interaction decision below
still gets full rationale). **Domain complexity:** standard (single expert operator, no regulated
data, no multi-user conflict scenarios).

### 2.1 Component decisions

| Element | Component chosen | Rejected alternative | Rationale |
|---|---|---|---|
| Fact path entry (create/override) | **Typeahead combo box** (text input + grouped, filtered suggestion list; typing a non-match is accepted) | Flat `<select>` (today's control) | 448 catalog entries in one unindexed dropdown is unusable — confirmed as the exact thing Boss flagged. A plain `<select>` also structurally cannot represent "type something that isn't in the list," which REQ-002 requires. |
| | | Server-side search (like `mergeDeviceSearch`) | 448 short strings is a trivial client-side filter; a network round-trip per keystroke adds latency and complexity for no benefit at this data size. Reserve server round-trips for things that scale past what the browser can hold (see fleet-wide browse, §2.1 below). |
| Child-collection key entry (REQ-010) | **Typeahead combo box**, sourced from a per-device "known keys for this collection" endpoint, accepts unmatched input | Free-text only (assumption A-2, since superseded by DEC-6) | DEC-6 already settled this — combo box, not free text, not a closed picker. |
| | | Closed `<select>` of only observed keys | Explicitly rejected by DEC-6/REQ-010 AC — a key not currently observed (e.g., a pulled disk) must still be enterable. |
| Near-miss catalog-path warning (DEC-11) | **Inline banner with two explicit affirmative actions** ("Use catalog path `X`" / "No, create new fact as typed") | Native `confirm()` | Documented, deliberate deviation from the app's standing `confirm()` convention (see §2.4) — `confirm()` offers only one framing (OK/Cancel around a single statement) and cannot show a second option as an equally valid affirmative choice. A single yes/no confirm would force phrasing this as "did you mean X? (yes/no)", which loses the second choice's visibility as a real path forward, not just a fallback. |
| Unified operator-facts table (device detail) | **Single data table** (`.data`), one row per (path, scope), replacing today's two separate tables (Manual Overrides + Custom Fields) | Two separate tables, kept side by side | REQ-004 retires Custom Fields as a *distinct concept* — keeping two visually separate tables would silently preserve the two-mechanism mental model this feature exists to remove. One table with a "kind" badge (Override / Arbitrary) keeps the distinction visible without it being a structural fork. |
| Revert/clear | **`confirm()` + targeted row removal + toast** | Reload | Matches the app's own most-recent precedent (`CustomFields.deleteDefinition`) and fixes the exact gap Boss flagged (today's Manual Overrides tab reverts with no confirmation at all). |
| Set/create/override submit | **`toast()` + `location.reload()`** | Targeted DOM insert | A new/changed row can shift which scope-key groupings exist, whether the empty state should still show, etc. — enough surface area that reconstructing it client-side risks drifting from server truth for marginal benefit, and reload-after-save is already the accepted idiom (`CustomFields.createDefinition`) with no user-visible cost (tab state survives). |
| Fleet-wide browse (REQ-006) | **Search-first, path-scoped table with keyset pagination**, plus a secondary "browse all paths with values" list | Dump every operator-authored fact across the fleet into one table | Stress-test: at scale (many devices × many arbitrary facts), an unfiltered table is unusable and cannot be usefully paginated by the user's actual question ("who has a value for fact X"). Search-first directly answers REQ-006's stated need. The secondary "browse all paths" list preserves today's Custom Fields capability of seeing *what fields exist at all* without knowing a path name in advance — see §2.5. |
| Value input | **Plain text input**, unchanged | Type-aware controls (date picker, number stepper, etc.) | Out of requirements scope (DEC-9: arbitrary facts are fully free-form; no NFR asks for typed input controls) and would add a dependency (NFR-7 discourages new third-party libraries). A small read-only "Type" hint badge is shown when type metadata exists (REQ-011), but it never changes which control renders the value. |

### 2.2 Process/workflow shape

Single-page, non-linear form — not a wizard. The three inputs (path, optional child-key, value,
optional label/description) have no required sequence except "path before child-key can be
evaluated for dimensionality." This matches the low frequency and low step-count of the task: an
admin doing this occasionally, filling 2-4 fields, does not benefit from a guided multi-step flow,
and a wizard would add friction to what should be a 10-second action for the common case (override
one device-scoped catalog value).

### 2.3 Information density

Dense, expert-oriented. Task frequency is low but the user is the app's sole admin and its author —
there is exactly one user, and that user is an expert in the domain by construction. No
novice-friendliness tradeoff exists to weigh against density; optimizing for scan-ability (mono-font
paths, compact rows, badges instead of prose) serves the only user this feature has. This mirrors
`design-conventions.md`'s existing choice (see the shipped `.data` table, `.mono` class, `.fs-xs`
labels) — nothing new is being introduced here, just applied consistently to the new controls.

### 2.4 Expert vs. novice affordances

Single persona — no novice affordances are being built, and this is a deliberate choice, not an
oversight: adding onboarding hints, tooltips-for-first-use, or a guided empty state would be design
effort spent on a user type (novice) that does not exist for this product. Where the design still
adds friction beyond today's baseline (the near-miss banner, the two confirmation dialogs), it is
because a mistake here is either data corruption (silent overwrite) or invisible confusion (a typo'd
override that "does nothing"), not because of a novice-safety goal.

### 2.5 Progressive disclosure

- Label/description entry is collapsed behind "Add label & description (optional)" on the
  create/override form — needed only for arbitrary facts an admin wants to keep human-readable
  later (REQ-011's use case), not on every entry.
- Child-collection scope entry (collection name + key) on the *arbitrary*-fact path is likewise
  hidden until the admin explicitly toggles "Scope: Device + collection instance" — most arbitrary
  facts, per DEC-9, will be simple device-scoped notes, and showing two extra fields by default for
  the common case is unjustified density.
- The fleet-wide page's secondary "browse all paths with values" list (§2.1) is a separate
  disclosure level below the primary search — it exists so the capability isn't lost, but it isn't
  the first thing shown, because "search for a specific path" is the more common question per
  REQ-006's own framing ("which devices have an operator-authored value for fact X").

### 2.6 Error recovery strategy

| Action | Confirmation? | Feedback | Validation timing |
|---|---|---|---|
| Set override / create arbitrary fact | Near-miss only (DEC-11) — otherwise no confirm, it's additive and revertable | Toast on error; reload on success | Inline, at submit time — wrong key-count and oversized-path errors (REQ-001/002 AC) render under the specific invalid field using the already-shipped `.field.invalid`/`.err` convention, not a toast, because they're field-specific |
| Revert/clear | Always — native `confirm()` | Toast on error; row removal + toast on success | N/A |
| Edit an existing row's label/description | No confirm (non-destructive) | Toast | Inline |

No action in this feature is undoable after confirmation — revert is itself the undo mechanism for
overrides, but reverting a revert (i.e., "I didn't mean to revert that") has no recovery path other
than re-entering the value, identical to today's Manual Overrides behavior. This is accepted as-is,
matching NFR-1's "audit parity with existing behavior," not a new gap introduced by this design.

### 2.7 Cross-cutting consistency

- `create-pattern`: inline form, always visible, above the results table — same shape on both the
  device-detail tab and (for path metadata only) the fleet-wide page.
- `edit-pattern`: click a row's value to edit it inline (matches `CustomFields.cshtml`'s existing
  per-row `<input>` pattern) — no modal for value edits.
- `delete-pattern` (revert/clear): `confirm()` + targeted row removal + toast, no exceptions.
- `confirmation-pattern`: destructive/irreversible → native `confirm()`; ambiguous-intent (near-miss)
  → inline dual-choice banner (documented exception, see §2.1); everything else → no confirmation.
- `empty-state-pattern`: `.empty` block with a one-line explanation + the create form is still shown
  above it (never "define something first, then come back") — see §6.3 for why this is a deliberate
  behavior change from today's Custom Fields flow.
- `navigation-pattern`: unchanged — tab within `DeviceDetail.cshtml`; `_HubTabs`/`HubTabSets.Data`
  for the fleet-wide page, same as every Admin/Data peer.

---

## 3. Structure

### 3.1 Information architecture

- **Device Detail → "Operator Facts" tab** (renamed from "Manual Overrides"; replaces that tabpanel
  entirely — `data-panel="manual-overrides"` becomes `data-panel="operator-facts"`, same tab-bar
  slot, same admin-only gating).
- **Admin → Data hub → "Operator Facts"** (renamed from "Custom Fields" in `HubTabSets.Data`; route
  moves from `/admin/custom-fields` to `/admin/operator-facts`; same tab position — 3rd of 3 in the
  Data hub, after Conflicts and OUI Database).
- No new top-level nav entries. No change to `HubTabSets.System`, `.Fleet`, `.Inventory`, or
  `.Network`.

**Labeling rationale:** "Operator Facts" (not "Manual Overrides," not "Custom Fields," not
"Operator-Authored Facts") — short enough to fit the existing tab-width conventions (peer labels:
"All Facts", "History", "Conflicts", "OUI Database") while using the glossary's actual umbrella term
in shortened form. "Manual Overrides" is retired because it no longer describes half of what the tab
does (arbitrary-fact creation isn't an override of anything).

### 3.2 User flows

**FLOW-001: Override an existing catalog fact (device-scoped)**
1. Admin opens a device's "Operator Facts" tab.
2. Types into the fact-path combo box; catalog matches appear grouped by section, filtered live.
3. Clicks a suggestion (or types the exact path and tabs out) → path field shows "Overriding catalog
   fact" badge; no child-key field appears (path has no collection dimension beyond `Device[]`).
4. Types a value, submits.
5. **Error path — wrong key count:** not reachable here (no key expected, none supplied) — see
   FLOW-002 for the case that can fail this way.
6. On success: toast "Override saved", page reloads with the tab still active, new/updated row
   visible in the unified table with an "Override" badge.

**FLOW-002: Override a child-collection-scoped catalog fact (REQ-001, REQ-010)**
1-3. As FLOW-001, but the selected catalog path has a collection dimension (e.g.
`Interface[].SpeedBps`) → a second combo box appears, labeled with the natural-key kind ("MAC
address") when known.
4. Admin types into the key combo; suggestions show the device's currently-observed instances
   (e.g. MACs with interface names) sourced from REQ-010's lookup. Admin picks one, or types a value
   with no match (accepted, not blocked).
5. Types a value, submits.
6. **Error path — no key supplied for a path that requires one:** inline error under the key field:
   "This fact needs an interface key — pick one or type one." Submit is blocked client-side before
   the round-trip; if bypassed, the server's REQ-001 AC rejection renders the same inline error from
   the response.
7. **Error path — key combo has zero observed instances for this device:** dropdown shows "No
   currently-observed interfaces on this device — you can still type a key" instead of a silent
   empty dropdown (a genuinely empty-looking dropdown reads as broken, not as "nothing to suggest").
8. On success: same as FLOW-001; the row's Scope column shows the compact form
   `Interface[aa:bb:cc:dd:ee:ff]`.

**FLOW-003: Create an arbitrary fact (REQ-002)**
1. Admin types a path that matches no catalog constant (e.g. `SwitchPortLabel`).
2. Path field shows "Creating new fact — not in catalog" badge, plus a persistent info banner:
   "Arbitrary facts never appear in reports or dashboards — they're visible and editable here and
   in the fleet-wide Operator Facts browse, nowhere else." (NFR-4 — set expectations, don't hide.)
3. **Branch — near-miss detected (DEC-11):** if the typed path is a close match to exactly one
   catalog constant, the badge in step 2 is replaced by the dual-choice banner from §2.1 instead.
   Choosing "Use catalog path" re-enters this flow at FLOW-001/002 step 3. Choosing "No, create new
   fact as typed" continues at step 4 below, and the path is *not* re-checked against near-miss
   again for this submission.
4. Admin optionally toggles "Scope: Device + collection instance," which reveals a free-text
   collection-name field and the same key combo box as FLOW-002 (sourced generically — since the
   collection name is admin-typed, not catalog-known, suggestions come from whatever collection
   names+keys the device's other arbitrary/catalog facts already use, if any; empty otherwise).
5. Admin optionally expands "Add label & description," fills them in.
6. Types a value, submits.
7. **Error path — path exceeds length/segment bounds (REQ-002 AC):** inline error under the path
   field: "Fact IDs are limited to 512 characters / 32 segments — this is N characters / N segments
   too long," not a silent truncation.
8. On success: toast "Fact created", reload, new row shows "Arbitrary" badge + the same
   not-in-reports indicator inline (small icon + tooltip, not repeating the full banner text per row).

**FLOW-004: Revert/clear an operator-authored fact (REQ-005)**
1. Admin clicks "Revert" on a row in the unified table (device-detail tab or, per §6.5, the
   fleet-wide page).
2. `confirm()`: "Revert [path] for this device? This restores whatever a collector last reported, if
   anything. This can't be undone from here." (Explicit about what "restores" means, since for
   arbitrary facts there is nothing to restore back to — see step 4.)
3. On confirm: request sent; on success, row removed from the table (targeted DOM update, not
   reload) + toast "Reverted."
4. **Branch — arbitrary fact with no collector-observed value beneath it:** the row simply
   disappears; there is nothing else to reveal. No separate messaging needed — the confirm dialog in
   step 2 already set that expectation.
5. **Error path:** request fails → toast with the error detail, row stays in place, no partial
   state (REQ-005/NFR-6).

**FLOW-005: Fleet-wide discoverability (REQ-006)**
1. Admin opens Admin → Data → "Operator Facts."
2. Default view: the fact-path combo box (same component, same catalog+free-text behavior as the
   create form) with no results table yet, and a link/toggle to "Browse all paths with values"
   below it.
3a. **Path search branch:** admin picks or types a path → results table loads (keyset-paginated):
    one row per (device, scope-key) with a current value for that path, each row linking to that
    device's Operator Facts tab, plus an inline Revert action (§6.5).
3b. **Browse-all branch:** table of every distinct operator-authored path currently in use, with a
    device count and, for paths that have label/description metadata, that label shown; clicking a
    row runs the FLOW-005 step-3a search for that path. This list also carries the path-level "Edit
    label/description" action from §6.7, independent of any device row.
4. **Error path:** search/browse request fails → page-level `.banner.error`, not a toast (this is a
   page-load failure, not an action failure) with a retry link.
5. **Empty path search result:** `.empty` block: "No device currently has an operator-authored value
   for this path."

### 3.3 Screen specs

**SCR-001: Device Detail — "Operator Facts" tab**

- **Purpose:** set, view, and revert every operator-authored fact for one device.
- **Primary user goal:** override a catalog fact or create an arbitrary one, for this device.
- **Entry points:** device-detail section nav (`data-tab="operator-facts"`), admin-only.
- **Content areas:**
  - NFR-4 info banner (`.banner.info`) — persistent, short, always visible on this tab (not
    conditional on having any arbitrary facts yet — sets the expectation before the admin creates
    one).
  - Create/override form — path combo, conditional child-key combo, conditional
    scope-toggle+collection-name (arbitrary paths only), value input, collapsed label/description,
    submit button. Component types per §2.1.
  - Unified operator-facts table (`.data`) — columns: Path (mono), Scope, Kind badge
    (Override/Arbitrary), Label, Value, Set by / when, Actions (Revert).
- **Key interactions:** see FLOW-001 through FLOW-004.
- **Data shown:** every `FactSource.ManualEntry` row for this device (both former mechanisms,
  unified) — path, scope key(s), value, label/description if present, kind, author, timestamp.
- **States:**

  | State | Description |
  |---|---|
  | Populated | Table shows N rows, form above it always present |
  | Empty | `.empty` block: "No operator-authored facts for this device yet." — form is still shown above it, not gated behind an extra step (see §6.3) |
  | Loading | Standard page load — this tab renders server-side with the rest of the page, no separate spinner needed (matches today's behavior) |
  | Error | If the device's fact list fails to load, `.banner.error` in place of the table, form still usable |
  | Near-miss in progress | Dual-choice banner replaces the path-status badge (FLOW-003 step 3) |
  | Single row | No special case — table renders one row normally |
  | 100+ rows | Table scrolls within `.detail-table-scroll` (existing device-detail convention, confirmed at `DeviceDetail.cshtml:662`) rather than growing the page |

- **Navigation context:** sibling tab to "All Facts", "History", etc. within Device Detail's section
  nav.
- **Related flows:** FLOW-001, FLOW-002, FLOW-003, FLOW-004.

**SCR-002: Admin — "Operator Facts" (fleet-wide browse)**

- **Purpose:** answer "which devices have an operator-authored value for fact X" and "what
  operator-authored facts exist at all," without visiting every device (REQ-006).
- **Primary user goal:** find every device with a value for a given fact path.
- **Entry points:** `_HubTabs`/`HubTabSets.Data`, admin-only, same as every Admin/Data peer.
- **Content areas:**
  - Path combo box (primary search) + "Browse all paths with values" toggle/link.
  - Results table (path-search mode): Device (linked), Scope, Value, Label, Set by/when, Actions
    (Revert).
  - Results table (browse-all mode): Path (mono), Device count, Label (if any), Actions (Edit
    label/description, per §6.7).
  - `.pagination` (keyset) on both table modes.
- **Key interactions:** see FLOW-005.
- **Data shown:** cross-device operator-authored fact rows for a chosen path, or the distinct list
  of paths with counts.
- **States:**

  | State | Description |
  |---|---|
  | Populated (path search) | N device rows for the chosen path |
  | Populated (browse-all) | N distinct paths |
  | Empty (path search) | `.empty`: "No device currently has an operator-authored value for this path." |
  | Empty (browse-all, whole feature unused yet) | `.empty`: "No operator-authored facts exist yet. Set one from any device's Operator Facts tab." |
  | Loading | Standard page load (server-rendered first page; keyset "next" is the only client round-trip) |
  | Error | `.banner.error` with retry, in place of the table |
  | 1,000+ devices for one path | Keyset pagination — same mechanism as every other paginated table in the app, no new pattern |

- **Navigation context:** 3rd tab in the Admin/Data hub, after Conflicts and OUI Database.
- **Related flows:** FLOW-005.

**Combo box — reusable control spec** (used by SCR-001's path field, SCR-001's child-key field, and
SCR-002's path field)

- Text `<input class="input">` with `autocomplete="off"`, `role="combobox"`,
  `aria-expanded`, `aria-controls` pointing at the results listbox, `aria-autocomplete="list"`.
- Results container: `role="listbox"`, options `role="option"`, each with `aria-selected`.
- Keyboard: Up/Down moves `aria-activedescendant` through visible options without closing the list;
  Enter selects the active option; Escape closes the list without clearing the typed text; typing
  continues filtering at all times. This is a deliberate upgrade over the existing
  `mergeDeviceSearch` precedent, which has no keyboard support at all — see §6.6.
- Catalog-sourced instances (fact-path combo) filter client-side against an inline list already sent
  with the page (same data volume as today's `Model.EditablePaths`, just grouped); child-key and
  browse-all-paths instances are server-sourced via the same debounced-fetch pattern as
  `mergeDeviceSearch`.
- Typing a value with no matching option is always accepted on blur/submit — never blocked — for
  every use of this control (fact path → arbitrary fact; child key → REQ-010 AC).

---

## 4. Design Direction

**Deliberate deviation from the standard Phase 2 process, and why:** the task card for this phase
assumes a product establishing its visual identity for the first time — building 3+ meaningfully
distinct design-system variations, scoring them on a distinctness rubric, and getting the user to
pick one. That process is for greenfield products. JMW.Agent already has a shipped, coherent,
2,229-line production design system (`src/Server.Web/wwwroot/css/design-system.css`) that Boss's own
task framing explicitly says to extend, not replace ("prefer extending that pattern over introducing
a third feedback idiom," "check other peer screens for structural consistency"). Running the
variation-generation exercise against an app that already has a settled, working identity would
produce throwaway alternatives to a design that isn't up for reconsideration, and would contradict
the instruction this task was scoped under. This is treated as a justified process deviation, not a
skipped step — the equivalent output (a defined, documented visual direction with token references)
already exists in the codebase and is cited below instead of re-derived.

**Existing tokens/components reused as-is** (no new design system entries needed): `.field`,
`.input`, `.btn`/`.btn-primary`/`.btn-sm`/`.btn-danger`, `.data` table + `.mono`, `.tabs`/
`.section-nav`, `.toast`, `.empty`, `.banner`/`.banner.info`/`.banner.error`/`.banner.stale` (the
near-miss dual-choice banner reuses the existing warn-toned `.stale` variant rather than introducing
a new `.banner.warn` — no new banner variant is needed), `.pagination`,
`.status` pills (repurposed for the Override/Arbitrary kind badge — two more `.status` variants
using existing `--ok`/`--info` tokens, no new colors), `_HubTabs`/`HubTabSets.Data`,
`_FilterBar`-style search-first pattern (informing SCR-002's structure, though SCR-002's own search
box is the combo control, not the generic chip filter bar — the two solve different problems and
aren't interchangeable).

**One genuinely new addition — the combo box component.** A debounced/filtered typeahead-with-picks
pattern already exists three times in intent (this feature's two combo boxes plus the pre-existing
`mergeDeviceSearch`) but has never been formalized as a named component — today it's copy-pasted
inline styles. This pass formalizes it as `.combo` / `.combo-results` / `.combo-option` using only
existing custom properties (`--surface`, `--surface-hover`, `--border-hair`, `--space-2`,
`--radius-md`, `--fs-sm`) — no new colors, spacing, or type introduced. The proposed CSS (to be added
to `design-system.css` by the implementation pass, not by this UX pass):

```css
/* ===== Combo box (typeahead + pick list) ===== */
.combo { position: relative; }

.combo-results {
    position: absolute;
    top: calc(100% + var(--space-1));
    left: 0;
    right: 0;
    max-height: 280px;
    overflow-y: auto;
    background: var(--surface);
    border: var(--border-hair);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-md);
    z-index: 50;
}

.combo-group-label {
    padding: var(--space-1) var(--space-3);
    font-size: var(--fs-xs);
    color: var(--text-faint);
    text-transform: uppercase;
    letter-spacing: .06em;
}

.combo-option {
    padding: var(--space-2) var(--space-3);
    cursor: pointer;
    font-size: var(--fs-sm);
}

.combo-option:hover, .combo-option[aria-selected="true"] {
    background: var(--surface-hover);
}

.combo-empty {
    padding: var(--space-3);
    font-size: var(--fs-sm);
    color: var(--text-dim);
}
```

This is deliberately scoped to visuals only — the accessibility behavior (keyboard nav, ARIA wiring)
is specified in §3.3's combo box control spec, not here, per the app's own convention of keeping
usage rules out of the token/visual file.

**Named interaction pattern rules (extending, not replacing, the app's existing implicit
conventions):**

- `create-pattern`: inline form, always visible above the results — matches `CustomFields.cshtml`.
- `edit-pattern`: click-to-edit inline value input — matches `CustomFields.cshtml`.
- `delete-pattern`: `confirm()` + targeted row removal + toast — matches `CustomFields.cshtml`'s
  `deleteDefinition`, and fixes `DeviceDetail.cshtml`'s current gap (no confirm at all).
- `confirmation-pattern`: destructive → `confirm()`; ambiguous-intent (near-miss only) → inline
  dual-choice banner, a **documented exception** to the `confirm()` default, justified in §2.1.
- `empty-state-pattern`: `.empty` block, create affordance never gated behind it.
- `navigation-pattern`: unchanged — `_HubTabs` for admin pages, device-detail section nav for the
  device tab.

**Accessibility:** WCAG 2.1 AA (matching the rest of the app — no explicit accessibility target is
documented elsewhere in the codebase, so AA is assumed as the reasonable default for an admin tool,
not independently confirmed with Boss). Color contrast: reuses existing tokens already presumed
AA-compliant elsewhere in the shipped system (not re-verified here — that verification is a Phase
3/audit-pass concern for any *new* color use, and this design introduces none). Keyboard: combo box
spec in §3.3; every other control is a standard `<input>`/`<button>`, natively keyboard-operable. No
reliance on color alone — the Override/Arbitrary kind badge is a `.status` pill with icon shape +
label text, not a bare color chip, matching the app's existing status-pill convention exactly.

---

## 5. Mockups

Full HTML mockups, importing the real production `design-system.css` (not a recreation of it), live
under `docs/plans/ux-operator-facts/mockups/`:

- `device-detail-operator-facts.html` — SCR-001, showing: the unified table with a mix of Override
  and Arbitrary rows, the NFR-4 banner, the create form with the child-key combo open on an
  `Interface[].SpeedBps` override, and the near-miss dual-choice banner state.
- `admin-operator-facts.html` — SCR-002, showing: path-search mode with results, and the
  browse-all-paths mode with the path-level label/description edit affordance (§6.7).

Both are static (no live backend) but include real inline JS demonstrating the combo box's
filter/keyboard behavior, the near-miss banner's two-choice branch, and the toggle between
path-search and browse-all modes on the admin page — so the interactions described in §3.2's flows
are actually exercisable, not just described. Both include the same light/dark toggle button
(`.mode-toggle`, reusing the app's real toggle markup and behavior) so both modes can be reviewed.

Interaction spec for anything not self-evident from clicking the mockups:

- **Near-miss trigger (mockup only):** typing `Interfac` or `Interface.Speed` in the path combo on
  `device-detail-operator-facts.html` demonstrates the near-miss banner against
  `Interface[].SpeedBps` — a hardcoded example standing in for the architecture pass's real
  similarity check.
- **Child-key combo empty state:** clearing the interface-key field and retyping demonstrates the
  "no currently-observed instances" message when the filter matches nothing.
- **Kind badges:** `.status.ok` (green, "Override") vs. `.status.info` (blue, "Arbitrary") — chosen
  from the existing four `.status` variants since neither of Override/Arbitrary is an error or
  warning condition; this reuses tokens, it does not introduce new ones.

---

## 6. Evaluation & Self-Critique

Run as one internal pass across both screens and the combo control, rather than the full separate
Nielsen/walkthrough/WCAG-pre-audit artifact set — proportionate to a 3-surface, single-persona
feature. Findings below were found and fixed *before* finalizing this document; they are kept
visible rather than silently absorbed, per the standing instruction to show real critique, not just
approval.

### 6.1 Heuristic pass (summary, both screens)

| Heuristic | Assessment |
|---|---|
| H1 Visibility of system status | Pass — toast on every mutation, badges show override-vs-arbitrary state live as the admin types |
| H2 Match with real world | Pass — glossary terms used verbatim (Override, Arbitrary fact, Scope, Natural key implied by the combo's suggestions) |
| H3 User control and freedom | Pass — near-miss banner explicitly gives a way *out* of the auto-detected framing ("No, create new fact as typed") |
| H4 Consistency | Pass, with one documented exception (near-miss banner vs. `confirm()`) — see §2.1, §4 |
| H5 Error prevention | Pass — inline field errors before submit for key-count/length; near-miss warning before an accidental parallel-mechanism creation (REQ-003) |
| H6 Recognition over recall | **Initial fail, fixed:** first draft of SCR-002 required the admin to already know a fact path to search it — no way to *discover* arbitrary paths that exist. Fixed by adding the "browse all paths with values" secondary mode (§2.5, §3.2 FLOW-005 step 3b). |
| H7 Flexibility/efficiency | N/A — no expert/novice split exists for this persona (§2.4) |
| H8 Aesthetic/minimalism | Pass — no new visual elements beyond one small reusable combo component; NFR-4 banner is one line, not a wall of text |
| H9 Error recovery | Pass — every inline error states what's wrong and what value would fix it (character/segment counts, missing key) |
| H10 Help/documentation | N/A by design — single expert user, no help system exists elsewhere in the app to be consistent with |

### 6.2 Cognitive walkthrough spot-check (FLOW-003, arbitrary fact creation, worst case for a first-time reader)

1. Will the admin try to achieve the right effect? Yes — the badge and banner make the "this is new,
   not a catalog thing" state impossible to miss.
2. Will they notice the correct action is available? Yes — the label/description and scope-toggle
   disclosures are visible collapsed affordances, not hidden menu items.
3. Will they associate the action with the effect? Yes for label/description; **initially uncertain
   for the scope toggle** — "Scope: Device + collection instance" as a toggle label doesn't by
   itself convey that it reveals two more fields. Accepted as a minor (Severity 3) gap: the toggle
   is self-revealing on click (the fields appear immediately), so the cost of an ambiguous label is
   one click, not a dead end. Not fixed — flagged rather than silently left off this document.
4. Will they see progress? Yes — toast + reload + new row visible with the correct badge.

### 6.3 Stress test

1. **Scale (0/1/1000+):** covered explicitly per screen in §3.3's state tables; fleet-wide browse
   uses keyset pagination specifically because "1,000+ devices for one path" is a real scenario at
   Boss's fleet size over time, not a hypothetical.
2. **Error path completeness:** every failure point named in FLOW-001 through FLOW-005 has an
   explicit user-visible outcome; none silently no-op (this was the exact original bug — silent
   overwrite on revert — and is checked against by name in FLOW-004 step 2's confirm text).
3. **Requirement fidelity (3 most complex REQs):**
   - REQ-001 (override any catalog fact incl. child-collection-scoped) — delivered via FLOW-002,
     not simplified to device-scope-only.
   - REQ-003 (disambiguate override vs. create) — delivered via the live badge state as the admin
     types, plus the near-miss banner for the genuinely ambiguous case; not simplified to "let the
     backend decide silently."
   - REQ-006 (fleet-wide discoverability) — delivered via both search-first *and* browse-all modes,
     not just a search box that assumes the admin already knows what to search for (the H6 fix in
     §6.1 exists specifically because a search-only design would have been a simplified,
     requirement-violating version of REQ-006).
4. **Data assumption audit:** every screen's data needs are stated in "Data shown" (§3.3); the one
   genuinely ambiguous shape — whether label/description is a per-(device,path) or per-path
   record — is called out explicitly to Boss in §6.7, not left for the architecture pass to guess.
5. **Cognitive load:** SCR-001's create form is the densest surface (up to 6 fields when both
   optional disclosures are open); each disclosure defaults closed specifically to keep the common
   case (device-scoped override) at 2 visible fields (path, value).

### 6.4 WCAG spot-check

Perceivable/Operable: combo box ARIA + keyboard spec (§3.3) is the one place this design goes beyond
copy-pasting an existing (keyboard-inaccessible) pattern — see §6.6. Understandable: every error
message names the specific limit or missing field, never a bare "invalid input." Robust: semantic
`<table>`/`<form>`/`<label>` throughout, no div-soup — matches the existing mockup files' own
markup style, confirmed by reading them.

### 6.5 Findings requiring explicit note to Boss (not silently decided)

1. **Fleet-wide revert action (§3.2 FLOW-005, SCR-002):** REQ-006's acceptance criteria only require
   *listing* devices with a value, not acting on them from that list. This design adds a Revert
   action directly on the fleet-wide table anyway, on the principle from the mockups task card that
   anything listed with a delete-capable peer view should be actionable, not just visible — but this
   is additive scope beyond the literal requirement. Flagged, not silently shipped as if it were
   required.
2. **Combo box keyboard accessibility (§3.3, §4):** the existing `mergeDeviceSearch` control this
   pattern is modeled on has no keyboard support today. This design does not retrofit that existing
   control (out of this feature's stated scope — "do not touch... other parts of DeviceDetail.cshtml
   beyond what's needed for consistency"), but recommends it as a follow-up, since leaving one combo
   box keyboard-accessible and two others not would itself be an inconsistency.
3. **Path-level label/description editing independent of any device value (§1 ledger, §3.2 FLOW-005
   step 3b, §3.3 SCR-002) — RESOLVED (confirmed by Boss, 2026-07-16): yes.** REQ-011 says
   label/description must "survive migration and remain associated with the corresponding fact path"
   but didn't explicitly require *editing* that metadata independent of a device value. This design
   adds that editing affordance because today's Custom Fields workflow supports defining a field
   before any device has a value, and REQ-004 requires every Custom Fields capability to have an
   equivalent path — dropping "define/label a field before rolling it out to devices" would have been
   a real capability regression. Boss confirmed path-level, independent editing is required; the
   architecture pass should design the metadata storage (e.g. a path-metadata table, per REQ-011)
   accordingly, keyed by path alone, not by (device, path).
4. **Behavior change from today, worth surfacing plainly:** Custom Fields today requires a two-step
   workflow (define the field on the admin page, *then* set a value on a device page). This design
   collapses that to one step — setting a labeled value directly from the device page — and moves
   the fleet-wide page to a browse/manage role rather than a definition-creation role. This is a
   genuine simplification, not a hidden side effect, and is called out here so it isn't discovered
   as a surprise later.

### 6.6 Verdict

**Approved**, standing in for the producer/critic loop's terminal state, with the four items in §6.5
carried forward as open notes for Boss and/or the architecture pass rather than blocking this UX
pass — none of them are Severity 1/2 usability defects in the design as specified; they are scope and
confirmation flags on decisions this pass made explicitly rather than silently.

---

## 7. Traceability (requirements → UX)

| Requirement | UX coverage |
|---|---|
| REQ-001 | FLOW-001, FLOW-002, SCR-001 |
| REQ-002 | FLOW-003, SCR-001 |
| REQ-003 | Live badge state + near-miss banner, FLOW-003 step 3, §2.1 |
| REQ-004 | Unified table (§2.1), retirement of the definition-first workflow (§6.5.4) |
| REQ-005 | FLOW-004 |
| REQ-006 | FLOW-005, SCR-002 |
| REQ-010 | Child-key combo, FLOW-002 steps 4/7 |
| REQ-011 | Label/description disclosure (§2.5), path-level edit (§6.5.3, flagged for confirmation) |
| NFR-1 | "Set by / when" column, SCR-001/SCR-002 |
| NFR-2 | Both screens admin-only, matching existing gating pattern (`User.IsInRole("admin")`) |
| NFR-4 | Persistent banner + per-row indicator, §3.2 FLOW-003 step 2/8 |
| NFR-6 | Inline field errors, never silent no-op — §2.6, §6.3.2 |
| DEC-11 | Near-miss dual-choice banner, §2.1 |
