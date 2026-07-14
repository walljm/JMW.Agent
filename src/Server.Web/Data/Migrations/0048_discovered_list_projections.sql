-- List sub-dimensions for discovered-neighbor scanner signals that are genuinely multi-valued
-- (a real list dimension keyed by the item, mirroring proj_discovered_services). These replace the
-- former comma-joined Attr[] facts: NetBIOS names (NbnsScanner) and CoAP resources / advertised
-- content-formats (CoApScanner). Scalar scanner signals live on device-detail fact views, not here.

CREATE TABLE IF NOT EXISTS jmwdiscovery.proj_discovered_nbns_names (
    device      TEXT NOT NULL,
    discovered  TEXT NOT NULL,
    nbnsname    TEXT NOT NULL,
    name        TEXT,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, discovered, nbnsname)
);

CREATE TABLE IF NOT EXISTS jmwdiscovery.proj_discovered_coap_resources (
    device        TEXT NOT NULL,
    discovered    TEXT NOT NULL,
    coapresource  TEXT NOT NULL,
    path          TEXT,
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, discovered, coapresource)
);

CREATE TABLE IF NOT EXISTS jmwdiscovery.proj_discovered_coap_formats (
    device             TEXT NOT NULL,
    discovered         TEXT NOT NULL,
    coapcontentformat  TEXT NOT NULL,
    id                 TEXT,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, discovered, coapcontentformat)
);
