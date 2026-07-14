-- Extends proj_discovered_nbns_names with the NetBIOS name-table fields NbnsScanner now
-- extracts alongside the raw name (RFC 1002 §4.2.18): the suffix byte, its well-known
-- description, owner node type, and the five NAME_FLAGS bits. Sibling columns on the
-- same (device, discovered, nbnsname) key already established for the "name" column.

ALTER TABLE proj_discovered_nbns_names
    ADD COLUMN suffix bigint,
    ADD COLUMN suffix_description text,
    ADD COLUMN owner_node_type text,
    ADD COLUMN is_group boolean,
    ADD COLUMN is_permanent boolean,
    ADD COLUMN is_active boolean,
    ADD COLUMN is_in_conflict boolean,
    ADD COLUMN is_being_deregistered boolean;
