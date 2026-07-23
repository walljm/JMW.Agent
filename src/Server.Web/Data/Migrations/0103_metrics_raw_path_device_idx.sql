-- 0071_metrics_raw.sql deliberately shipped with no path/key index — "nothing reads metrics_raw
-- as a time series" was true until the Device Detail interface-throughput sparkline: one device's
-- Interface[].TotalBytes history, picked out of every device's metric rows. Without this index
-- that's a sequential scan of every retained partition (whatever MetricRetention:StaleAfter
-- currently keeps, ~3 days by default) on every Device Detail page view.
--
-- Partitioned table: CREATE INDEX on the parent propagates to every existing partition AND to
-- every partition MetricPartitionService creates afterward — no per-partition maintenance needed.
-- collected_at trails so an index scan already returns rows in the order the sparkline wants,
-- no separate sort step.
CREATE INDEX IF NOT EXISTS metrics_raw_path_device_time_idx
    ON metrics_raw (attribute_path, (key_values ->> 'Device'), collected_at);
