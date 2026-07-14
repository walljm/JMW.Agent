---
agent: sdev-ux
date: 2026-06-04
iteration: 2
revision_id: 10
status: draft
---

# Brand Brief — JMW Agent Facts Server

No external `branding/` artifacts exist; this brief is established from the Phase 2 design direction and the product's personas/requirements. It documents the strategic identity (the "why"); token values live in `design-system.css` and usage rules in `design-conventions.md`.

## Product Name

- **Canonical name:** **JMW Agent Facts Server** (full, formal — used in titles, documentation, the login/bootstrap screens).
- **Short name / wordmark:** **JMW Facts** (sidebar brand, page titles, conversational reference).
- **Never called:** "the dashboard" (it is more than the dashboard), "the portal," or "JMW Monitor." Not a marketing name — this is an internal operations tool and the name should read as utilitarian, not productized.

## Tagline / Positioning

**"Every host, every fact, one console."** — A self-hosted operations console that unifies what your agents actively collect with what your network passively reveals, so one operator can see and manage the whole fleet without SSHing into a single box.

The positioning differentiator is the **managed + discovered unification**: most tools show you either what you instrument or what you scan; this one treats both as one fleet at two fidelities.

## Brand Personality

- **Voice:** terse, precise, technical. Speaks to an expert peer, never down to a novice. Uses correct domain vocabulary (ARP, OUI, LLDP, SMART, mDNS) without apology or glossing.
- **Tone:** calm and factual under normal conditions; direct and unambiguous when something is wrong ("3 offline," "SMART: failing") — never alarmist, never cute. No exclamation marks in status copy, no emoji, no encouragement ("Great job!"). The tool reports; it does not cheerlead.
- **Honesty as a brand value:** the product always tells the operator how fresh and how trustworthy a fact is. Staleness, partial profiles, and provenance are surfaced, never hidden behind a confident-looking number. This is the most important personality trait and it is load-bearing for the discovered-device feature.

## Color Rationale

A **near-black surface with a green phosphor cast** evokes the terminal the operator already lives in — the tool feels native to a NOC/SSH workflow rather than like a consumer SaaS dashboard. Green is the resting/healthy signal *and* the brand accent, so a calm fleet literally looks calm. The semantic signal palette (amber warn, coral-red critical, cyan info) is reserved strictly for status meaning — color is never decorative, so when something turns amber or red it reads instantly as "attention here." A thick left status rail lets a problem row/card "light up" down the edge in a dim room. (Token values in `design-system.css`.)

## Typography Rationale

**Monospace as the primary typeface** (IBM Plex Mono) is both a brand signature and a functional choice: the product's data is overwhelmingly fixed-width identifiers — IP addresses, MAC addresses, UUIDs, ports, fact attribute paths like `Device[router-1].Interface[eth0].Speed`. A monospace face aligns these in columns and makes them scannable and comparable in a way a proportional face cannot. It also reinforces the terminal-native identity. A proportional sans (IBM Plex Sans) is used sparingly for longer prose/help text where readability of running sentences matters more than column alignment. (Token values in `design-system.css`.)

## Logo / Mark

No dedicated logo exists. The current mark is a **typographic wordmark** ("JMW Facts" in the accent phosphor green) with a small block glyph. If a richer mark is wanted later, the standalone Artist agent can be invoked; it is not required for this iteration.

## Naming Conventions (brand voice)

Complements `glossary.md` (domain terms). Brand-voice rules for how features/sections/actions are named in the UI:

- Use the operator's word, verbatim, from the glossary — never a friendlier synonym. "Discovered," not "Seen." "Promote to Managed," not "Adopt" or "Add." "Stale," not "Out of date."
- Sections are named for the thing they contain ("Open Ports," "Storage," "Change Feed"), not for an abstraction ("Network Insights," "Health Center").
- Actions are imperative verbs that state exactly what happens ("Promote," "Rotate," "Save," "Delete"), with the consequence spelled out at the confirmation step for high-stakes actions.
- Group headers (INVENTORY, POSTURE, NETWORK, AUDIT, ADMIN) are short scan anchors, uppercase, organizational — not domain claims.

## What Makes It Memorable

The **terminal-native, monospace, phosphor-on-black console** is the signature — it looks unmistakably like an operator's tool, not a generic dark SaaS dashboard. The second memorable element is the **All Hosts view**: one table where solid-badge managed hosts and dashed-outline discovered hosts coexist, each discovered host wearing its discovery-source tags like a confidence fingerprint. That unification — and the honesty about how much is known about each host — is the experience that distinguishes this product.
