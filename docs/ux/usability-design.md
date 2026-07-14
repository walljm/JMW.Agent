---
agent: sdev-ux
date: 2026-06-04
iteration: 2
revision_id: 7
status: draft
---

# Usability Design — JMW Agent Facts Server

## UX Depth Level

standard

## Domain Complexity

complex — expert vocabulary, high information density, and load-bearing fact provenance (multi-source merge precedence per REQ-035/REQ-038). See `index.md` for the full rationale.

## Discovery Inputs

Phase 0 (discovery) was skipped per orchestrator briefing. No `discovery.md` exists. This usability design is grounded in the approved requirements (REQ-001–REQ-038), the two persona records, and the glossary, rather than a fresh user interview. The project owner (Jason Wall) is the sole decision-maker AND the primary operator (PERSONA-001), so the requirements and personas are first-person authoritative, not proxy evidence — the mental model below is synthesized from those records.

**Interview substitute note:** Because this is an autonomous workflow run, the live Work & Mental Model interview was not conducted. Where a decision would normally hinge on an interview answer, it is grounded in explicit requirement text and flagged. Two assumptions remain unverified and are listed under "Open Assumptions for Validation" at the end.

## User Mental Model Summary

The operator thinks about the system as **a fleet of hosts, each of which is either something I run an agent on (managed) or something I merely see on the wire (discovered).** Everything radiates from the host.

Key objects and how the operator holds them:

- **Host / Device** — the central object. The operator does not distinguish "managed device" and "discovered device" as two separate kinds of thing; they are one thing (a box on the network) seen at two levels of fidelity. This is why REQ-037 mandates a *single unified* All Hosts table, not two lists. Confidence in what they know about a host is a spectrum, not a binary.
- **Agent** — "the thing I installed that does the collecting." The operator configures agents and assigns them targets. Agents are infrastructure, not subjects of monitoring per se (though an offline agent is itself a problem to surface).
- **Fact** — the operator does not think in raw facts; they think in *categories of information about a host* (its disks, its certs, its open ports). Facts are the storage primitive; the UI presents them grouped into the operator's categories (the Device Detail sections).
- **Provenance** — for discovered hosts, the operator constantly asks "how do I know this, and how sure am I?" A hostname seen via mDNS + confirmed by LLDP is trustworthy; a lone ARP entry is a guess. The operator needs to see source and recency to decide whether to act.
- **Cross-fleet concern** — the operator also thinks horizontally: "show me every expiring cert," "every host missing a firewall," "what's listening on port 22 anywhere." These are the reporting views; they slice the fleet by one attribute rather than drilling one host.

The two dominant work loops:

1. **Daily scan** (high frequency): open dashboard → is any count non-zero/red? → click the offending card → see the filtered list → triage. The operator wants this to take seconds and to be glanceable.
2. **Investigation** (medium frequency): something looks wrong → drill into the specific host/cert/service → read the detail → decide. Success criterion 3 caps this at 3 clicks from the dashboard.

Lower-frequency loops: configuring a new agent/target (weekly or less), rotating credentials (during cert/credential rotation periods), promoting a discovered host to managed (occasional, deliberate).

## Screen/Task Decisions

The screens fall into four structural families. Decisions are stated per family with screen-specific notes, because peer screens must share patterns (consistency is a hard requirement). Three screens warrant individual treatment beyond their family: Fleet Dashboard, All Hosts, and Device Detail.

---

### Family A: Cross-fleet reporting tables

Screens: Device List, Service List, Security Posture, Certificate Inventory, Open Ports, Container Fleet, Patch Status, Storage Health, Network/ARP, Change Feed, Admin Accounts/Sessions, and All Hosts (which extends this family — see its own section).

