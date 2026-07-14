// Shared D3 force-directed graph renderer for the Subnets page's Topology (L3) and
// Physical/L2 tabs — see docs/plans/d3-l2-l3.md decision #2 (one renderer, two data shapes).
// Vendors d3.v7.min.js locally (loaded before this script); no CDN, no build step.
//
// Usage:
//   renderTopologyGraph('topology-graph', { nodes: [{id, label, kind}], edges: [{fromId, toId, fromPort, toPort, via}] });
//
// Node kinds get a color/shape via KIND_STYLE below; unrecognized kinds fall back to a
// neutral circle so a future node kind never renders invisibly.
(function () {
    const KIND_STYLE = {
        subnet: { shape: 'rect', varName: '--info' },
        router: { shape: 'rect', varName: '--accent' },
        internet: { shape: 'circle', varName: '--ok' },
        device: { shape: 'rect', varName: '--accent' },
        unknown: { shape: 'circle', varName: '--text-faint' },
    };
    const DEFAULT_STYLE = { shape: 'circle', varName: '--text-dim' };

    function cssVar(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }

    function styleFor(kind) {
        return KIND_STYLE[kind] || DEFAULT_STYLE;
    }

    window.renderTopologyGraph = function (containerId, graph) {
        const container = document.getElementById(containerId);
        if (!container) {
            return;
        }

        container.innerHTML = '';

        if (!graph || !graph.nodes || graph.nodes.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'empty';
            empty.textContent = 'Nothing to show yet.';
            container.appendChild(empty);
            return;
        }

        const width = container.clientWidth || 800;
        const height = 520;

        const nodes = graph.nodes.map((n) => Object.assign({}, n));
        const nodeById = new Map(nodes.map((n) => [n.id, n]));
        const links = graph.edges
            .filter((e) => nodeById.has(e.fromId) && nodeById.has(e.toId))
            .map((e) => Object.assign({}, e, { source: e.fromId, target: e.toId }));

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

        const simulation = d3.forceSimulation(nodes)
            .force('link', d3.forceLink(links).id((d) => d.id).distance(110))
            .force('charge', d3.forceManyBody().strength(-280))
            .force('center', d3.forceCenter(width / 2, height / 2))
            .force('collide', d3.forceCollide(40));

        const link = zoomLayer.append('g')
            .attr('class', 'tgraph-edges')
            .selectAll('line')
            .data(links)
            .join('line')
            .attr('class', 'tgraph-edge')
            .attr('stroke', linkColor)
            .attr('stroke-width', 1.5);

        const node = zoomLayer.append('g')
            .attr('class', 'tgraph-nodes')
            .selectAll('g')
            .data(nodes)
            .join('g')
            .attr('class', (d) => `tgraph-node kind-${d.kind}`)
            .call(drag(simulation));

        node.each(function (d) {
            const g = d3.select(this);
            const style = styleFor(d.kind);
            const color = cssVar(style.varName) || '#888';

            if (style.shape === 'rect') {
                g.append('rect')
                    .attr('x', -46)
                    .attr('y', -16)
                    .attr('width', 92)
                    .attr('height', 32)
                    .attr('rx', 6)
                    .attr('fill', color)
                    .attr('fill-opacity', 0.85);
            } else {
                g.append('circle')
                    .attr('r', 18)
                    .attr('fill', color)
                    .attr('fill-opacity', 0.85);
            }

            g.append('title').text(d.label);
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
        let highlighted = null;

        function neighborsOf(id) {
            const set = new Set([id]);
            links.forEach((l) => {
                const s = typeof l.source === 'object' ? l.source.id : l.source;
                const t = typeof l.target === 'object' ? l.target.id : l.target;
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
                const s = typeof l.source === 'object' ? l.source.id : l.source;
                const t = typeof l.target === 'object' ? l.target.id : l.target;
                return s !== highlighted && t !== highlighted;
            });
        }

        node.on('click', (event, d) => {
            event.stopPropagation();
            highlighted = highlighted === d.id ? null : d.id;
            applyHighlight();
        });

        svg.on('click', () => {
            highlighted = null;
            applyHighlight();
        });

        simulation.on('tick', () => {
            link
                .attr('x1', (d) => d.source.x)
                .attr('y1', (d) => d.source.y)
                .attr('x2', (d) => d.target.x)
                .attr('y2', (d) => d.target.y);

            node.attr('transform', (d) => `translate(${d.x},${d.y})`);
        });

        function drag(sim) {
            function dragstarted(event, d) {
                if (!event.active) sim.alphaTarget(0.3).restart();
                d.fx = d.x;
                d.fy = d.y;
            }

            function dragged(event, d) {
                d.fx = event.x;
                d.fy = event.y;
            }

            function dragended(event, d) {
                if (!event.active) sim.alphaTarget(0);
                // Sticky: leave fx/fy set so a positioned node stays put. Double-click to release.
            }

            return d3.drag()
                .on('start', dragstarted)
                .on('drag', dragged)
                .on('end', dragended);
        }

        node.on('dblclick', (event, d) => {
            event.stopPropagation();
            d.fx = null;
            d.fy = null;
            simulation.alphaTarget(0.3).restart();
            setTimeout(() => simulation.alphaTarget(0), 300);
        });
    };

    function truncateLabel(label) {
        const s = String(label || '');
        return s.length > 16 ? s.slice(0, 15) + '…' : s;
    }
})();
