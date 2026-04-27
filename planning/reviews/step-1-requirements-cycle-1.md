---
agent: sdev-01b-requirements-critic
date: 2026-04-27
step: 1-requirements
cycle: 1
revision_id: 1
verdict: needs_revision
two_pass_review: completed (forward + reverse)
---

# Review: Step 1 — Requirements (Cycle 1)

## Summary

The package is unusually well-formed for a Cycle 1 submission: clean glossary, coherent MVP/Full-Vision split, sensible decisions, and acceptance criteria that are mostly testable. The producer did real work, not a checkbox pass.

That said, several issues require revision before downstream agents (UX, Architecture) can proceed without making material assumptions on Boss's behalf. The largest concerns are (1) scope feasibility from the "expand all options" pattern Boss applied, (2) MVP boundary contradictions where must-have features depend on should-have features, and (3) acceptance criteria that are ambiguous enough to permit shipping the wrong thing while passing all checks.

**Verdict: `needs_revision`** (none of the findings are existential — most are scoping clarifications and tightening of AC). Cycle 2 should resolve everything below.

## Two-Pass Review

- **Forward pass:** Reviewed `index.md`, `glossary.md`, `stakeholders.md`, `quality-standards.md`, `constraints.md`, all 6 DECs, all 51 REQs, `risks.md`, `PERSONA-001`. Drafted findings against checklist.
- **Reverse pass:** Re-read REQ-051 → REQ-001 (last to first), then constraints/stakeholders. Caught: REQ-014/REQ-016 MVP-vs-discovery dependency, empty `depends_on`/`trace_to` fields across all REQs (uniform omission missed in forward pass), and several "live/near-live" timing terms that were waved through individually but cluster into a consistency issue.
- **Merge:** Findings consolidated below.

## Blocking Findings

### B-1: FreeBSD/OpenBSD support is gold-plated against the stated persona

**Where:** REQ-003 (must-have), `constraints.md` "Agent platforms", R-1.

PERSONA-001 lists Boss's actual fleet: "Linux servers (x86 and Raspberry Pi), macOS workstations, IoT devices, printers, Google Home / Chromecast devices, switches, APs, and phones." **No BSD host is mentioned.** Yet REQ-003 makes BSD support a *must-have* and `constraints.md` says "All five are required."

Cost of BSD support is non-trivial: BSD-specific syscalls/sysctl for system info, BPF differences for ARP scanning, separate test/CI environment, separate release artifacts, ongoing maintenance burden against an OS Boss doesn't currently run. This is the textbook gold-plating anti-pattern.

**Required action:** Confirm with Boss whether (a) a BSD host actually exists or is imminent in his network, (b) BSD support is aspirational/portfolio-driven, or (c) it can be dropped. Then either demote BSD to `should-have`/`nice-to-have`, add a justification line to REQ-003, or remove BSD entirely. Same scrutiny applies less acutely to the Windows agent.

### B-2: MVP-must-have features depend on Full-Vision should-have features

**Where:** REQ-014, REQ-015, REQ-016 vs REQ-034–043.

- **REQ-014 #1** says the client list "shows every active registered agent **and every approved discovered device**." Discovered devices don't exist in MVP.
- **REQ-014** surfaces "device class" but auto-classification (REQ-042) is should-have.
- **REQ-015** description references Docker, listening services, reboot history, mDNS services, MAC vendor, and historical sparklines — all should-have.
- **REQ-016** applies tagging to "agent-backed or discovered" devices.

**Required action:** For each MVP REQ that references should-have functionality, add a "MVP behavior" clarifying note. Example for REQ-014: *"MVP scope: list shows registered agents only; `device class` shows `unknown` for all rows; discovered devices and auto-classification are introduced in REQ-034 / REQ-042 respectively."*

### B-3: Empty `depends_on` and `trace_to` fields across all 51 REQs

Every REQ frontmatter has `depends_on: []` and `trace_to: []`. Real dependencies need to be captured at minimum:

- REQ-008 → REQ-004, REQ-007
- REQ-009 → REQ-007
- REQ-010 → REQ-007, REQ-024
- REQ-013 → REQ-012
- REQ-018, REQ-051 → REQ-018
- REQ-021, REQ-027, REQ-028, REQ-029, REQ-030, REQ-038 → REQ-022
- REQ-035, REQ-039, REQ-040, REQ-041, REQ-042, REQ-043 → REQ-034
- REQ-049 → REQ-046, REQ-047
- REQ-024 → REQ-023
- REQ-014/15/17 → REQ-011, REQ-012, REQ-013

`trace_to` should reference DECs where applicable: REQ-005 → DEC-002, REQ-007/008/020 → DEC-001, REQ-011+ → DEC-005, REQ-002 → DEC-006.

### B-4: Acceptance criteria too ambiguous to drive correct implementation

