-- Per-user UI preferences (first consumer: saved table column widths). A generic key/value store
-- keyed by user so preferences follow the user across browsers/devices, unlike localStorage.
-- pref_value is JSONB so each preference kind defines its own shape (e.g. column widths is an
-- object of column-index -> pixels). Rows are removed with the user (ON DELETE CASCADE).
CREATE TABLE if NOT EXISTS user_preferences (
    user_id
    UUID
    NOT
    NULL
    REFERENCES
    users
       (
    user_id
       ) ON
    DELETE
    CASCADE,
    pref_key
    TEXT
    NOT
    NULL,
    pref_value
    JSONB
    NOT
    NULL,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY
    KEY
       (
    user_id,
    pref_key
       )
);
