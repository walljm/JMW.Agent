// Drag data-table column headers to reorder columns, persisted per user (server-side, so the order
// follows the user across browsers/devices — not localStorage, mirroring column-resize.js). Order is
// stored as { order: [colKey, ...] } under the preference key "colorder:<table-slug>", where the slug
// derives from the table's aria-label (or id).
//
// Movable vs anchor columns: only headers with a stable data-col-key are movable (and draggable);
// headers without one (row-select checkboxes, action columns) are fixed anchors that keep their
// position. A table needs at least two movable columns to be reorderable.
//
// Coexistence: column-resize.js keys saved widths by the same data-col-key, so a resized width stays
// attached to its column when it moves. The resize handle sits on the header's right edge and stops
// its own mousedown, so a resize never starts a reorder drag.
(function () {
    var meta = document.querySelector('meta[name="csrf-token"]');
    var CSRF = meta ? meta.content : '';

    var cache = {};   // key -> [colKey, ...] (movable columns, in saved order)
    var pending = {}; // key -> debounce timer
    var DRAG_THRESHOLD = 4; // px before a press becomes a drag (vs a sort click)

    function tableKey(table) {
        var raw = table.getAttribute('aria-label') || table.id || '';
        var slug = raw.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
        return slug ? 'colorder:' + slug : null;
    }

    function headerCells(table) {
        return table.tHead && table.tHead.rows.length > 0
            ? Array.prototype.slice.call(table.tHead.rows[0].cells)
            : [];
    }

    function colKeyOf(th) {
        return th.getAttribute('data-col-key');
    }

    // Positions/keys of the movable (keyed) columns in current DOM order. `duplicate` is set if two
    // headers share a col-key — reordering can't tell them apart, so callers bail rather than corrupt
    // the row (fail-safe: the table simply isn't reorderable until the keys are made unique).
    function movableInfo(table) {
        var ths = headerCells(table);
        var positions = [];
        var keyToIndex = {};
        var duplicate = false;
        ths.forEach(function (th, i) {
            var k = colKeyOf(th);
            if (k) {
                if (keyToIndex[k] != null) {
                    duplicate = true;
                }
                positions.push(i);
                keyToIndex[k] = i;
            }
        });
        return { ths: ths, positions: positions, keyToIndex: keyToIndex, duplicate: duplicate };
    }

    // Rebuild one row so the movable columns follow targetKeys, leaving anchor columns in place.
    function reorderRow(row, info, targetKeys) {
        if (!row || row.cells.length !== info.ths.length) {
            return; // filler / colspan row — leave it alone
        }
        var cells = Array.prototype.slice.call(row.cells);
        var desired = cells.slice(); // anchors keep their current cell by default
        for (var k = 0; k < info.positions.length; k++) {
            desired[info.positions[k]] = cells[info.keyToIndex[targetKeys[k]]];
        }
        var frag = document.createDocumentFragment();
        desired.forEach(function (c) { frag.appendChild(c); });
        row.appendChild(frag);
    }

    function applyOrder(table, order) {
        if (!order || order.length === 0) {
            return;
        }
        var info = movableInfo(table);
        if (info.positions.length < 2 || info.duplicate) {
            return;
        }

        var currentKeys = info.positions.map(function (i) { return colKeyOf(info.ths[i]); });
        // Keep saved keys that still exist, then append any new columns in their authored order —
        // so a schema change adds columns rather than discarding the saved layout.
        var target = order.filter(function (k) { return info.keyToIndex[k] != null; });
        currentKeys.forEach(function (k) {
            if (target.indexOf(k) < 0) {
                target.push(k);
            }
        });
        if (target.length !== currentKeys.length) {
            return;
        }
        if (currentKeys.every(function (k, i) { return k === target[i]; })) {
            return; // already in saved order — avoid needless DOM churn on every htmx swap
        }

        reorderRow(table.tHead && table.tHead.rows[0], info, target);
        for (var b = 0; b < table.tBodies.length; b++) {
            var rows = table.tBodies[b].rows;
            for (var r = 0; r < rows.length; r++) {
                reorderRow(rows[r], info, target);
            }
        }
        if (table.tFoot) {
            for (var f = 0; f < table.tFoot.rows.length; f++) {
                reorderRow(table.tFoot.rows[f], info, target);
            }
        }
    }

    function save(key, order) {
        cache[key] = order;
        if (pending[key]) {
            clearTimeout(pending[key]);
        }
        pending[key] = setTimeout(function () {
            fetch('/api/v1/preferences/' + encodeURIComponent(key), {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': CSRF },
                body: JSON.stringify({ order: order }),
            });
        }, 500);
    }

    // Swallow the sort click that would otherwise fire on the header we just finished dragging.
    function suppressNextClick(th) {
        function swallow(e) {
            e.preventDefault();
            e.stopPropagation();
            th.removeEventListener('click', swallow, true);
        }
        th.addEventListener('click', swallow, true);
        setTimeout(function () { th.removeEventListener('click', swallow, true); }, 0);
    }

    // Insertion slot (0..m) among the movable headers nearest the pointer x.
    function slotAtX(movableThs, x) {
        for (var i = 0; i < movableThs.length; i++) {
            var rect = movableThs[i].getBoundingClientRect();
            if (x < rect.left + rect.width / 2) {
                return i;
            }
        }
        return movableThs.length;
    }

    function beginDrag(table, key, th, startEvent) {
        var info = movableInfo(table);
        var currentKeys = info.positions.map(function (i) { return colKeyOf(info.ths[i]); });
        var movableThs = info.positions.map(function (i) { return info.ths[i]; });
        var fromIndex = currentKeys.indexOf(colKeyOf(th));
        if (fromIndex < 0) {
            return;
        }

        var wrap = table.parentNode;
        if (wrap && getComputedStyle(wrap).position === 'static') {
            wrap.style.position = 'relative';
        }
        var indicator = document.createElement('div');
        indicator.className = 'col-reorder-indicator';
        if (wrap) {
            wrap.appendChild(indicator);
        }
        var dropSlot = fromIndex;

        function positionIndicator(slot) {
            var wrapRect = wrap.getBoundingClientRect();
            var x;
            if (slot < movableThs.length) {
                x = movableThs[slot].getBoundingClientRect().left;
            } else {
                x = movableThs[movableThs.length - 1].getBoundingClientRect().right;
            }
            indicator.style.left = (x - wrapRect.left + wrap.scrollLeft) + 'px';
            indicator.style.top = wrap.scrollTop + 'px';
            indicator.style.height = wrap.clientHeight + 'px';
        }

        function onMove(ev) {
            dropSlot = slotAtX(movableThs, ev.clientX);
            positionIndicator(dropSlot);
        }

        function onUp() {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.userSelect = '';
            th.classList.remove('col-dragging');
            if (indicator.parentNode) {
                indicator.parentNode.removeChild(indicator);
            }

            var to = dropSlot > fromIndex ? dropSlot - 1 : dropSlot;
            if (to !== fromIndex) {
                var next = currentKeys.slice();
                var moved = next.splice(fromIndex, 1)[0];
                next.splice(to, 0, moved);
                applyOrder(table, next);
                save(key, next);
            }
            suppressNextClick(th);
        }

        document.body.style.userSelect = 'none';
        th.classList.add('col-dragging');
        onMove(startEvent);
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    }

    function wireHeader(table, key, th) {
        th.addEventListener('mousedown', function (e) {
            if (e.button !== 0 || e.target.closest('.col-resize-handle')) {
                return; // let the resize handle own edge drags
            }
            var startX = e.pageX;
            var startY = e.pageY;

            function watch(ev) {
                if (Math.abs(ev.pageX - startX) > DRAG_THRESHOLD || Math.abs(ev.pageY - startY) > DRAG_THRESHOLD) {
                    cleanup();
                    beginDrag(table, key, th, ev);
                }
            }
            function cleanup() {
                document.removeEventListener('mousemove', watch);
                document.removeEventListener('mouseup', cleanup);
            }

            document.addEventListener('mousemove', watch);
            document.addEventListener('mouseup', cleanup);
        });
    }

    function wire(table, key) {
        // htmx swaps replace the table, so the dataset flag won't survive — re-apply saved order and
        // (if not yet wired) attach drag handlers to the movable headers.
        applyOrder(table, cache[key]);
        if (table.dataset.reorderable === '1') {
            return;
        }

        var info = movableInfo(table);
        if (info.positions.length < 2 || info.duplicate) {
            return; // nothing (meaningful) to reorder, or ambiguous duplicate keys
        }
        table.dataset.reorderable = '1';
        table.classList.add('cols-reorderable');
        info.positions.forEach(function (i) { wireHeader(table, key, info.ths[i]); });
    }

    function initTable(table) {
        var key = tableKey(table);
        if (!key) {
            return;
        }
        if (cache[key] !== undefined) {
            wire(table, key);
            return;
        }
        fetch('/api/v1/preferences/' + encodeURIComponent(key))
            .then(function (r) { return r.ok ? r.json() : {}; })
            .then(function (v) { cache[key] = (v && v.order) || []; wire(table, key); })
            .catch(function () { cache[key] = []; wire(table, key); });
    }

    function initAll() {
        document.querySelectorAll('table.data').forEach(initTable);
    }

    initAll();
    document.addEventListener('htmx:afterSwap', initAll);
})();
