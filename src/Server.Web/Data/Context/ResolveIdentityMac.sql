-- Context derivation "identity-mac" (docs/plans/context-derivations.md §4): the newest real MAC
-- fingerprint per device. This value lives ONLY in the device_fingerprints registry (never in
-- facts_history), which is why it needs a context derivation rather than AnalysisEngine. Set
-- form of DeviceListApi's former dmac lateral: newest 'mac' row per device by last_seen.
-- fp_value is canonical bare 12-hex lowercase already; lower() guards legacy rows.
SELECT DISTINCT ON (df.device_id)
    df.device_id::text AS device
  , lower(df.fp_value) AS value
FROM
    device_fingerprints df
WHERE
    df.fp_type = 'mac'
ORDER BY
    df.device_id
  , df.last_seen DESC
