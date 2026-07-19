-- MAC-keyed targets: a target can address a device by MAC instead of a fixed IP, so collection
-- keeps working when the device's DHCP lease moves (the failure mode that stranded the Google
-- Wifi target on a stale .223 address). endpoint_kind discriminates how `endpoint` is read:
--   'address' — the host/IP/URL, used as-is (today's behavior; the default for existing rows)
--   'mac'     — a canonical bare 12-hex lowercase MAC the server resolves to the device's
--               current IP at config-assembly time (see GetIpForMac.sql / AgentConfigAssembler)
-- Overloading `endpoint` (rather than adding a nullable column) keeps every read path and the
-- agent contract unchanged: the agent still receives a concrete, ready-to-use address.
ALTER TABLE targets
    ADD COLUMN IF NOT EXISTS endpoint_kind TEXT NOT NULL DEFAULT 'address';

ALTER TABLE targets
    ADD CONSTRAINT targets_endpoint_kind_chk CHECK (endpoint_kind IN ('address', 'mac'));

-- A mac-kind endpoint must be canonical bare 12-hex lowercase (the form MacFormat.ToBareHex
-- produces and the projection tables store), so the resolution joins line up exactly.
ALTER TABLE targets
    ADD CONSTRAINT targets_mac_endpoint_chk
        CHECK (endpoint_kind <> 'mac' OR endpoint ~ '^[0-9a-f]{12}$');
