-- Adds CIDR prefix-length columns to proj_interfaces.
--
-- Some collectors (Google Wifi/OnHub) must emit a bare IP for ipv4/ipv6 — that bare-IP
-- meaning is an exact-match join key elsewhere (DiscoveryMaterializer's MAC reconstruction
-- keys ARP-observed addresses against proj_interfaces.ipv4) — but the source data (the AP's
-- own `ip -s -d addr` output) does carry the CIDR prefix length before it gets stripped.
-- Without a place to keep it, an isolated interface with no peer already covering its subnet
-- (e.g. the guest network's br-guest, whose only agent-visible interface is the AP's own)
-- never gets a subnet synthesized on the Subnets page, since ListSubnetInterfaces.sql
-- requires ipv4/ipv6 to already contain a "/". SubnetsApi now falls back to synthesizing
-- "{ip}/{prefixLen}" from these columns when ipv4/ipv6 itself carries no "/".
ALTER TABLE jmwdiscovery.proj_interfaces
    ADD COLUMN IF NOT EXISTS ipv4_prefix_length integer,
    ADD COLUMN IF NOT EXISTS ipv6_prefix_length integer;
