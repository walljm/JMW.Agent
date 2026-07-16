---
date: 2026-07-16
status: proposed
mode: standalone
---

# L3 Topology Graph — Host-Local Subnet Correctness + Enrichment

> **Status:** PROPOSED — design capture only, no code until Boss approves.
>
> **Origin (Boss, 2026-07-16):** captured across a working discussion, in priority order:
> 1. The L3 diagram must **not** draw one shared subnet node linking two devices when those
>    subnets are device-local and non-routable (Docker's `172.17.0.0/16` bridge exists on every
>    host but routes nowhere between them). Showing a common subnet there is misleading.
> 2. "Show what's on the other side" — attach a **VPN cloud** to a Tailscale subnet and an
>    **Internet cloud** to a WAN uplink, so the graph reveals which devices have VPN/WAN egress.
> 3. Label the **device↔subnet edges with the interface** (`eth0`, `docker0`, `tailscale0`).
> 4. Use **real icons** (router / host / cloud) instead of plain boxes.
>
> **Companion:** AGENTS.md "Topology graphs (Subnets page)". The prior renderer decision doc
> (`docs/plans/d3-l2-l3.md`) was deleted post-ship; its decisions live in
> `src/Server.Web/wwwroot/js/topology-graph.js`'s header comment.
>
> Every claim below about current behavior was verified against primary source this session
> (references inline). Tracks are independent and stack on the existing renderer + graph API.

---

## 1. Problem

The L3 graph keys every subnet purely by its CIDR string, so identical CIDRs on different hosts
**merge into one node**. Container/VM bridges make this common and wrong:

- Docker's default `bridge` is always `172.17.0.0/16` on **every** host; user-defined bridges take
  `172.18+`. These are host-local NAT — not routable between hosts.
- Two hosts each running Docker therefore both attach to a single shared `172.17.0.0/16` node, and
  the "device spans ≥2 subnets ⇒ router" heuristic then makes those hosts appear **interconnected
  through Docker's internal network** — a topology that does not exist.

The same graph is also thinner than the data allows: it shows subnets and routers but not what a
subnet egresses to (VPN / WAN), nor which interface attaches a host to a subnet.

## 2. Current behavior (verified)

`src/Server.Web/Api/Reporting/SubnetsApi.cs`:

- `BuildAggregatesAsync` aggregates subnets from `proj_interfaces` (agent CIDRs), `proj_dhcp_scopes`,
  `proj_device_routes`, and default routes. `GetOrAdd` keys the dictionary on **`network.ToString()`
  alone** — this is the merge point.
- `GetGraphAsync` emits nodes `(id, label, kind)` and edges `(FromId, ToId)`. Node kinds today:
  `subnet`, `router`, `internet`.
- **An `internet` node already exists:** `CreateInternetNode` fires when a device's default-route
  gateway resolves outside every known subnet (the one thing route data uniquely tells us). This is
  the precedent Track 2 extends.
- `SubnetInterface(Device, Hostname?, InterfaceName?, Ip)` already carries the interface name per
  (device, subnet) — the data Track 3 needs is already aggregated, just not emitted on edges.

`src/Server.Web/wwwroot/js/topology-graph.js` (shared L2/L3 renderer):

- `KIND_STYLE` maps each kind → `{ shape, varName }` (a CSS color var); unknown kinds fall back to a
  neutral circle, so a new kind never renders invisibly.
- Each node is a `<g>` group containing a `<rect>`/`<circle>` + `<text>` label + `<title>` tooltip —
  an icon is a one-line append to that group.
- The edge data shape **already anticipates labels**: the usage comment declares
  `edges: [{fromId, toId, fromPort, toPort, via}]` (the L2 tab carries port/via info). L3's
  `SubnetGraphEdge` is only `(FromId, ToId)` today.
- **CSP constraint:** the page runs under `script-src 'self' 'unsafe-inline'` with **no
  `'unsafe-eval'`** and no CDN (AGENTS.md). Vendor everything locally; no D3 plugin that uses
  `eval`/`new Function()`.

---

## 3. Track 1 — Host-local subnet keying (the correctness fix)

### 3.1 What determines "routable" (do not over-simplify)

Routability is a per-host property of the interface/network, not a property of the CIDR:

| Docker network | Routable? | Notes |
|---|---|---|
| `bridge` driver | **No** — host-local NAT | The `172.17/172.18…` case. Merge is wrong. |
| `macvlan` / `ipvlan` | **Yes** — real LAN IPs | "All Docker = local" would be **wrong** here. |
| `overlay` (swarm) | Yes — across the swarm | Host-local to nothing. |
| `host` | n/a | No separate subnet. |

