-- proj_security was write-only (read by no query). Security posture now surfaces as a
-- device-detail "Security" fact view (firewall / AV / secure boot / TPM / SELinux / AppArmor /
-- SIP / Gatekeeper / Defender) plus an "Encrypted Volumes" view, rendered from facts_history —
-- which also surfaces the AppArmor/SIP/Gatekeeper/Defender/EncryptedVolume facts that were
-- never even projected.
DROP TABLE if EXISTS jmwdiscovery.proj_security;
DELETE
FROM
    jmwdiscovery.retention_policies
WHERE
    table_name = 'proj_security';
