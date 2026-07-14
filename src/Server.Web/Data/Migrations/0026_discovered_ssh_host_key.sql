-- SSH host-key fingerprint ("sha256:<base64>") on the discovered-observation row.
-- A host key is a stable per-host identity, so the DiscoveryMaterializer resolves it
-- as a FingerprintType.SshHostKey fingerprint — merging observations of the same host
-- seen by different observers (or at different IPs) into one device.
ALTER TABLE jmwdiscovery.proj_discovered
    ADD COLUMN if NOT EXISTS ssh_host_key text;
