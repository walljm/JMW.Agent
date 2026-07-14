-- Reinstates proj_device_certs, dropped in 0032 as write-only. It now has a real reader: the
-- Certificate Authorities terrain page (/terrain/ca) groups is_ca=true rows by fingerprint to
-- list every CA cert trusted somewhere in the fleet, and uses is_ca=false rows' issuer_dn to
-- infer CAs we've only ever seen sign a leaf cert. Same schema as the original table.
-- Key = SHA-256 fingerprint (lowercase hex, no colons). Deduplicates identical certs across
-- multiple paths on the same device.
CREATE TABLE if NOT EXISTS proj_device_certs (
    device TEXT NOT NULL,
    cert TEXT NOT NULL, -- SHA-256 fingerprint (lowercase hex)
    subject_dn TEXT,
    issuer_dn TEXT,
    not_before TIMESTAMPTZ,
    not_after TIMESTAMPTZ,
    path TEXT,
    is_ca BOOLEAN,
    sans TEXT, -- comma-joined DNS SANs (up to 10)
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, cert)
);

-- Expiry monitoring: "certs expiring in the next 30 days."
CREATE INDEX if NOT EXISTS proj_device_certs_not_after_idx
    ON proj_device_certs (not_after)
    WHERE not_after IS NOT NULL;

-- CA attribution: "which devices hold a cert issued by CA X?"
CREATE INDEX if NOT EXISTS proj_device_certs_issuer_idx
    ON proj_device_certs (issuer_dn)
    WHERE issuer_dn IS NOT NULL;

-- New: TLS issuer/subject observed on a discovered neighbor's presented certificate
-- (TlsCertScanner, via NetworkDiscoveryCollector's Discovered[].Tls* facts). This is the
-- "observed-in-traffic" CA signal on the terrain page — we've seen something signed by this
-- issuer DN, but never captured the CA's own certificate.
-- Key dims match proj_discovered (device, discovered=IP).
CREATE TABLE if NOT EXISTS proj_discovered_tls (
    device TEXT NOT NULL,
    discovered TEXT NOT NULL,
    tls_subject TEXT,
    tls_issuer TEXT,
    tls_serial TEXT,
    tls_not_after TIMESTAMPTZ,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, discovered)
);

CREATE INDEX if NOT EXISTS proj_discovered_tls_issuer_idx
    ON proj_discovered_tls (tls_issuer)
    WHERE tls_issuer IS NOT NULL;

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'proj_device_certs'
    , 'stable'
    , INTERVAL '90 days'
    , 'Also consider pruning by not_after < now() - interval ''30 days'' for long-expired certs'
)
     ,
(
    'proj_discovered_tls'
    , 'network'
    , INTERVAL '7 days'
    , 'Passive TLS probe results; neighbors may leave the network without notice'
)
ON CONFLICT (table_name) DO NOTHING;
