// Delegated click handling for clickable table rows (<tr data-href="...">).
// Supports normal click (navigate in place), ctrl/cmd/shift+click and middle-click
// (open in a new tab), matching how a real <a> element behaves.
(function () {
    function rowFor(target) {
        return target.closest('tr[data-href]');
    }

    document.addEventListener('click', function (e) {
        // Real links inside a row (e.g. audit log target links) keep native behavior.
        if (e.target.closest('a')) {
            return;
        }

        var row = rowFor(e.target);
        if (!row) {
            return;
        }

        var href = row.dataset.href;
        if (e.ctrlKey || e.metaKey || e.shiftKey) {
            window.open(href, '_blank');
        } else {
            window.location.href = href;
        }
    });

    // Middle-click fires 'auxclick', not 'click', in every modern browser.
    document.addEventListener('auxclick', function (e) {
        if (e.button !== 1) {
            return;
        }

        var row = rowFor(e.target);
        if (!row) {
            return;
        }

        e.preventDefault();
        window.open(row.dataset.href, '_blank');
    });
})();
