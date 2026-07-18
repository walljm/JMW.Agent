-- Persist the endpoint IP the agent connected to when polling a service (Service[].Address).
-- The collector resolves the endpoint host to an IP at connection time; storing it lets the
-- server link a remotely-polled service to its host device (endpoint IP -> device) and keeps the
-- address queryable/displayable. Fill-only, nullable.
ALTER TABLE proj_services
    ADD COLUMN IF NOT EXISTS address TEXT;
