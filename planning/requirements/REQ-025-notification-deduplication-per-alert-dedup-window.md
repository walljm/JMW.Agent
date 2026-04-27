---
id: REQ-025
title: Notification deduplication / per-alert dedup window
priority: should-have
category: usability
status: draft
depends_on: []
trace_to: []
---

## Description

The same alert condition does not fire repeated notifications during a configurable dedup window. While an alert remains in 'firing' state, only the first transition triggers a notification; the recovery transition triggers a single recovery notification.

## Acceptance Criteria

1. The dedup window is configurable globally (default: 1 hour).
2. Each alert tracks 'last notified at'; re-evaluating the same firing condition within the window does not produce another notification.
3. The dashboard surfaces the alert as still firing — dedup affects notification, not state.
4. After recovery, a subsequent re-fire produces a fresh notification (not suppressed by the prior dedup window).
