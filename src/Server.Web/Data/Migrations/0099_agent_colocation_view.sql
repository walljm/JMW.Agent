-- agent_colocation: ordered pairs (agent, other_agent) of agents proven to share ONE L2 broadcast
-- domain -- i.e. the same LAN. Two agents are co-located when they have both observed the same
-- globally-administered unicast MAC in ARP. A globally-administered MAC is unique worldwide, so a
-- MAC seen by two agents is site-unique evidence that they share a segment. That is exactly what
-- distinguishes "same LAN" from "the same RFC1918 subnet numerals reused at two physical sites" --
-- subnet equality cannot tell those apart, a shared real MAC can.
--
-- This is the co-location proof that lets an agent-scoped IP->MAC lookup (GetKnownMacsForIp /
-- GetKnownMacsWithVendorForIp) safely consider MACs captured by a *different* agent on the same
-- LAN -- the case that strands Google Wifi reconstruction when obscured-MAC polling and ARP
-- collection run on separate co-located agents (the Google Wifi endpoint itself never emits a real
-- MAC, so it can never anchor co-location; the polling agent does).
--
-- Threshold: the default gateway's MAC alone is a strong single witness, but requiring >= 3 shared
-- MACs guards against a lone stale / relayed / roamed sighting pairing two unrelated segments.
-- Symmetric: both (a,b) and (b,a) are emitted so a lookup keyed on either agent finds its partners.
--
-- Only globally-administered unicast MACs count. The I/G (multicast, 0x01) and U/L (locally-
-- administered / randomized, 0x02) bits live in the low nibble of the first octet (the 2nd hex
-- digit); either bit set means the MAC is not a stable, globally-unique device identity and so is
-- not admissible co-location evidence. MACs are stored canonical bare 12-hex lowercase.
CREATE OR REPLACE VIEW agent_colocation AS
SELECT
    x.agent_id            AS agent
  , y.agent_id            AS other_agent
  , count(DISTINCT x.mac) AS shared_macs
FROM
    proj_device_arp x
    JOIN proj_device_arp y ON y.mac = x.mac AND y.agent_id <> x.agent_id
WHERE
      x.agent_id IS NOT NULL
  AND y.agent_id IS NOT NULL
  AND x.mac ~ '^[0-9a-f]{12}$'
  AND (('x' || substr(x.mac, 2, 1))::bit(4) & B'0011') = B'0000'
GROUP BY
    x.agent_id, y.agent_id
HAVING
    count(DISTINCT x.mac) >= 3;