So the decision needs the **driver/scope**, not the CIDR range. (A range guess like "all of
`172.16.0.0/12` is Docker" is the weakest signal — a real LAN can legitimately use `172.20.0.0/24`.)

### 3.2 Data gap

| Signal | Status | Useful for classification? |
|---|---|---|
| Interface **name** (`docker0`, `br-<hash>`, `veth*`, `virbr*`) — `proj_interfaces.name` | ✅ collected | Heuristic only — `br-<hash>` is indistinguishable from a legit Linux LAN bridge |
| Interface **type** — `FactPaths.InterfaceType` | ✅ collected | **No.** It is .NET's `NetworkInterfaceType` (`NetworkCollector.cs` emits `nic.NetworkInterfaceType.ToString()`), which reports `docker0` as plain `Ethernet`. It will not flag a virtual bridge. |
| Docker **network** enumeration (driver, scope, subnet, bridge name) | ❌ **not collected** | Authoritative. `DockerCollector.cs` calls only `/info` and `/containers/json`, never `/networks`. |

`GET /v1.43/networks` returns per network: `Driver`, `Scope` (`local`/`swarm`),
`IPAM.Config[].Subnet`, and `Options["com.docker.network.bridge.name"]` (ties the CIDR back to the
`docker0`/`br-<hash>` interface).

### 3.3 Detection approach

1. **Authoritative (recommended):** add Docker `/networks` collection → new fact paths for
   `Subnet`, `Driver`, `Scope`, and bridge name; project them. A CIDR reported with `driver=bridge,
   scope=local` is definitively host-local; `macvlan`/`ipvlan`/`overlay` are **not** flagged local.
2. **Fallback heuristic** (hosts where the Docker API is unreachable, or non-Docker virtual bridges):
   interface-name match — `docker0`, `br-*`, `veth*`, `virbr*`, `vboxnet*`, `cni*`, plus link-local
   `169.254.0.0/16`.

**Recommendation:** do the authoritative collection. The name heuristic alone both false-positives
(a real `br-` LAN bridge) and false-negatives (macvlan that *is* routable); `driver`/`scope` is the
only thing that separates macvlan from bridge.

### 3.4 Keying change

In `BuildAggregatesAsync`, a subnet flagged host-local keys on **`device + CIDR`** instead of CIDR
alone, so each host's `172.17.0.0/16` becomes its own node attached only to its owning host, labeled
with the host. Real/routable subnets keep global CIDR keying (unchanged). This also stops the
"spans ≥2 subnets ⇒ router" logic from chaining hosts through their private Docker networks.

---

## 4. Track 2 — Off-subnet "cloud" nodes

Value: reveal VPN/WAN egress at a glance.

- **Internet / WAN cloud — partly built.** `CreateInternetNode` + the `internet` kind already exist;
  an OnHub `wan0` with a default route already triggers it. Refinement: make the attachment explicit
  per-device rather than one shared node.
- **Tailscale / VPN cloud — new `vpn` kind.** Cleanest overlay to detect: a `tailscale0` interface
  with an IP in `100.64.0.0/10` (CGNAT) is unambiguous. Render a `vpn` cloud on the far side of that
  subnet → an at-a-glance "which hosts are on the tailnet." Other overlays by interface-name
  heuristic only: WireGuard `wg*`, OpenVPN `tun*`/`tap*`, Zerotier `zt*` (no canonical IP range).

**Caveat (honest):** a cloud collapses a mesh/whole-Internet into one node — but it is the *same*
honest simplification the existing `internet` node already makes. Draw a cloud only on evidence (an
interface in the overlay range, or a default route leaving known subnets), never fabricated.

---

## 5. Track 3 — Interface labels on device↔subnet edges

The interface name is already aggregated in `SubnetInterface`, and the renderer's edge shape already
carries a `via` field. Work: extend L3 `SubnetGraphEdge` with an interface/`via` field and render it
along the edge.

**Synergy:** the edge interface name *is* the `docker0`/`tailscale0` signal Track 1 and Track 2 key
on — labeling edges makes the host-local and VPN cases self-evident to a human reader. Watch
edge-label clutter on a force layout; mitigate by showing on hover or only at higher zoom.

---

## 6. Track 4 — Real icons per node kind

Nodes are `<g>` groups, so an icon is an appended inline `<svg>`/`<use>`. Two landmines:

- **Licensing (the real trap).** The network-diagram instinct is Cisco's router/switch/cloud icons —
  those are trademarked and **not freely redistributable; do not use them.** Use a permissive set:
  **Tabler (MIT), Lucide (ISC), Font Awesome free (CC BY 4.0 / OFL), or Material Symbols
  (Apache 2.0)** — all have router/server/cloud/network glyphs.
- **CSP.** Hand-vendor a small inline SVG sprite under `wwwroot/` (referenced with
  `asp-append-version`) — **not** an icon font from a CDN, **not** any `eval`-based plugin. Fill icons
  with the existing CSS color vars (`--accent`, `--info`, `--ok`) so they follow the theme.

Kind → icon: `subnet` → LAN/network glyph, `router` → router, `device`/host → server (differentiate
workstation vs server later), `internet` → cloud, `vpn` → cloud-with-lock / shield, `switch` (if L2
lands) → switch.

---

## 7. Sequencing

Semantic changes (Tracks 1–3) change *what the diagram tells you*; icons (Track 4) change *how
clearly*. Recommended order:

1. **Track 1** — host-local keying (correctness; can ship on the name heuristic first, then layer the
   authoritative Docker `/networks` data on top).
2. **Track 3** — interface edge labels (cheap; reuses aggregated data; reinforces Track 1).
3. **Track 2** — VPN/WAN clouds (extends the existing `internet` precedent).
4. **Track 4** — icons (cosmetic polish; last).

## 8. Open questions

- Do we want host-local subnets shown at all on L3, or collapsed behind a per-host "N container
  networks" affordance to reduce noise? (Leaning: show, scoped per host, but collapsible.)
- macvlan/ipvlan containers hold real LAN IPs — should they merge into the real LAN subnet node
  (correct L3 view) rather than render as a separate Docker subnet? (Leaning: yes — they *are* on
  the LAN.)
- Per-device Internet nodes vs. one shared Internet cloud: per-device is more honest about distinct
  egress paths but busier. Decide during Track 2.
