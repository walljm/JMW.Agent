---
id: REQ-053
title: Windows agent support
priority: should-have
category: deployment
status: draft
depends_on: REQ-002
trace_to: []
revision_id: 1
---

## Description

A Windows agent binary is produced and supported, but at lower priority than Linux/macOS. Boss uses Windows infrequently (see PERSONA-001) and is not a primary fleet member, so this requirement is should-have rather than must-have. The Windows agent is expected to share the same codebase and feature set as the Linux/macOS agent except where platform constraints (e.g., raw ICMP requiring admin) force degradation.

## Acceptance Criteria

1. A Windows x86_64 agent binary is produced as part of the release process.
2. The agent installs and runs as a non-privileged user by default; operations that genuinely require admin (e.g., raw ICMP) degrade gracefully (TCP connect probe fallback) rather than crashing.
3. The agent is verified to start, register, and report basic system info on a current Windows release.
4. Privileged operations that have no acceptable fallback are documented as 'unsupported on Windows' in the OS/arch matrix rather than silently producing wrong values.
5. Failure of Windows-specific behavior does not block a release if Linux and macOS agents pass.
