// Client-side search + sort for tables whose full row set is already rendered (detail pages, small
// admin lists, the subnet list) — the ones that are NOT keyset-paginated, so the server-side
// filter-bar/sortable-header machinery (which round-trips a page fragment) doesn't apply.
//
// Opt in with `data-client-grid` on the <table>. This script injects a search box above it and makes
// the headers click-to-sort (numeric-aware; per-header opt-out with `data-no-sort`). Column reorder
// (column-reorder.js) and resize (column-resize.js) still apply on top — reorder suppresses the sort
// click after a drag, so the two share the header without conflict. Sort/filter state is in-memory
// only (per the request, just column *order* persists); a full navigation resets it.
(function () {
    'use strict';

    function scrollWrap(table) {
        var p = table.parentElement;
        return p && (p.classList.contains('grid-scroll') || p.classList.contains('detail-table-scroll'))
            ? p
            : table;
    }

    function sortValue(cell) {
        var explicit = cell.getAttribute('data-sort');
        return (explicit !== null ? explicit : cell.textContent).trim();
    }

    function bodyRows(table) {
        var rows = [];
        for (var b = 0; b < table.tBodies.length; b++) {
            var trs = table.tBodies[b].rows;
            for (var r = 0; r < trs.length; r++) {
                // Skip colspan filler rows (empty-state messages) — they aren't sortable data.
                if (trs[r].cells.length === headerCount(table)) {
                    rows.push(trs[r]);
                }
            }
        }
        return rows;
    }

    function headerCount(table) {
        return table.tHead && table.tHead.rows.length > 0 ? table.tHead.rows[0].cells.length : 0;
    }

    function applyFilter(table, query) {
        var q = query.trim().toLowerCase();
        for (var b = 0; b < table.tBodies.length; b++) {
            var trs = table.tBodies[b].rows;
            for (var r = 0; r < trs.length; r++) {
                var row = trs[r];
                if (row.cells.length !== headerCount(table)) {
                    continue; // leave filler rows untouched
                }
                var hit = q === '' || row.textContent.toLowerCase().indexOf(q) >= 0;
                row.classList.toggle('cg-hidden', !hit);
            }
        }
    }

    function sortByColumn(table, colIndex, dir) {
        var rows = bodyRows(table);
        var vals = rows.map(function (row) { return sortValue(row.cells[colIndex]); });
        var numeric = vals.every(function (v) {
            return v === '' || v === '—' || !isNaN(parseFloat(v.replace(/[, ]/g, '')));
        });

        var idx = rows.map(function (row, i) { return i; });
        idx.sort(function (a, b) {
            var va = vals[a];
            var vb = vals[b];
            var cmp;
            if (numeric) {
                var na = parseFloat(va.replace(/[, ]/g, ''));
                var nb = parseFloat(vb.replace(/[, ]/g, ''));
                na = isNaN(na) ? -Infinity : na;
                nb = isNaN(nb) ? -Infinity : nb;
                cmp = na - nb;
            } else {
                cmp = va.localeCompare(vb, undefined, { numeric: true, sensitivity: 'base' });
            }
            return dir === 'desc' ? -cmp : cmp;
        });

        // Re-append rows in sorted order within their first tbody (client-grid tables use one tbody).
        var tbody = table.tBodies[0];
        var frag = document.createDocumentFragment();
        idx.forEach(function (i) { frag.appendChild(rows[i]); });
        tbody.appendChild(frag);
    }

    function wireHeaders(table) {
        var ths = table.tHead.rows[0].cells;
        for (var i = 0; i < ths.length; i++) {
            (function (th) {
                if (th.hasAttribute('data-no-sort')) {
                    return;
                }
                th.classList.add('cg-sortable');
                th.addEventListener('click', function (e) {
                    if (e.target.closest('.col-resize-handle')) {
                        return;
                    }
                    var current = th.getAttribute('aria-sort');
                    var dir = current === 'ascending' ? 'desc' : 'asc';
                    var all = table.tHead.rows[0].cells;
                    for (var j = 0; j < all.length; j++) {
                        all[j].removeAttribute('aria-sort');
                    }
                    th.setAttribute('aria-sort', dir === 'asc' ? 'ascending' : 'descending');
                    // Read the live index so sorting stays correct after a column reorder.
                    sortByColumn(table, Array.prototype.indexOf.call(all, th), dir);
                });
            })(ths[i]);
        }
    }

    function injectSearch(table) {
        var anchor = scrollWrap(table);
        if (anchor.previousElementSibling && anchor.previousElementSibling.classList.contains('client-grid-toolbar')) {
            return; // already injected (e.g. re-run after an htmx swap of an ancestor)
        }
        var bar = document.createElement('div');
        bar.className = 'client-grid-toolbar';
        var input = document.createElement('input');
        input.type = 'search';
        input.className = 'input client-grid-search';
        input.placeholder = 'Filter…';
        input.setAttribute('aria-label', 'Filter table');
        input.addEventListener('input', function () { applyFilter(table, input.value); });
        bar.appendChild(input);
        anchor.parentNode.insertBefore(bar, anchor);
    }

    function enhance(table) {
        if (table.dataset.clientGrid === 'wired' || headerCount(table) === 0) {
            return;
        }
        table.dataset.clientGrid = 'wired';
        injectSearch(table);
        wireHeaders(table);
    }

    function initAll() {
        document.querySelectorAll('table[data-client-grid]').forEach(enhance);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll);
    } else {
        initAll();
    }
    document.addEventListener('htmx:afterSwap', initAll);
})();
