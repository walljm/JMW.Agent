---
id: DEC-007
title: Go as the implementation language
date: 2026-04-27
status: draft
revision_id: 1
---

## Decision

JMW.Agent is implemented in Go (Golang) for both the server and the agent.

## Context

This project is a deliberate rewrite of an earlier .NET prototype. The new implementation needs:

- Single self-contained binaries on Linux x86_64, Linux ARM, macOS, and (lower priority) Windows — see REQ-001, REQ-002, REQ-003, REQ-053.
- No runtime dependency (no JVM, no Node, no .NET runtime install).
- Strong cross-compilation support (Boss builds for ARM from non-ARM hosts).
- A reasonable standard library for HTTP, TLS, SQLite (via cgo or pure-Go driver), structured logging, and concurrency — keeping the dependency surface small (DEC-006).

## Decision Drivers

- **Single-binary delivery:** Go builds static binaries trivially via `go build` with no runtime; this is the central deployment constraint (REQ-001, REQ-002).
- **Cross-compilation:** `GOOS`/`GOARCH` cross-compile is first-class. Building Linux/ARM and Windows binaries from a Mac/Linux dev box is a one-line operation.
- **Stdlib coverage:** net/http, crypto/tls, encoding/json, database/sql, log/slog cover the bulk of the project — aligned with the strict-deps policy in DEC-006.
- **Operational characteristics:** modest memory footprint, predictable garbage collector, suits a long-lived agent on a Raspberry Pi.
- **Boss familiarity:** Boss is comfortable with Go; this is a solo-maintained project, so language fluency matters.

## Alternatives Considered

- **Rust:** Stronger memory and concurrency guarantees, smaller binaries, no GC. Rejected for this project on solo-maintenance pragmatics: longer iteration time, async ecosystem fragmentation, steeper time-to-debug for occasional touches. Worth revisiting if specific subsystems (e.g., ICMP probes) prove resource-critical.
- **.NET (current prototype language):** Rejected because the rewrite premise is precisely to escape the runtime dependency and improve cross-platform packaging.
- **Python / Node:** Rejected on single-binary requirement and Pi resource budget.

## Consequences

- All REQs that mention "binary" assume `go build` output. The toolchain is Go (latest stable).
- The strict-deps policy (DEC-006) inherits Go module ecosystem norms; non-stdlib deps are evaluated case by case via DEP records.
- Go-specific quality gates (gofmt, go vet, staticcheck, govulncheck) appear in quality-standards.md.
