// Drag-to-resize data-table columns, persisted per user (server-side, so widths follow the user
// across browsers/devices — not localStorage). A table stays in its natural auto layout until the
// user actually resizes it (or has saved widths), so untouched tables look exactly as before.
//
// Width state is stored as { colKey: pixels } under the preference key "cols:<table-slug>", where
// the slug derives from the table's aria-label (or id) and colKey is the header's data-col-key
// (falling back to its column index for tables without keys). Keying by data-col-key keeps a saved
// width attached to its column when column-reorder.js moves the column. Saved widths are fetched
// once per key and cached, so htmx panel/grid swaps re-apply them without re-fetching.
(function () {
    var meta = document.querySelector('meta[name="csrf-token"]');
    var CSRF = meta ? meta.content : '';

    var cache = {};   // key -> { colKey: px }
    var pending = {}; // key -> debounce timer

    function tableKey(table) {
        var raw = table.getAttribute('aria-label') || table.id || '';
        var slug = raw.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
        return slug ? 'cols:' + slug : null;
    }

    function headerCells(table) {
        return table.tHead && table.tHead.rows.length > 0 ? table.tHead.rows[0].cells : [];
    }

    // Stable identity for a header's width: its data-col-key, or its index for unkeyed tables.
    function colKey(th, index) {
        return th.getAttribute('data-col-key') || String(index);
    }

    function widthFor(widths, th, index) {
        var w = widths[colKey(th, index)];
        // Legacy prefs were keyed purely by index — fall back so saved widths survive the format
        // change (a subsequent resize rewrites them under the col-key).
        return w != null ? w : widths[String(index)];
    }

    function applySaved(table, widths) {
        if (!widths) {
            return;
        }

        var ths = headerCells(table);
        var applied = false;
        for (var i = 0; i < ths.length; i++) {
            var w = widthFor(widths, ths[i], i);
            if (w != null) {
                ths[i].style.width = w + 'px';
                applied = true;
            }
        }

        if (applied) {
            table.style.tableLayout = 'fixed';
            // Fixed layout means an over-wide cell overflows its column — clip+ellipsize so a
            // narrowed column doesn't bleed its content over the next one (see .cols-resized CSS).
            table.classList.add('cols-resized');
        }
    }

    function snapshot(table) {
        var ths = headerCells(table);
        var widths = {};
        for (var i = 0; i < ths.length; i++) {
            widths[colKey(ths[i], i)] = ths[i].offsetWidth;
        }

        return widths;
    }

    function save(key, widths) {
        cache[key] = widths;
        if (pending[key]) {
            clearTimeout(pending[key]);
        }

        pending[key] = setTimeout(function () {
            fetch('/api/v1/preferences/' + encodeURIComponent(key), {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': CSRF },
                body: JSON.stringify(widths),
            });
        }, 500);
    }

    function wire(table, key) {
        // htmx swaps replace the table element, so dataset.resizable won't survive a swap — a
        // re-wire is expected. Re-applying saved widths from cache keeps them across swaps.
        if (table.dataset.resizable === '1') {
            applySaved(table, cache[key]);
            return;
        }

        table.dataset.resizable = '1';
        var ths = headerCells(table);
        for (var i = 0; i < ths.length; i++) {
            addHandle(table, ths, ths[i], i, key);
        }

        applySaved(table, cache[key]);
    }

    function addHandle(table, ths, th, index, key) {
        var handle = document.createElement('span');
        handle.className = 'col-resize-handle';
        // The handle sits above the sort link; swallow its click so a resize never triggers sort.
        handle.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
        });
        handle.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();

            // Freeze every column's current width, then switch to fixed layout so the dragged
            // width actually takes effect (auto layout treats width as a hint only).
            var frozen = snapshot(table);
            for (var j = 0; j < ths.length; j++) {
                ths[j].style.width = frozen[colKey(ths[j], j)] + 'px';
            }

            table.style.tableLayout = 'fixed';
            table.classList.add('cols-resized'); // clip overflowing cells (see .cols-resized CSS)
            document.body.style.userSelect = 'none';

            var startX = e.pageX;
            var startW = frozen[colKey(th, index)];

            function onMove(ev) {
                th.style.width = Math.max(40, startW + (ev.pageX - startX)) + 'px';
            }

            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.userSelect = '';
                save(key, snapshot(table));
            }

            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
        th.appendChild(handle);
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
            .then(function (w) { cache[key] = w || {}; wire(table, key); })
            .catch(function () { cache[key] = {}; wire(table, key); });
    }

    function initAll() {
        document.querySelectorAll('table.data').forEach(initTable);
    }

    initAll();
    // Re-wire tables that htmx swapped in (grid refreshes, panel reloads).
    document.addEventListener('htmx:afterSwap', initAll);
})();