- **Primary user goal:** scan a single cross-fleet concern, filter/sort to the rows that matter, click through to a host or entity detail.
- **Task frequency:** high (these are the daily-scan and triage destinations).
- **Component choices:**
  - **Data display: dense data table.** Chosen over card grid because the primary action is *scanning and comparing* many rows on shared columns (PERSONA-001 explicitly wants information density). Cards waste vertical space and break column alignment that lets the eye compare values down a column. Lists (single-column) lose the multi-attribute comparison these screens need.
  - **Filtering: toolbar filters above the table + free-text search.** Chosen over a persistent sidebar filter panel because (a) the sidebar is already consumed by primary navigation, and (b) most reporting views filter on 1–3 dimensions (e.g., cert status, expiry window), not a dozen faceted dimensions where a sidebar earns its space. Filters are expressed as URL query params so dashboard cards can deep-link into a pre-filtered state (REQ-010).
  - **Sorting: clickable column headers.** Standard, expected by expert users, no rationale beyond convention.
  - **Pagination: keyset/cursor pagination, 100 rows/page default.** Required by code-guidance (keyset, never offset) and REQ-037 (100/page). The control reads "Next/Previous" rather than numbered pages because keyset pagination cannot cheaply jump to page N.
  - **Status representation: icon + text label + color (never color alone).** Each status cell carries a shape/glyph and a word, with color as reinforcement — required for the accessibility rule "no reliance on color alone" and because the operator scans these in a dim room.
- **Information density: dense.** High task frequency + expert user. Compact row height, no decorative whitespace inside the table. Generous space is spent only on the surrounding chrome (filter bar, page header), not the data.
- **Progressive disclosure:** the table shows the high-value scan columns only; full per-entity detail is one click away on the detail page. Where a cell could overflow (long cert subject, many discovery-source tags), it truncates with a tooltip/expander rather than wrapping and breaking row rhythm.
- **Process shape:** single page, no multi-step process. Filter state lives in the URL so it survives refresh and is shareable (a viewer can be sent a link to a pre-filtered view).

---

### Fleet Dashboard (Family A's entry point, but distinct)

- **Primary user goal:** answer "is anything wrong?" in one glance, then jump straight to the problem.
- **Task frequency:** highest of any screen — opened on every login and as a periodic health check.
- **Component choices:**
  - **Summary cards (not a table) for the top-level counts.** This is the one place a card grid beats a table: each metric is a single number with a status color and a destination, and the operator scans for *which card is hot*, not for comparing rows. Cards give large, glanceable numbers and a big click target.
  - **Every card is a link to a pre-filtered reporting view** (REQ-010). A hot card ("12 expiring certs") navigates to `Certificates` already filtered to the expiring set. This is the backbone of the scan loop and dictates the shared filter-param convention defined in Phase 1 structure.
  - **Auto-refresh with a visible interval control** (30s / 60s / 5m / off, default 60s, per REQ-010) and a "last updated" timestamp. The control is a small select in the page header. Refresh updates counts in place; it must not reset the operator's scroll position or steal focus.
- **Information density: moderate.** Cards are deliberately larger and more spaced than table rows — this is the one screen optimized for a 2-second glance from across the room, so legibility beats density here. The card *grid* is dense (many cards visible without scrolling) but each card is generously sized.
- **Progressive disclosure:** the dashboard shows counts only; the detail is on the linked pages. A managed-vs-discovered device-count breakdown (REQ-030) appears on the device-count card without requiring a click.
- **Process shape:** single page.

---

### All Hosts ⟶ careful treatment (most novel screen)

- **Primary user goal:** see *every* host the system knows about — managed and discovered together — judge how well each discovered host is characterized, and promote the ones worth managing.
- **Task frequency:** medium. Reviewed when auditing network coverage or deciding what to bring under management.

**Why this is the hard screen:** it must hold two populations with different data shapes (managed hosts have OS/agent/full profile; discovered hosts have only passive signals) in *one* table without making the table feel like two stapled-together things, and it must communicate *confidence* — a concept that doesn't exist on any other screen.

