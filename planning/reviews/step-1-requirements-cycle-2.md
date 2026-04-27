---
agent: sdev-01b-requirements-critic
date: 2026-04-27
step: 1-requirements
cycle: 2
revision_id: 1
verdict: approved
two_pass_review: completed (forward + reverse)
---

# Review: Step 1 — Requirements (Cycle 2)

## Verdict: `approved`

All six cycle-1 blockers (B-1 through B-6) and the three stress-test items (S-1, S-2, S-4) are substantively resolved — not just checkbox-claimed. A handful of small issues were introduced by the revisions, but none rise to blocking. They're recorded as suggestions for the analyst to clean up opportunistically.

## Findings Summary

**Blocking:** none.

**Suggestions (non-blocking):**

1. **Cross-reference rot in tightened AC.** REQ-014 #2/#3, REQ-015 #3, REQ-017 #1/#3 reference *"live-indicator budget in quality-standards.md"* and *"minimum desktop target documented in quality-standards.md"*. The numbers (30s refresh, 1366×768) are correctly inlined in the AC, but those named budgets do not exist in `quality-standards.md`. Either add them to QS or strip the parenthetical "per quality-standards.md" claim.
2. **REQ-052 `trace_to: DEC-001` is semantically wrong.** DEC-001 is *distributed network discovery*; REQ-052 is *dashboard TLS posture*. Should be empty or trace to DEC-002.
3. **REQ-007 / REQ-008 / REQ-020 `trace_to: DEC-001`** — marginal. Auth/transport REQs traced to discovery decision; defensible but not obvious.
4. **`revision_id` field still reads `1`** on REQs materially edited in cycle 2. Worth verifying with orchestrator whether this is process bug or transparent CLI handling.
5. **REQ-049 filename** still ends `-wizard.md` although the requirement is now "Restore from SQLite backup (CLI + UI)." Slug is misleading.

## Per-Finding Verification

| Cycle 1 finding | Status | Evidence |
|---|---|---|
| B-1 BSD/Windows scope | ✅ Resolved | constraints.md drops BSD; Windows demoted to should-have via REQ-053. REQ-003 must-have surface = Linux x86_64 + Linux ARM + macOS only. |
| B-2 MVP-vs-should-have dependency | ✅ Resolved | REQ-014/15/16/17 each carry explicit "MVP Scope" sections. |
| B-3 Empty depends_on/trace_to | ✅ Resolved (depends_on); ⚠ partial (trace_to) | depends_on broadly populated; trace_to has two semantic mismatches (issues 2 and 3). |
| B-4 Ambiguous AC | ✅ Resolved | All flagged REQs tightened with concrete numbers/behaviors. |
| B-5 Backup verification | ✅ Resolved | REQ-049 promoted to should-have, CLI+UI shared code path, round-trip test AC. |
| B-6 Dashboard TLS | ✅ Resolved | REQ-052 added (must-have, MVP). HTTPS by default, self-signed first boot, HTTP opt-out only. |
| S-1 Agent footprint fallback | ✅ Resolved | REQ-002 #5/#6 — per-subsystem flags + heartbeat advertisement. |
| S-2 Device lifecycle | ✅ Resolved | REQ-015 dedicated "Device Lifecycle" section. |
| S-3 Notification flooding | ⚠ Acknowledged, not actioned | R-9 unchanged. Defensible — alerting UX is REQ-017's purview. |
| S-4 Auto-update trust root | ✅ Resolved | REQ-045 AC #3 now requires install-time embedded signing key. |

## Two-Pass Review

- **Forward pass:** index.md → constraints.md → risks.md → glossary.md → quality-standards.md → DEC-007 → spot-checked all REQs flagged as edited.
- **Reverse pass:** REQ-053 → REQ-001, then re-read constraints/stakeholders. Caught QS cross-reference rot, REQ-052 wrong trace, revision_id stamping question.

## New Stress Test

1. **Cert rotation.** R-13 names this; AC #6 surfaces 30-day expiry alert — adequate at requirements layer; architect must design rotation.
2. **Per-subsystem disable interactions.** REQ-002 disable + REQ-035 dedup losing an observer is acknowledged in R-3.
3. **Cert mid-rotation grace window.** R-13 mitigation mentions "dual-pin window" — adequate.
4. **AC reality check.** REQ-039 graph constraint, REQ-042 anchor rules, "always-unknown fails the test suite" — implementation-shaping in the right way.
5. **Downstream impact forecast.** Highest-friction for architect: REQ-052 (cert rotation), REQ-035 (per-observer reconciliation), REQ-045 (auto-update + rollback). All well-specified at the *what* level.

No new blocking findings.

## Recommendation

- Approve cycle 2.
- Suggestions optional cleanup.
- Proceed to Step 2 (UX Design).

```yaml
verdict: approved
blocking_findings: 0
suggestions: 5
proceed_to: step-2-ux-design
```
