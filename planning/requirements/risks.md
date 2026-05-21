---
agent: sdev-01a-requirements-analyst
date: 2026-04-27
status: draft
---

# Risks and Tensions

Tensions and trade-offs identified during requirements gathering. These are informational; they are not blockers. The Architect, UX Designer, and other downstream agents should weigh them when making their own decisions.

## R-1: Cross-platform agent surface (Linux x86_64 + Linux ARM + macOS must-have, Windows should-have)

Supporting Linux x86_64, Linux ARM, and macOS as must-have plus Windows as should-have multiplies the surface area for system-info collectors, network probes, and packaging. Each OS has quirks (raw ICMP permissions on Windows, macOS sandboxing, ARM-specific syscalls, etc.). FreeBSD/OpenBSD have been removed from scope (constraints.md) to bound this risk.

- **Mitigation direction:** factor collectors behind a small interface; ship a default no-op fallback per metric; require any collector to report 'unsupported' rather than crash on an unsupported platform. Windows-only failures must not block a release (REQ-053 AC #5).

## R-2: Pi resource pressure with 1-year hourly retention × 75 devices

Tiered retention saves space, but at scale (75 devices × dozens of metrics × 1 year hourly) the SQLite file can still grow. Combined with running on a Pi that may also be hosting other services, disk and memory pressure is a real risk.

- **Mitigation direction:** the Architect should size the schema and index strategy with this in mind; expose retention windows as configuration so Boss can tune them down on a constrained host.

## R-3: Distributed discovery quality depends on agent placement

Subnets without an agent are entirely invisible to the system. There is no central scanner to fall back on. Boss must remember to deploy at least one agent per subnet he cares about — and the dashboard should make it obvious when a subnet has no observers.

- **Mitigation direction:** surface 'subnets currently observed' in the dashboard; do not silently merge subnets that have lost their observer.

## R-4: Multi-observer dedup conflicts can produce surprising data

When two agents observe the same device with conflicting metadata (different hostnames, different mDNS service-types over time, MAC randomization on phones, etc.), the canonical record's "best-known" reconciliation may produce a value that surprises the user. Per-observer raw data must remain accessible for diagnostic purposes.

- **Mitigation direction:** keep raw observations forever (or near-forever within retention) in a per-observer table; expose them through the per-observer view.

## R-5: MAC randomization breaks the 'one MAC = one device' assumption

Modern phones (iOS, recent Android) randomize their MAC per network. The system's MAC-keyed deduplication may show the same physical phone as several short-lived devices, polluting the inventory.

- **Mitigation direction:** detect short-lived MACs (single observation, never seen again) and mark them as ephemeral; allow Boss to manually associate them or ignore them.

## R-6: Discovery scan frequency vs network noise

ARP scanning and active mDNS queries on the IoT VLAN can disturb fragile devices (cheap IoT firmware, Chromecast group dynamics). Default cadence is 15 minutes (gentle), but tuning is between Boss and his network — there is no universally safe value.

- **Mitigation direction:** make cadence configurable per-agent; document the trade-off in operator docs.

## R-7: First-time agent-server bootstrap is chicken-and-egg

For mTLS, the agent needs to trust the server's certificate before its first handshake. The pre-shared key + cert pinning on first connect is the standard answer, but it has gotchas: a stolen pre-shared key during the bootstrap window can register a hostile agent.

- **Mitigation direction:** this is the Architect's territory. The pre-shared key is a sensitive secret with a real attack window; the dashboard must surface its existence, allow rotation, and an alert on auto-approval is a useful detection signal (REQ-010).

## R-8: SQLite single-writer ceiling

If Boss's network grows beyond expectations or per-agent metric volume increases substantially, SQLite's single-writer model can become a bottleneck. The 25–75 device target is comfortable; 500 devices probably isn't.

- **Mitigation direction:** treat 'sustained heartbeat ingest backpressure' as the trigger to revisit the storage decision, not 'we crossed N devices'.

## R-9: Notification flooding during a network event

A site-wide event (power blip, ISP outage, switch reboot) can cause many devices to go offline simultaneously, producing a flood of notifications. Dedup helps per-alert; quiet hours help for nightly events. Neither catches a midday correlated outage.

- **Mitigation direction:** at minimum, the dashboard should surface 'N alerts firing in the last 5 minutes' so a flood is visible at a glance. A future optimization could correlate offline events into a single 'site event' notification, but that's not in MVP scope.

## R-10: Single admin = single point of failure for access

If Boss forgets the admin password and loses CLI access to the server host (e.g., the host is wedged), the only path back is wiping the data directory. This is acceptable for a personal project but worth being explicit about.

- **Mitigation direction:** document the recovery procedure clearly; remind users at bootstrap to save their credentials in their password manager.

## R-11: Auto-update mechanism is a high-value attack target

The auto-update flow lets the server push new binaries to every agent. Compromise of the server's binary store equals compromise of every host running an agent. Cryptographic signing is essential — and the trust root for that signing must be set at agent install time, not pulled from the server.

- **Mitigation direction:** Architect's call. At minimum, signature verification with a key pinned at agent install. The 'opt-out' control on auto-update is also important as a defense-in-depth measure.

## R-12: Strict dependency policy may slow delivery

The strict dep policy (DEC-006) is correct for a long-lived solo-maintained project, but it means areas like mDNS, OUI lookup, and chart rendering can't just `go get` a popular library — each must be evaluated. This will add some upfront work to architecture and planning.

- **Mitigation direction:** the Architect documents each accepted dep in a DEP record up front so this work happens once, not repeatedly.

## R-13: Dashboard TLS / certificate management lifecycle

REQ-052 generates a self-signed cert at first boot and shares it with the agent transport (REQ-020). This solves the bootstrap problem cleanly but introduces real lifecycle work: cert expiry, cert rotation (which forces every agent to re-pin), browser trust warnings on a self-signed cert, and the operator's option to swap in their own cert+key. There is no Let's Encrypt integration in scope (LAN-only deployment, no public DNS). If Boss neglects rotation, the dashboard goes down with an expired cert and every agent's pin must be refreshed simultaneously.

- **Mitigation direction:** REQ-052 AC #6 surfaces cert expiry as an alert (30-day warning); the Architect should design the rotation procedure so agent re-pinning is automated where possible (e.g., dual-pin window) rather than purely manual.

## R-14: Agent fleet upgrade coordination (no canary by default)

REQ-045 auto-update pushes a new agent binary to every opted-in agent on the next heartbeat. There is no canary mechanism (e.g., update 10% first, observe, then proceed). A buggy release can therefore propagate to the entire fleet within one heartbeat interval. The auto-rollback in REQ-045 AC #5 mitigates 'fails to start' failures, but a release that starts cleanly and then misbehaves (high CPU, bad data, slow leak) will affect every host before Boss notices.

- **Mitigation direction:** the per-agent opt-out (REQ-045 AC #4) is the manual canary mechanism — Boss can leave one or two agents on a previous version as production canaries. The Architect may consider a tag-based rollout in a later iteration; out of scope for MVP.
