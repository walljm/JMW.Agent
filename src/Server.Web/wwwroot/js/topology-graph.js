// Shared D3 graph renderer for the Subnets page's Topology (L3) and Physical/L2 tabs —
// see docs/plans/d3-l2-l3.md decision #2 (one renderer, two data shapes).
// Vendors d3.v7.min.js locally (loaded before this script); no CDN, no build step.
//
// Two layout modes (per container):
//   'force' — d3-force directed graph (default; good for exploring clusters).
//   'hier'  — top-down layered/BFS tree, the conventional network-engineering view.
//             Nodes are assigned to layers by hop-distance from one or more "top" (root)
//             nodes; within a layer they're ordered by a barycenter sweep to reduce edge
//             crossings. Roots default to the highest network tier by kind (L3) or the
//             highest-degree node (L2), and can be overridden by shift/⌘/ctrl-clicking nodes.
//
// Usage:
//   renderTopologyGraph('topology-graph', { nodes:[{id,label,kind}], edges:[{fromId,toId,fromPort,toPort,via}] },
//       { layout:'force'|'hier', roots:[id,…], onStateChange: ({layout,roots}) => … });
//   setTopologyLayout('topology-graph', 'hier');   // switch mode, redraw, fire onStateChange
//   resetTopologyRoots('topology-graph');          // clear manual roots, fall back to auto
//
// Node kinds get a color/shape via KIND_STYLE below; unrecognized kinds fall back to a
// neutral circle so a future node kind never renders invisibly. Each kind also gets a small
// line-art icon (ICON_PATHS) drawn above the node — permissive glyphs (Tabler/Lucide-style,
// MIT/ISC), hand-vendored as inline SVG paths so nothing loads under the no-'unsafe-eval' CSP.
(function () {
    // `r` (circle radius) / `w`,`h` (rect size) default to the shape's usual size below when
    // omitted — only the Internet node currently overrides them, to read as the clear edge of
    // the network rather than just another same-sized node.
    const KIND_STYLE = {
        subnet: { shape: 'rect', varName: '--info' },
        router: { shape: 'rect', varName: '--accent' },
        internet: { shape: 'circle', varName: '--ok', r: 26 },
        // Same green as Internet (it's the ISP uplink segment made visible, the rarer
        // counterpart to the synthetic Internet node) but still a rect — it IS a real subnet.
        'wan-subnet': { shape: 'rect', varName: '--ok' },
        vpn: { shape: 'circle', varName: '--warn' },
        device: { shape: 'rect', varName: '--accent' },
        // A Google Wifi/OnHub mesh point known only by BSSID (L2 graph only) — amber like vpn,
        // since it's also "real infrastructure we can only partially identify", not a plain device.
        mesh: { shape: 'circle', varName: '--warn' },
        unknown: { shape: 'circle', varName: '--text-faint' },
    };
    const DEFAULT_STYLE = { shape: 'circle', varName: '--text-dim' };
    const DEFAULT_RECT = { w: 92, h: 32 };
    const DEFAULT_CIRCLE_R = 18;

    // Network-tier ordering for auto-root selection on the L3 graph: lower = higher tier
    // (closer to the internet edge), so auto-roots are the lowest-rank kind present. The L2
    // graph carries none of these kinds, so it falls back to highest-degree (see chooseAutoRoots).
    // internet and wan-subnet share rank 0 — they're mutually exclusive per graph (see
    // SubnetsApi.GetGraphAsync), never both roots at once.
    const KIND_RANK = { internet: 0, 'wan-subnet': 0, router: 1, subnet: 2, device: 2, vpn: 3, unknown: 4 };
    const L3_KINDS = new Set(['internet', 'wan-subnet', 'router', 'subnet', 'vpn']);

    // Icons on a 24×24 grid (Lucide viewBox), stroke line-art, drawn centered above each node.
    // network / router / server / globe / shield — themeable via stroke color.
    const ICON_PATHS = {
        subnet: 'M12 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM18 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM12 9v3M7.5 16.5 10.5 12.5M16.5 16.5 13.5 12.5',
        router: 'M6.5 17.5h11a2 2 0 0 0 2-2v-1a2 2 0 0 0-2-2h-11a2 2 0 0 0-2 2v1a2 2 0 0 0 2 2ZM7 15v.01M11 15v.01M15 9l3-3M19 10l2-2M9 15h6',
        internet: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18ZM3.5 9h17M3.5 15h17M12 3a13 13 0 0 1 0 18 13 13 0 0 1 0-18Z',
        'wan-subnet': 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18ZM3.5 9h17M3.5 15h17M12 3a13 13 0 0 1 0 18 13 13 0 0 1 0-18Z',
        vpn: 'M12 3 5 6v5c0 4 3 7.5 7 9 4-1.5 7-5 7-9V6l-7-3ZM12 11v3M12 8v.01',
        device: 'M4 5h16v6H4zM4 13h16v6H4zM7 8h.01M7 16h.01',
        mesh: 'M5 12.55a11 11 0 0 1 14.08 0M1.42 9a16 16 0 0 1 21.16 0M8.53 16.11a6 6 0 0 1 6.95 0M12 20h.01',
    };

    function cssVar(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }

    function styleFor(kind) {
        return KIND_STYLE[kind] || DEFAULT_STYLE;
    }

    // Derive the drawing height from the space left below the container in the viewport, so the
    // graph fills the window instead of a fixed band. Reserve the padding that sits below the graph
    // — the card's bottom padding and the scrolling .main's bottom padding — so the SVG doesn't push
    // its scroll container a few pixels past the viewport and leave a faint scrollbar. Clamped to a
    // sane minimum.
    function measuredHeight(container) {
        const top = container.getBoundingClientRect().top;
        const padBottom = (el) => (el ? parseFloat(getComputedStyle(el).paddingBottom) || 0 : 0);
        const reserve = padBottom(container.closest('.card')) + padBottom(container.closest('.main')) + 8;
        const avail = window.innerHeight - top - reserve;
        return Math.max(480, Math.floor(avail));
    }

    // One ResizeObserver + resize listener per container keeps the SVG sized to the visible
    // container. Crucial for tabbed panels: a `hidden` panel has clientWidth 0, so its graph
    // can't be measured until it becomes visible — the observer fires on that 0→real change
    // and draws it correctly then, and again on any window resize. Redraw is gated on a WIDTH
    // change so setting the SVG's height (which grows the container) can't feed back into a loop.
    const observed = new WeakSet();

    function setupAutoResize(container) {
        if (observed.has(container)) {
            return;
        }
        observed.add(container);

        let timer = null;

        function scheduleRedraw() {
            const width = container.clientWidth;
            if (width === 0 || width === container.__topoWidth) {
                return; // hidden, or only the height changed (our own draw) — ignore
            }
            if (timer) {
                clearTimeout(timer);
            }
            timer = setTimeout(() => drawIfVisible(container), 120);
        }

        if (typeof ResizeObserver !== 'undefined') {
            new ResizeObserver(scheduleRedraw).observe(container);
        }
        // Pure-vertical viewport changes don't alter the container width, so the observer
        // won't fire — a window resize listener covers that case (and re-measures height).
        window.addEventListener('resize', () => {
            if (timer) {
                clearTimeout(timer);
            }
            timer = setTimeout(() => drawIfVisible(container), 120);
        });
    }

    function drawIfVisible(container) {
        if (container.clientWidth > 0 && container.__topoGraph) {
            draw(container, container.__topoGraph);
        }
    }

    // Notify the host page that the interactive state (layout mode / chosen roots / manually
    // dragged node positions) changed, so it can persist it and sync its toolbar controls.
    // Positions are keyed by the graph's stable node ids (see SubnetsApi.GetGraphAsync), so a
    // saved position still lands on the same physical entity after a rebuild.
    function fireState(container) {
        if (typeof container.__topoOnState === 'function') {
            container.__topoOnState({
                layout: container.__topoLayout === 'hier' ? 'hier' : 'force',
                roots: Array.from(container.__topoRoots || []),
                positions: Object.assign({}, container.__topoPositions || {}),
            });
        }
    }

    window.renderTopologyGraph = function (containerId, graph, options) {
        const container = document.getElementById(containerId);
        if (!container) {
            return;
        }
        options = options || {};

        // Stash everything so the resize observer can redraw on tab reveal / resize without the
        // page re-supplying it. Options are only applied when present, so an option-less redraw
        // (e.g. the fullscreen toggle) preserves the current layout, roots, positions, and callback.
        container.__topoGraph = graph;
        if (options.layout) {
            container.__topoLayout = options.layout === 'hier' ? 'hier' : 'force';
        }
        if (options.roots) {
            container.__topoRoots = new Set(options.roots);
        }
        if (options.positions) {
            container.__topoPositions = Object.assign({}, options.positions);
        }
        if (typeof options.onStateChange === 'function') {
            container.__topoOnState = options.onStateChange;
        }

        setupAutoResize(container);
        drawIfVisible(container);
    };

    window.setTopologyLayout = function (containerId, layout) {
        const container = document.getElementById(containerId);
        if (!container) {
            return;
        }
        container.__topoLayout = layout === 'hier' ? 'hier' : 'force';
        fireState(container);
        drawIfVisible(container);
    };

    window.resetTopologyRoots = function (containerId) {
        const container = document.getElementById(containerId);
        if (!container) {
            return;
        }
        container.__topoRoots = new Set();
        fireState(container);
        drawIfVisible(container);
    };

    function toggleRoot(container, id) {
        if (!(container.__topoRoots instanceof Set)) {
            container.__topoRoots = new Set();
        }
        if (container.__topoRoots.has(id)) {
            container.__topoRoots.delete(id);
        } else {
            container.__topoRoots.add(id);
        }
        fireState(container);
        drawIfVisible(container);
    }

    function linkEndId(end) {
        return typeof end === 'object' && end !== null ? end.id : end;
    }

    function rankOf(kind) {
        return KIND_RANK[kind] !== undefined ? KIND_RANK[kind] : KIND_RANK.unknown;
    }

    function degreeMap(nodes, links) {
        const deg = new Map(nodes.map((n) => [n.id, 0]));
        links.forEach((l) => {
            const s = linkEndId(l.source);
            const t = linkEndId(l.target);
            if (deg.has(s)) deg.set(s, deg.get(s) + 1);
            if (deg.has(t)) deg.set(t, deg.get(t) + 1);
        });
        return deg;
    }

    // Pick sensible default roots when the operator hasn't designated any. L3 graphs have real
    // tier semantics in `kind`, so root at the highest tier present (internet, else router, …).
    // L2 (and any graph with no L3 kinds) is a flat device mesh — root at the most-connected node.
    function chooseAutoRoots(nodes, links) {
        const hasL3 = nodes.some((n) => L3_KINDS.has(n.kind));
        if (hasL3) {
            let minRank = Infinity;
            nodes.forEach((n) => {
                const r = rankOf(n.kind);
                if (r < minRank) minRank = r;
            });
            const roots = nodes.filter((n) => rankOf(n.kind) === minRank).map((n) => n.id);
            // Guard against the whole graph being one rank (would give a degenerate single layer).
            if (roots.length > 0 && roots.length < nodes.length) {
                return roots;
            }
        }
        const deg = degreeMap(nodes, links);
        let best = nodes[0].id;
        let bestDeg = -1;
        nodes.forEach((n) => {
            const d = deg.get(n.id) || 0;
            if (d > bestDeg) {
                bestDeg = d;
                best = n.id;
            }
        });
        return [best];
    }

    // Compute top-down layered positions in place (sets d.x/d.y, pins d.fx/d.fy, tags d.__layer).
    // Layers = hop-distance from the root set via undirected BFS (robust to how edge direction is
    // stored, and naturally tolerant of cycles/redundant links). Unreachable nodes drop to a
    // trailing layer so nothing vanishes. Returns nothing; callers read d.__layer for root styling.
    function layoutHierarchy(nodes, links, rootSet, width, height) {
        const nodeById = new Map(nodes.map((n) => [n.id, n]));
        const adj = new Map(nodes.map((n) => [n.id, []]));
        links.forEach((l) => {
            const s = linkEndId(l.source);
            const t = linkEndId(l.target);
            if (adj.has(s) && adj.has(t)) {
                adj.get(s).push(t);
                adj.get(t).push(s);
            }
        });

        let rootIds = Array.from(rootSet || []).filter((id) => nodeById.has(id));
        if (rootIds.length === 0) {
            rootIds = chooseAutoRoots(nodes, links);
        }

        // Multi-source BFS → layer per node.
        const layerOf = new Map();
        const queue = [];
        rootIds.forEach((id) => {
            if (!layerOf.has(id)) {
                layerOf.set(id, 0);
                queue.push(id);
            }
        });
        for (let i = 0; i < queue.length; i++) {
            const id = queue[i];
            const depth = layerOf.get(id);
            adj.get(id).forEach((nb) => {
                if (!layerOf.has(nb)) {
                    layerOf.set(nb, depth + 1);
                    queue.push(nb);
                }
            });
        }

        let maxLayer = 0;
        layerOf.forEach((v) => {
            if (v > maxLayer) maxLayer = v;
        });
        let hasOrphan = false;
        nodes.forEach((n) => {
            if (!layerOf.has(n.id)) {
                layerOf.set(n.id, maxLayer + 1);
                hasOrphan = true;
            }
        });
        const layerCount = hasOrphan ? maxLayer + 2 : maxLayer + 1;

        // Group nodes by layer (input order), then one downward barycenter sweep to reduce crossings.
        const layers = Array.from({ length: layerCount }, () => []);
        nodes.forEach((n) => {
            n.__layer = layerOf.get(n.id);
            layers[n.__layer].push(n);
        });

        const orderIndex = new Map();
        layers.forEach((layerNodes, li) => {
            if (li > 0) {
                const keyed = layerNodes.map((n, idx) => {
                    const parents = adj.get(n.id).filter((id) => layerOf.get(id) === li - 1);
                    let key;
                    if (parents.length === 0) {
                        key = idx; // no parent above — keep relative order
                    } else {
                        let sum = 0;
                        parents.forEach((id) => (sum += orderIndex.get(id) || 0));
                        key = sum / parents.length;
                    }
                    return { n, key, idx };
                });
                keyed.sort((a, b) => a.key - b.key || a.idx - b.idx);
                layers[li] = keyed.map((k) => k.n);
            }
            layers[li].forEach((n, i) => orderIndex.set(n.id, i));
        });

        // Assign coordinates. Each layer is centered horizontally; the drawing may exceed the
        // viewBox for large graphs — zoom/pan covers that.
        const topMargin = 60;
        const usable = height - topMargin - 40;
        const layerGap = layerCount > 1 ? Math.max(90, Math.min(150, usable / (layerCount - 1))) : 0;
        const nodeSpacingX = 130;
        layers.forEach((layerNodes, li) => {
            const spanW = (layerNodes.length - 1) * nodeSpacingX;
            layerNodes.forEach((n, i) => {
                n.x = width / 2 - spanW / 2 + i * nodeSpacingX;
                n.y = topMargin + li * layerGap;
                n.fx = n.x;
                n.fy = n.y;
            });
        });
    }

    function draw(container, graph) {
        container.innerHTML = '';

        if (!graph || !graph.nodes || graph.nodes.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'empty';
            empty.textContent = 'Nothing to show yet.';
            container.appendChild(empty);
            return;
        }

        const layout = container.__topoLayout === 'hier' ? 'hier' : 'force';
        const roots = container.__topoRoots instanceof Set ? container.__topoRoots : new Set();

        const width = container.clientWidth || 800;
        const height = measuredHeight(container);
        container.__topoWidth = width; // so the resize observer ignores our own height change

        const nodes = graph.nodes.map((n) => Object.assign({}, n));
        // Restore any manually dragged position (force mode only — hier mode's layoutHierarchy
        // below unconditionally overwrites x/y/fx/fy for every node, so seeding here is harmless
        // either way). Positions are keyed by the stable node id, not by array position.
        const savedPositions = container.__topoPositions || {};
        nodes.forEach((n) => {
            const pos = savedPositions[n.id];
            if (pos) {
                n.x = pos.x;
                n.y = pos.y;
                n.fx = pos.x;
                n.fy = pos.y;
            }
        });
        const nodeById = new Map(nodes.map((n) => [n.id, n]));
        const links = graph.edges
            .filter((e) => nodeById.has(e.fromId) && nodeById.has(e.toId))
            .map((e) => Object.assign({}, e, {
                source: nodeById.get(e.fromId),
                target: nodeById.get(e.toId),
            }));

        const svg = d3.select(container)
            .append('svg')
            .attr('class', 'topology-svg')
            .attr('width', '100%')
            .attr('height', height)
            .attr('viewBox', [0, 0, width, height]);

        const zoomLayer = svg.append('g').attr('class', 'tgraph-zoom-layer');

        svg.call(
            d3.zoom()
                .scaleExtent([0.2, 4])
                .on('zoom', (event) => zoomLayer.attr('transform', event.transform))
        );

        const linkColor = cssVar('--border-strong') || '#888';
        const textColor = cssVar('--text') || '#eee';

        // Force mode runs a simulation; hierarchical mode computes fixed positions once.
        let simulation = null;
        if (layout === 'hier') {
            layoutHierarchy(nodes, links, roots, width, height);
        } else {
            simulation = d3.forceSimulation(nodes)
                .force('link', d3.forceLink(links).id((d) => d.id).distance(110))
                .force('charge', d3.forceManyBody().strength(-280))
                .force('center', d3.forceCenter(width / 2, height / 2))
                .force('collide', d3.forceCollide(40));
        }

        const link = zoomLayer.append('g')
            .attr('class', 'tgraph-edges')
            .selectAll('line')
            .data(links)
            .join('line')
            .attr('class', 'tgraph-edge')
            .attr('stroke', linkColor)
            .attr('stroke-width', 1.5);

        // Track 3: interface name (e.g. "eth0", "docker0") along each labeled device↔subnet edge,
        // drawn at the edge midpoint. Only edges that carry a `via` get a label — keeps clutter down.
        const edgeLabel = zoomLayer.append('g')
            .attr('class', 'tgraph-edge-labels')
            .selectAll('text')
            .data(links.filter((l) => l.via))
            .join('text')
            .attr('class', 'tgraph-edge-label')
            .attr('text-anchor', 'middle')
            .attr('dy', '-2')
            .attr('fill', textColor)
            .attr('font-size', '8px')
            .attr('opacity', 0.75)
            .attr('pointer-events', 'none')
            .text((d) => d.via);

        const node = zoomLayer.append('g')
            .attr('class', 'tgraph-nodes')
            .selectAll('g')
            .data(nodes)
            .join('g')
            .attr('class', (d) => `tgraph-node kind-${d.kind}`)
            .classed('is-root', (d) => (layout === 'hier' ? d.__layer === 0 : roots.has(d.id)))
            .call(dragBehavior());

        node.each(function (d) {
            const g = d3.select(this);
            const style = styleFor(d.kind);
            const color = cssVar(style.varName) || '#888';

            let shapeTop; // top edge (rect) / negative radius (circle) — anchors the icon above it
            if (style.shape === 'rect') {
                const w = style.w || DEFAULT_RECT.w;
                const h = style.h || DEFAULT_RECT.h;
                shapeTop = -h / 2;
                g.append('rect')
                    .attr('x', -w / 2)
                    .attr('y', shapeTop)
                    .attr('width', w)
                    .attr('height', h)
                    .attr('rx', 6)
                    .attr('fill', color)
                    .attr('fill-opacity', 0.85);
            } else {
                const r = style.r || DEFAULT_CIRCLE_R;
                shapeTop = -r;
                g.append('circle')
                    .attr('r', r)
                    .attr('fill', color)
                    .attr('fill-opacity', 0.85);
            }

            g.append('title').text(d.label);

            // Track 4: line-art icon above the node, in the node's own accent color.
            const iconPath = ICON_PATHS[d.kind];
            if (iconPath) {
                const iconSize = 18;
                g.append('path')
                    .attr('class', 'tgraph-icon')
                    .attr('d', iconPath)
                    .attr('transform',
                        `translate(${-iconSize / 2}, ${shapeTop - iconSize - 2}) scale(${iconSize / 24})`)
                    .attr('fill', 'none')
                    .attr('stroke', color)
                    .attr('stroke-width', 2)
                    .attr('stroke-linecap', 'round')
                    .attr('stroke-linejoin', 'round');
            }
        });

        node.append('text')
            .attr('class', 'tgraph-label')
            .attr('text-anchor', 'middle')
            .attr('dy', '0.32em')
            .attr('fill', textColor)
            .attr('font-size', '10px')
            .attr('pointer-events', 'none')
            .text((d) => truncateLabel(d.label));

        // Click-to-highlight: dim everything except the clicked node, its neighbors, and
        // the connecting edges. Click the same node again (or the background) to clear.
        // Modifier-click (shift/⌘/ctrl) instead toggles the node as a hierarchy root.
        let highlighted = null;

        function neighborsOf(id) {
            const set = new Set([id]);
            links.forEach((l) => {
                const s = linkEndId(l.source);
                const t = linkEndId(l.target);
                if (s === id) set.add(t);
                if (t === id) set.add(s);
            });
            return set;
        }

        function applyHighlight() {
            if (highlighted === null) {
                node.classed('dimmed', false);
                link.classed('dimmed', false);
                return;
            }

            const connected = neighborsOf(highlighted);
            node.classed('dimmed', (d) => !connected.has(d.id));
            link.classed('dimmed', (l) => {
                const s = linkEndId(l.source);
                const t = linkEndId(l.target);
                return s !== highlighted && t !== highlighted;
            });
        }

        node.on('click', (event, d) => {
            event.stopPropagation();
            if (event.shiftKey || event.metaKey || event.ctrlKey) {
                toggleRoot(container, d.id); // triggers a redraw with the new root set
                return;
            }
            highlighted = highlighted === d.id ? null : d.id;
            applyHighlight();
        });

        svg.on('click', () => {
            highlighted = null;
            applyHighlight();
        });

        function positionElements() {
            link
                .attr('x1', (d) => d.source.x)
                .attr('y1', (d) => d.source.y)
                .attr('x2', (d) => d.target.x)
                .attr('y2', (d) => d.target.y);

            node.attr('transform', (d) => `translate(${d.x},${d.y})`);

            edgeLabel
                .attr('x', (d) => (d.source.x + d.target.x) / 2)
                .attr('y', (d) => (d.source.y + d.target.y) / 2);
        }

        if (simulation) {
            simulation.on('tick', positionElements);
        } else {
            positionElements(); // static layout — one pass
        }

        // Drag: in force mode, pin then let the simulation settle (double-click releases).
        // In hierarchical mode there's no simulation, so just move the node and redraw edges.
        function dragBehavior() {
            function dragstarted(event, d) {
                if (simulation && !event.active) simulation.alphaTarget(0.3).restart();
                d.fx = d.x;
                d.fy = d.y;
            }

            function dragged(event, d) {
                d.fx = event.x;
                d.fy = event.y;
                if (!simulation) {
                    d.x = event.x;
                    d.y = event.y;
                    positionElements();
                }
            }

            function dragended(event, d) {
                if (simulation && !event.active) simulation.alphaTarget(0);
                // Sticky: leave fx/fy set so a positioned node stays put. Double-click to release.
                // Force mode only — a hier-mode drag is a one-off nudge, not a saved arrangement
                // (the next redraw recomputes fixed layered positions regardless).
                if (simulation) {
                    if (!container.__topoPositions) container.__topoPositions = {};
                    container.__topoPositions[d.id] = { x: d.fx, y: d.fy };
                    fireState(container);
                }
            }

            return d3.drag()
                .on('start', dragstarted)
                .on('drag', dragged)
                .on('end', dragended);
        }

        node.on('dblclick', (event, d) => {
            event.stopPropagation();
            if (!simulation) {
                return; // fixed layout — nothing to release
            }
            d.fx = null;
            d.fy = null;
            if (container.__topoPositions && d.id in container.__topoPositions) {
                delete container.__topoPositions[d.id];
                fireState(container);
            }
            simulation.alphaTarget(0.3).restart();
            setTimeout(() => simulation.alphaTarget(0), 300);
        });
    }

    function truncateLabel(label) {
        const s = String(label || '');
        return s.length > 16 ? s.slice(0, 15) + '…' : s;
    }
})();
