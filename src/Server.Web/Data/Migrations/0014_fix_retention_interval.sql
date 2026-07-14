-- Rewrite the audit_log retention interval from '2 years' (months-based, incompatible
-- with .NET TimeSpan) to '730 days' (days-only, safe for TimeSpan casting).
UPDATE retention_policies
SET
    stale_after = INTERVAL '730 days'
WHERE
    TABLE_NAME = 'audit_log'
  AND stale_after = INTERVAL '2 years';
