-- D19 consolidation: "NOT EXISTS (SELECT 1 FROM device_fingerprints WHERE fp_type = 'mac' AND
-- fp_value = <mac>)" — the "is this MAC already fingerprinted" anti-join used to decide whether a
-- passively-observed MAC (ARP/DHCP/scanner) represents a new, not-yet-identified device — was
-- copy-pasted across GetNewDiscoveredMacs, GetNewDhcpMacs, GetNewDhcpLocalMacs, and GetNewArpMacs.
-- One view, one definition of "fingerprinted as a MAC".
CREATE
OR REPLACE VIEW fingerprinted_macs AS
SELECT fp_value AS mac
FROM device_fingerprints
WHERE fp_type = 'mac';
