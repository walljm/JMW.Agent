-- ip_sort_key(ip): a fixed-width TEXT key whose lexical order matches NUMERIC IP order, so a
-- host list sorted by IP reads 192.168.1.9 < 192.168.1.10 < 192.168.1.100 (not the string order
-- 192.168.1.10 < 192.168.1.100 < 192.168.1.9). Returning text keeps the one uniform keyset cursor
-- shape — (sort_key, device_id) — that every sortable column uses.
--
--   ''            null / empty      → sorts first (a device with no identifying IP)
--   '4' + 10 dig  IPv4              → the 32-bit address value, zero-padded (0 .. 4294967295)
--   '6' + host    IPv6              → grouped after all IPv4 (rare on a LAN)
--   'z' + raw     unparseable       → sorts last
--
-- Pure function of its input → IMMUTABLE; the inet cast is guarded so a bad value can't abort a query.
CREATE
OR REPLACE FUNCTION ip_sort_key(ip text)
    RETURNS text
    LANGUAGE plpgsql
    IMMUTABLE
    PARALLEL SAFE
AS $$
DECLARE
a inet;
BEGIN
    IF
ip IS NULL OR ip = '' THEN
        RETURN '';
END IF;

BEGIN
        a
:= ip::inet;
EXCEPTION WHEN OTHERS THEN
        RETURN 'z' || ip; -- unparseable → sorts after every valid address
END;

    IF
family(a) = 4 THEN
        -- The IPv4 address as its 32-bit integer, zero-padded to 10 digits (max 4294967295)
        -- so lexical text order equals numeric address order.
        RETURN '4' || lpad((a - inet '0.0.0.0')::text, 10, '0');
END IF;

    -- IPv6: grouped after IPv4 via the '6' prefix; ordered by canonical text among itself.
RETURN '6' || host(a);
END;
$$;
