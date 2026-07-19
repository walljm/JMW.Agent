// Drag-to-resize data-table columns, persisted per user (server-side, so widths follow the user
// across browsers/devices — not localStorage). A table stays in its natural auto layout until the
// user actually resizes it (or has saved widths), so untouched tables look exactly as before.
//
// Width state is stored as { columnIndex: pixels } under the preference key "cols:<table-slug>",
// where the slug derives from the table's aria-label (or id). Saved widths are fetched once per
// key and cached, so htmx panel/grid swaps re-apply them without re-fetching.
(function () {
    var meta = document.querySelector('meta[name="csrf-token"]');
    var CSRF = meta ? meta.content : '';

    var cache = {};   // key -> { index: px }
    var pending = {}; // key -> debounce timer

    function tableKey(table) {
        var raw = table.getAttribute('aria-label') || table.id || '';
        var slug = raw.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
        return slug ? 'cols:' + slug : null;
    }

    function headerCells(table) {
        return table.tHead && table.tHead.rows.length > 0 ? table.tHead.rows[0].cells : [];
    }

    function applySaved(table, widths) {
        if (!widths) {
            return;
        }

        var ths = headerCells(table);
        var applied = false;
        for (var i = 0; i < ths.length; i++) {
            if (widths[i] != null) {
                ths[i].style.width = widths[i] + 'px';
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
            widths[i] = ths[i].offsetWidth;
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
                ths[j].style.width = frozen[j] + 'px';
            }

            table.style.tableLayout = 'fixed';
            table.classList.add('cols-resized'); // clip overflowing cells (see .cols-resized CSS)
            document.body.style.userSelect = 'none';

            var startX = e.pageX;
            var startW = frozen[index];

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
