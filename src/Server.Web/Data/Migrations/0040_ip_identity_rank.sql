-- ip_identity_rank(ip): rank a candidate address for use as a device's IDENTIFYING IP.
--
-- Lower is better. The two jobs, both driven by the Boss's rule "never identify a device
-- by a loopback address":
--   99  non-identifying  — loopback (127.0.0.0/8, ::1), link-local (169.254/16, fe80::/10),
--                          the unspecified address (0.0.0.0, ::), or an unparseable value.
--                          Every host carries 127.0.0.1, so it can never disambiguate one.
--    0  private / LAN     — RFC1918 (10/8, 172.16/12, 192.168/16) and IPv6 ULA (fc00::/7).
--    1  public / global   — everything else (e.g. a router's WAN address).
--
-- Callers exclude non-identifying candidates with "ip_identity_rank(ip) < 99" and order by
-- the rank so a device's identity is its LAN address, never its loopback and never a public
-- address when a LAN address exists. Pure function of its input (no table access) → IMMUTABLE.
-- The inet cast is wrapped so a malformed value ranks 99 instead of aborting the query.
CREATE
OR REPLACE FUNCTION ip_identity_rank(ip text)
    RETURNS INT
    LANGUAGE plpgsql
    IMMUTABLE
    PARALLEL SAFE
AS $$
DECLARE
a inet;
BEGIN
BEGIN
        a
:= ip::inet;
EXCEPTION WHEN OTHERS THEN
        RETURN 99; -- unparseable → never an identity
END;

    IF
a <<= inet '127.0.0.0/8'         -- IPv4 loopback
        OR a = inet '::1'               -- IPv6 loopback
        OR a <<= inet '169.254.0.0/16'  -- IPv4 link-local
        OR a <<= inet 'fe80::/10'       -- IPv6 link-local
        OR a = inet '0.0.0.0'           -- IPv4 unspecified
        OR a = inet '::'                -- IPv6 unspecified
    THEN
        RETURN 99;
END IF;

    IF
a <<= inet '10.0.0.0/8'
        OR a <<= inet '172.16.0.0/12'
        OR a <<= inet '192.168.0.0/16'
        OR a <<= inet 'fc00::/7'        -- IPv6 unique local
    THEN
        RETURN 0; -- private / LAN — preferred
END IF;

RETURN 1; -- global / public
END;
$$;