- **REQ-039 (topology map):** A developer could ship a nested HTML list and satisfy every criterion. Either explicitly require a graph/spatial visualization or downgrade to "topology view."
- **REQ-042 (auto-classification):** AC #5 says "ambiguous classifications fall back to 'unknown'" — a spec that always returns 'unknown' satisfies all AC. Need positive-classification anchors.
- **REQ-035 (dedup):** "documented precedence" without defining where it lives or what it says. Either inline rules in AC or commit to authoring a precedence section.
- **REQ-011 #4:** "small fraction of the heartbeat interval" — define numerically.
- **REQ-015 #1, REQ-014 #2, REQ-017 #3, REQ-014 #5:** "reasonable time", "live (or near-live)", "without a full page reload" — inherit from quality-standards or restate the number.
- **REQ-017 #1:** "without scrolling on a typical desktop viewport" — define (e.g., 1366×768).
- **REQ-038 #4:** Restate as a requirement, not an example.
- **REQ-045 #5:** "prior binary is retained for rollback" — automatic rollback on N consecutive failed starts? Manual? Underspecified.

**Required action:** Replace "reasonable", "snappy", "near-live", "graceful" with numbers or concrete behaviors.

### B-5: REQ-049 (restore wizard) is `nice-to-have` — but no other REQ verifies that backups work

REQ-046/REQ-047 produce backup files; nothing in MVP or should-have proves any of them are restorable. If REQ-049 slips, JMW.Agent ships with a backup feature that has never been exercised end-to-end.

**Required action:** Either promote REQ-049 to should-have OR add a CLI restore subcommand requirement under should-have (faster path: `./jmw-server restore <file>` without a wizard UI).

### B-6: Dashboard TLS / certificate strategy is unspecified

REQ-005 #2 says session cookies are "Secure (when served over TLS)" — implying the dashboard *can* be served over plain HTTP. Agent transport requires TLS (REQ-020). But where does the dashboard's TLS certificate come from? Self-signed default? User-supplied? Auto-generated? Is plain HTTP an acceptable LAN configuration? How does agent cert pinning interact with dashboard cert?

**Required action:** Add a REQ (or extend REQ-005/REQ-020) covering dashboard TLS posture. Reasonable default: server generates a self-signed cert at first boot, dashboard refuses HTTP unless explicitly opted out, agent cert pin captures the same cert.

## Stress Test Findings

### S-1: Agent footprint has no fallback

If a Pi can't sustain "system metrics + subnet scanning + mDNS" simultaneously, what's the documented graceful degradation? Consider AC: agent advertises which subsystems it's running and supports per-agent disable of discovery/latency/SMART for resource-constrained hosts.

### S-2: Device deletion / "stop tracking this device" semantics

REQ-015 #5 mentions "deregister an agent, mark a discovered device as ignored" but no REQ defines:
- What happens to historical metrics on agent deregister?
- What does "ignored" mean — hidden from views, suppressed in alerts, or fully deleted?
- A discovered device observed once that disappears — does it stay forever or expire?

**Required action:** Add a REQ (or AC under REQ-015) covering device lifecycle.

### S-3: Notification-flood midday case acknowledged but not addressed

R-9 documents the risk; mitigation ("N alerts firing in last 5 min" surface) isn't an actual REQ or AC. Either add it to REQ-017 or accept the gap explicitly.

### S-4: Auto-update signing key trust root

REQ-045 AC #3 should require: "signature verification uses a key embedded at agent install time, not retrieved from the server." R-11 hands this to the architect — acceptable, but worth surfacing in AC.

## Suggestions (Non-blocking)

- **REQ-009** (auto-expiry of pending registrations, must-have): Borderline gold-plating for one operator. Consider demoting to should-have.
- **REQ-043** (firmware tracking via mDNS TXT) and **REQ-037** (rogue device detection): Verify Boss wants these at should-have priority vs nice-to-have.
- **Glossary addition:** Add "Cert pinning" since REQ-002/REQ-020 rely on it.
- **Risks gap:** Add risks for dashboard TLS/cert management (B-6) and agent fleet upgrade coordination (no canary).
- **REQ-051 export:** Specify max row count or time range to prevent runaway exports.
- **Missing DEC-007:** Go as the implementation language. Worth a DEC for traceability.

## Summary of Required Changes (Cycle 2)

1. **B-1:** Confirm/adjust BSD (and Windows) priority.
2. **B-2:** Carve out MVP behavior on REQ-014/15/16.
3. **B-3:** Populate `depends_on` and DEC `trace_to` fields across all 51 REQs.
4. **B-4:** Tighten ambiguous AC.
5. **B-5:** Promote REQ-049 (or add CLI restore equivalent) to should-have.
6. **B-6:** Add dashboard TLS / cert strategy requirement.
7. **S-2:** Define device lifecycle.
8. **S-4:** Pin auto-update trust root in REQ-045 AC.
9. Suggestions optional.

## Verdict

`needs_revision` — route to `sdev-01a-requirements-analyst` for cycle 2.
