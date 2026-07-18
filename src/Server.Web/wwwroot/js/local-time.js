// Localizes server-rendered UTC timestamps to the viewer's browser timezone.
// Server markup is authored as <time data-utc="2024-01-01T12:00:00.000Z">2024-01-01 12:00:00 UTC</time>
// (see ViewFormat.IsoUtc) so the page is still correct — just UTC — with JS disabled.
(function () {
    function pad(n) {
        return String(n).padStart(2, '0');
    }

    function format(iso, dateOnly) {
        var d = new Date(iso);
        if (isNaN(d.getTime())) {
            return null;
        }

        var formatted = d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
        if (!dateOnly) {
            formatted += ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
        }

        return formatted;
    }

    function localize(el) {
        var formatted = format(el.dataset.utc, el.dataset.utcFormat === 'date');
        if (formatted === null) {
            return;
        }

        el.textContent = formatted;
        el.title = el.dataset.utc;
    }

    // For elements whose visible text stays as-is (e.g. audit log's "5m ago" relative
    // time) but whose hover tooltip should show a localized absolute time instead of
    // a raw UTC ISO string.
    function localizeTitle(el) {
        var formatted = format(el.dataset.utcTitle, false);
        if (formatted !== null) {
            el.title = formatted + ' local';
        }
    }

    function localizeTree(root) {
        root.querySelectorAll('time[data-utc]').forEach(localize);
        root.querySelectorAll('[data-utc-title]').forEach(localizeTitle);
    }

    // Initial full-page render.
    localizeTree(document);

    // htmx swaps replace DOM subtrees with fresh server HTML (rendered in UTC) after this
    // script's initial pass already ran — without re-localizing, every panel refresh (e.g.
    // Recent Activity's 30s poll) reverts its timestamps to UTC. Re-localizing is idempotent:
    // the data-utc/data-utc-title attributes are preserved, so re-running just recomputes the
    // same local value. Listen on document so it fires regardless of swap target or style.
    document.addEventListener('htmx:afterSwap', function () {
        localizeTree(document);
    });
})();