- **Component choices:**
  - **Single unified data table** (REQ-037 mandates "single unified list"). Managed-only columns (OS, Agent Name) are simply blank/"—" for discovered rows. Rejected alternative: two stacked tables (managed above, discovered below) — rejected because it fights the operator's mental model (one fleet, two fidelities) and because REQ-037 explicitly says single list. Rejected alternative: a card grid — rejected for the same density reasons as Family A.
  - **Management status: badge with distinct shape AND fill, not color alone.** Managed = solid/filled badge; discovered = outlined/muted badge (REQ-037 specifies exactly this). The shape difference (solid vs outline) carries the meaning so it survives grayscale and colorblindness.
  - **Discovery sources: compact tag list per row** (e.g., `ARP` `mDNS` `LLDP`). Tags are read-only chips, not interactive on the row (they ARE filterable from the toolbar). The *count* of distinct tags is itself the at-a-glance confidence cue: one tag = weakly known, several concordant tags = well characterized (REQ-038).
  - **Confidence: expressed through the source-tag count + a "Last Seen" recency column**, not a separate numeric score. Rationale: REQ-038 frames confidence as "seen by N sources, last seen X ago" — a derived narrative, not a computed 0–100 score that would imply false precision. A host "possibly offline" (no source within 2× its interval, REQ-038) gets a muted/warning treatment on its Last Seen cell.
  - **Promote: inline action button on discovered rows only, Admin role only** (REQ-036/REQ-037). Hidden entirely (not just disabled) for the Read-Only Viewer and not rendered on managed rows. Launches the promotion workflow (see Cross-Cutting → Promotion).
  - **Filtering (toolbar):** management status, discovery source, OUI vendor prefix, free-text against hostname+IP (REQ-037). Sorting: hostname, IP, last seen, status, vendor.
- **Information density: dense**, consistent with Family A.
- **Progressive disclosure:** the row shows identity + status + sources + recency; full provenance (per-source first/last seen, observation counts, all conflicting values) lives on the Device Detail page's Discovery Sources panel. The row answers "how sure am I, roughly?"; the detail page answers "exactly which agent saw it via which protocol, how often."

**Realistic-scale scenario (happy path, ~250 hosts):** operator opens All Hosts after deploying agents to a new subnet. The table shows ~40 managed rows (solid badge, OS+agent populated) interleaved with ~210 discovered rows (outline badge, OS/agent blank, 1–3 source tags each). Operator filters source = `mDNS` to find hosts advertising services, sorts by Last Seen to surface the freshest, spots a NAS seen by ARP+mDNS+LLDP (3 concordant sources, high confidence), and clicks Promote on its row.

**Failure/recovery scenario (promotion fails):** operator promotes the NAS, selecting an agent and stored SSH credential. The agent cannot reach it (wrong credential). Per REQ-036 the host reverts to discovered and the operator is notified in the Admin area. Recovery: the operator sees the failure notice, edits the target/credential, and re-promotes — the host's passive profile was never lost (REQ-035/036 retain all prior facts), so no work is destroyed. This is the key reason promotion is *additive*, not a destructive state change.

---

### Device Detail ⟶ careful treatment (highest density)

- **Primary user goal:** read everything known about one host, organized into the operator's mental categories.
- **Task frequency:** high (the investigation-loop endpoint).
- **Component choices:**
  - **Tabbed (or anchored-section) layout over the ~17 data categories** (REQ-012). Tabs chosen over one long scroll because 17 sections of tables is too much to scroll through to reach "Certificates"; tabs let the operator jump to the category they care about. Identity header (hostname, UUID, vendor, model, type, zone, agent/status) is persistent above the tabs — it's the context that frames every tab.
  - **Sections with no data are hidden, not shown empty** (REQ-012, REQ-030). A discovered host or a workstation with no containers simply doesn't render those tabs. This is deliberate: an empty "Containers" tab on every workstation would be noise. (This is the one place we override the global empty-state pattern — documented as an exception in design-conventions later.)
  - **For discovered hosts:** the "collecting agent" identity field is replaced by a **Discovery Sources panel** (per-source first/last seen, observation count, REQ-038) and a header confidence summary ("Seen by 3 sources, last 5 min ago"). Provenance for the key identity fields (hostname, IP, MAC, vendor) shows the canonical value with a **"show all sources" toggle** (REQ-035) that reveals lower-precedence conflicting values with their source attribution. Default is collapsed (canonical only) so the common case stays clean; the toggle serves the moment the operator distrusts a value.
  - **Within each section: data table or key-value list** depending on cardinality. Single-instance data (OS, hardware identity) → key-value list. Multi-row data (interfaces, disks, ports, certs, ARP) → dense table, paginated where unbounded (ports, ARP per REQ-012).
  - **Routes:** summary stats inline, full table behind a "View all routes" action (REQ-012 / DEC-004 — routes are queried on demand from history, not projected).
