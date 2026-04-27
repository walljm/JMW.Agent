---
id: REQ-024
title: Notification channels: email, Discord, Pushover, Gotify
priority: should-have
category: integration
status: draft
depends_on: REQ-023
trace_to: []
revision_id: 1
---

## Description

The admin can configure outbound notification channels. Supported channels for MVP: SMTP email, Discord webhook, Pushover, Gotify. Each alert can route to any subset of configured channels.

## Acceptance Criteria

1. Each channel type can be configured from the dashboard with the credentials/URLs it requires.
2. Channels can be tested via a 'Send test message' button before being relied on.
3. Channels can be enabled, disabled, or deleted at any time.
4. Per-channel routing rules: an alert can be set to fire on a subset of channels (e.g., 'critical → all channels; informational → Discord only').
5. Failed notification attempts are logged with the failure reason and retried with backoff up to a bounded number of attempts.
