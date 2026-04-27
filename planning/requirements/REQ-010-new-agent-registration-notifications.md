---
id: REQ-010
title: New-agent registration notifications
priority: should-have
category: operational
status: draft
depends_on: [REQ-007, REQ-024]
trace_to: []
revision_id: 1
---

## Description

When a new agent registers and is awaiting approval (or is auto-approved via pre-shared key), the admin is notified via the configured notification channels. This ensures Boss is aware of unexpected registrations even when not actively watching the dashboard.

## Acceptance Criteria

1. A pending registration triggers a notification through every enabled notification channel.
2. Auto-approved registrations also trigger a notification (lower severity / informational).
3. The notification includes hostname, source IP, and a link or path to the dashboard for review.
4. The admin can disable new-agent notifications independently of other alert channels.