- **Information density: dense within tabs; the identity header is moderate** (it's the persistent anchor and deserves a little breathing room).
- **Progressive disclosure:** tabs themselves are the primary disclosure mechanism; the "show all sources" provenance toggle and "View all routes" are secondary disclosures for occasional deep inspection.
- **Provenance handling (complex-domain obligation):** source attribution is first-class on this screen for discovered hosts — it is *the* screen where the operator evaluates trust. Canonical-value precedence (`agent-direct > lldp > mdns > nbns > arp/dhcp`, REQ-035) determines which value shows by default; the toggle never silently drops a lower-precedence value.

---

### Family B: Admin configuration screens

Screens: Agent List, Agent Detail/Configuration, Remote Targets, Credentials.

- **Primary user goal:** set up and adjust agents, targets, and credentials. Infrequent, deliberate, must-not-fumble work.
- **Task frequency:** low (weekly or less for agents/targets; episodic for credential rotation).
- **Component choices:**
  - **List + detail pattern for agents.** Agent List is a table (Name, Zone, last heartbeat, online/offline status). Clicking an agent opens its detail page, which contains the editable Configuration section (REQ-005) and its target lists.
  - **Editing: dedicated form section on the detail page, with explicit Save** (not inline-cell editing, not a modal). Rationale: agent config is a coherent set of related fields (Name, Zone, Interval, MaxConcurrency) that the operator reviews and commits together; partial/auto-save risks pushing a half-edited config to the agent on its next poll. An explicit Save with a "pending changes vs. last-delivered" indicator (REQ-005) makes the commit deliberate and shows config-delivery lag honestly.
  - **Targets (device/service): table with add/edit/delete.** Adding or editing a target opens a **modal form** — a target is a small, self-contained record (address, protocol, credential reference) and the operator usually adds one without leaving the agent context. Modal keeps them anchored to the agent they're configuring.
  - **Credentials: table + modal create/edit.** Secret entry is **write-only** — the secret field is empty on edit (never pre-filled, never displayed; REQ-007). Rotation is "enter a new secret" in the same modal; the label and references are preserved. Type is a select (SSH password, SSH key, SNMP v1/v2c, SNMP v3, HTTP bearer, HTTP basic) that changes which secret fields show.
- **Information density: moderate.** Lower frequency + higher consequence of error → more spacing, clearer field labels and help text than the dense reporting tables. This is a deliberate density *exception* from the reporting screens, justified by frequency and stakes.
- **Progressive disclosure:** credential secret fields are revealed by the chosen type. Agent config shows current settings; "Advanced" (MaxConcurrency, per-collector frequency overrides) is grouped but visible — the expert operator wants it, so it's not hidden behind a separate screen, just visually separated from the everyday Name/Zone/Interval fields.
- **Process shape:** hub-and-spoke, not a wizard. The Agent Detail page is a hub; collectors, targets, and config are spokes the operator visits in any order. No forced sequence — an expert configuring an agent does not need step-by-step guidance, and a wizard would obstruct the common case of changing one field.

---

### Collection Configuration (per-agent collector enable/disable + frequency)

- **Primary user goal:** turn collectors on/off for an agent and set per-collector frequency (REQ-008, REQ-009).
- **Task frequency:** low.
- **Component choices:**
  - **Collector list with a toggle per collector** (enable/disable, REQ-008). Toggle chosen over checkbox because it reads as a live on/off state, which matches the operator's model ("is this collector running?").
  - **Per-collector frequency: inline editable field next to the toggle**, accepting a human-readable duration ("5m", "1h", REQ-005/009), defaulting to the agent-level interval with the default shown as placeholder so the operator sees what they'd inherit if they leave it blank.
  - Lives within the Agent Detail hub as a section/spoke.
- **Information density: moderate**, consistent with Family B.
- **Progressive disclosure:** all collectors listed (the operator wants the full set visible); the frequency override is a secondary field that defaults to inherited.

---

### Family C: Service detail / entity detail (non-device)

Screens: Service Detail (CA / DNS / DHCP).

- Mirrors Device Detail's structure (persistent identity header + sectioned body) but with service-type-specific sections (CA: issued certs; DNS: zones/records; DHCP: leases). Reusing the Device Detail layout pattern is deliberate peer-consistency: the operator learns one detail-page shape and applies it everywhere.
- Information density: dense within sections, consistent with Device Detail.

---

### Family D: Authentication

Screens: Login, first-run bootstrap (one-time console token, DEC-003).

- **Primary user goal:** get in.
- **Component choices:** single centered form (the one screen where a centered card is correct — there is no fleet data to be dense about). Server-side session auth (REQ-001), httpOnly cookie. First-run bootstrap consumes the console-printed one-time token to set the admin password (DEC-003), then redirects to login.
- **Information density: spacious** — deliberately the opposite of the rest of the app; this screen has exactly one job.

## Cross-Cutting Decisions

### Promotion workflow (Discovered → Managed)

The most consequential interaction in the discovery feature. Triggered from the Promote button on All Hosts or Device Detail (Admin only, REQ-036).

- **Process shape: short modal wizard (3 confirmations on one modal, not multi-page).** Steps per REQ-036: (1) select the agent that will manage the device, (2) supply or select a stored credential, (3) confirm the IP/hostname to connect to. These are few enough and tightly related enough to live on one modal with clearly sequenced fields, rather than a multi-page wizard that would feel heavyweight for three inputs. Rejected alternative: a full dedicated page — rejected because promotion is launched from a table row and the operator should return to that row's context on completion/cancel.
- **Confirmation:** the final step restates what will happen ("Agent X will begin SSH collection against host Y using credential Z"). This is the high-consequence confirmation; it earns an explicit confirm because it creates a RemoteTarget and starts live collection.
- **Undo / reversibility (complex-domain obligation — undo limitations acknowledged):** promotion is *not* freely reversible — managed → discovered is not a normal transition (REQ-030); it would require manually de-enrolling the agent (out of scope). BUT promotion is *non-destructive*: all passive facts are retained (REQ-035/036), so a failed or regretted promotion loses no historical data. The design relies on the pre-commit confirmation (prevention) rather than an undo, and on the automatic revert-to-discovered on connection failure (REQ-036) as the safety net. This limitation is stated to the operator at the confirm step.
- **Failure feedback:** if the agent can't reach the target, the host reverts to discovered and an Admin-area notification appears (REQ-036) — no email/webhook this iteration.

### Expert/Novice Strategy

**Both personas get the same dense, expert interface — no novice/simplified mode.** Justification (not a default): PERSONA-001 explicitly rejects consumer-grade simplification; PERSONA-002 (Read-Only Viewer) is described as "moderate" skill, "can interpret status indicators without deep protocol knowledge." The viewer's need is met not by a simpler interface but by (a) clear status indicators with text labels (not color/jargon alone) and (b) RBAC hiding the entire Admin surface they would never use (REQ-002). Building a second, simplified interface for an occasional secondary user would double the surface area for no requirement. The divergence between the two users is *access scope* (RBAC), not *interface complexity*.

Expert affordances (for the primary operator): URL-encoded filter state (shareable, bookmarkable scan views), keyboard-friendly tables (see below), dense layouts that show more without scrolling, and "show all sources"/"view all routes" power-user disclosures.

**Keyboard-first for high-frequency scanning (complex-domain obligation, considered now not deferred):** the daily-scan and investigation loops are keyboard-friendly by design — column-header sort and filter controls are standard focusable elements, table rows are navigable, and the global search/filter is reachable without the mouse. We are NOT building a full command palette this iteration (not required, and the operator's loops are dashboard-card-click driven), but the core scan path does not require a mouse. This is recorded so Phase 3 specifies focus order and keyboard nav rather than treating it as an afterthought.

### Error Recovery Model

- **Confirmation dialogs (reserved for destructive/high-consequence/irreversible actions only, to avoid alert fatigue — complex-domain obligation):**
  - Delete a credential that is referenced by targets → confirm, with the referencing targets listed (REQ-007).
  - Delete a credential/target/agent (any delete) → confirm.
  - Delete a discovered device → confirm (REQ-029 allows manual delete).
  - Promote a device → final-step confirm (creates a target, starts collection; REQ-036).
  - Everyday edits (rename a credential, change an interval, toggle a collector) → **no** confirmation; they are low-consequence and reversible by re-editing. Pervasive confirmations would train the operator to click through them blindly.
- **Undo:** not provided as a general mechanism. Reporting screens are read-only (nothing to undo). Admin edits are reversible by re-editing rather than by an undo stack. The genuinely irreversible-in-this-iteration action (promotion's de-enroll) is handled by prevention (confirm + non-destructive data model), per above.
- **Validation:** Admin forms validate inline where cheap (duration format "5m"/"1h", required fields, credential-type-appropriate secret fields) and at submit for server-side checks (e.g., credential reachability is NOT validated at form time — it's validated when the agent next polls/connects, and surfaced as a status). Partial saves are never silently committed (REQ-028) — a failed save shows the specific error and leaves the form populated so no input is lost.
- **Feedback model:**
  - **Transient success** (saved, deleted, promoted) → toast notification, auto-dismissing.
  - **Persistent state/staleness** → inline banner. If a reporting page's data is older than the staleness threshold, a "Data may be stale — last seen [timestamp]" banner shows (REQ-028). Offline agents surface within one refresh cycle (REQ-028).
  - **Per-section query failure** → inline error banner *on that section only*; other sections that loaded still render (REQ-028). A Device Detail page where the Disks query failed shows the Disks tab with an error and recovery (retry) while every other tab works.
  - **Whole-page/DB failure** → a clean error page, never a stack trace, never a raw DB error in the browser (REQ-028).
- **Stale-data honesty:** every entity screen carries a "last updated" timestamp (REQ-028, REQ-012). The operator must never mistake stale projection data for live truth — staleness is shown, not hidden.

### Interaction Pattern Consistency

These patterns are shared across screens; exceptions are documented with reasons:

- **Navigate:** role-aware left sidebar, grouped (see `information-arch.md`). Same on every screen.
- **Scan a fleet concern:** dense data table + toolbar filters + URL-encoded filter state + keyset pagination. Same across all Family A screens and All Hosts.
- **Drill to detail:** click a table row → entity detail page with persistent identity header + sectioned/tabbed body. Same for Device, Service.
- **Create (Admin):** modal form for self-contained records (targets, credentials). **Exception:** agent-level config is edited in a dedicated detail-page section with explicit Save (not a modal) because it's a coherent multi-field config committed as a set, not a standalone record — documented exception.
- **Edit (Admin):** same surface as create (modal for records, detail-section for agent config). Secrets are write-only (never pre-filled).
- **Delete (Admin):** confirmation dialog; if the item is referenced, the dialog lists referencing items.
- **Confirm:** reserved for destructive/high-consequence/irreversible actions only (see Error Recovery).
- **Empty state:** screens show a meaningful empty state with guidance. **Exception:** Device/Service Detail *hide* no-data sections entirely rather than showing an empty state (REQ-012) — documented exception, justified by the noise an empty Containers/Battery tab would create on every host.
- **Feedback:** toast for transient success, inline banner for persistent state/errors. Same everywhere.

## Open Assumptions for Validation

These were not confirmable without a live interview and should be validated before implementation:

1. **Device Detail uses tabs (vs. anchored long-scroll sections).** Assumed tabs based on the 17-section count and the operator's jump-to-category behavior. If the operator prefers a single scroll with a jump-nav, the structure is easily swapped — both satisfy REQ-012. Flagged for the project owner to confirm in mockup review.
2. **No command palette this iteration.** Assumed the dashboard-card-click scan loop is sufficient and a command palette is out of scope. If the operator wants keyboard-driven jump-to-screen, it's an additive Phase-N feature; the keyboard-first focus work done here does not preclude it.
