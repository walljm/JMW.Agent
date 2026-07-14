-- proj_dns_records: add record type to the key and a CNAME target column.
--
-- Previously keyed by (service, zone, record) with a single ip column, which
-- meant A and AAAA for the same hostname collided and CNAME had nowhere to land.
-- Record type becomes part of the primary key; A/AAAA keep using `ip`, CNAME
-- uses the new `target` column.
--
-- Existing rows are all A records (the collector only kept A), so backfilling
-- rtype = 'A' preserves them. Projections are derived state and repopulate from
-- the fact store on the next collection cycle regardless.

ALTER TABLE proj_dns_records
    ADD COLUMN if NOT EXISTS rtype text NOT NULL DEFAULT 'A';

ALTER TABLE proj_dns_records
    ADD COLUMN if NOT EXISTS target text;

ALTER TABLE proj_dns_records
DROP
CONSTRAINT IF EXISTS proj_dns_records_pkey;

ALTER TABLE proj_dns_records
    ADD PRIMARY KEY (service, zone, record, rtype);

-- Drop the default now that existing rows are backfilled; new rows always
-- supply an explicit rtype from the projection writer.
ALTER TABLE proj_dns_records
    ALTER COLUMN rtype DROP DEFAULT;
