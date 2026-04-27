-- 0006: per-device mDNS profile (services + TXT) as JSON blob
ALTER TABLE devices ADD COLUMN services_json TEXT;
