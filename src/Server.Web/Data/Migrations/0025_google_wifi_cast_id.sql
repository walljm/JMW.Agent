-- Stable Google Cast device id (from the _googlecast mDNS advertisement) on the
-- discovered-observation row. The DiscoveryMaterializer anchors mDNS identity to
-- this id (FingerprintType.CastId) rather than the IP, so a stale advertisement on
-- a reused IP cannot smear the cast device's name onto the new occupant.
ALTER TABLE jmwdiscovery.proj_discovered
    ADD COLUMN if NOT EXISTS cast_id text;
