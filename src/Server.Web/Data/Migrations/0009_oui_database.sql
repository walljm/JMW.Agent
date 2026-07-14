-- OUI database tables
-- oui_entries: one row per prefix, keyed by hex string (lowercase, no separators)
-- oui_meta: single-row table tracking the last download timestamp and record count

CREATE TABLE oui_entries (
    prefix text NOT NULL,
    bits   INT  NOT NULL,
    vendor text NOT NULL,
    PRIMARY KEY (prefix, bits)
                         );

CREATE TABLE oui_meta (
    id           INT         NOT NULL DEFAULT 1 CHECK (id = 1),
    updated_at   timestamptz NOT NULL,
    record_count INT         NOT NULL,
    version_hash text        NOT NULL,
    PRIMARY KEY (id)
                      );
