// grid-infinite-scroll.js — auto-loads the next keyset page when the user scrolls to the end
// of a data grid, APPENDING rows instead of replacing the table.
//
// Progressive enhancement over the "Next" pagination link, which stays in the DOM as the
// cursor source (and the no-JS fallback) but is hidden while this is active. Reuses the
// existing fragment endpoint: the Next href + "&fragment=1" returns just the grid panel.
//
// Works for both layouts: region-scrolled grids (.data-grid-page, where .grid-scroll scrolls
// internally with the pager pinned below it) and page-scrolled stacked tables. An
// IntersectionObserver against the viewport reports the sentinel as visible only when it is
// actually on screen, correctly accounting for the .grid-scroll clipping in the first case.
(function () {
    'use strict';

    // Prefetch a little before the very bottom so scrolling stays continuous.
    var ROOT_MARGIN = '600px';

    function nextLinkIn(panel) {
        return panel.querySelector('.pagination a.js-grid-next');
    }

    function init(panel) {
        if (!panel || !panel.classList || !panel.classList.contains('grid-panel')) {
            return;
        }
        if (panel.dataset.infiniteScroll === 'on') {
            return; // already wired
        }

        var scroll = panel.querySelector('.grid-scroll');
        var tbody = scroll ? scroll.querySelector('table tbody') : null;
        if (!scroll || !tbody || !nextLinkIn(panel)) {
            return; // not a paginated grid, or nothing more to load
        }

        panel.dataset.infiniteScroll = 'on';
        panel.classList.add('infinite-on'); // hides the manual pager via CSS

        var sentinel = document.createElement('div');
        sentinel.className = 'grid-infinite-sentinel';
        sentinel.setAttribute('aria-hidden', 'true');
        scroll.appendChild(sentinel);

        var loading = false;
        var done = false;

        var observer = new IntersectionObserver(function (entries) {
            for (var i = 0; i < entries.length; i++) {
                if (entries[i].isIntersecting) {
                    loadMore();
                    break;
                }
            }
        }, { rootMargin: ROOT_MARGIN });
        observer.observe(sentinel);

        function stop() {
            done = true;
            observer.disconnect();
            sentinel.remove();
        }

        function isOnScreen(elem) {
            var r = elem.getBoundingClientRect();
            var vh = window.innerHeight || document.documentElement.clientHeight;
            return r.top < vh && r.bottom > 0;
        }

        async function loadMore() {
            if (loading || done) {
                return;
            }
            var link = nextLinkIn(panel);
            if (!link) {
                stop();
                return;
            }

            loading = true;
            try {
                var href = link.getAttribute('href');
                var url = href + (href.indexOf('?') >= 0 ? '&' : '?') + 'fragment=1';
                var resp = await fetch(url, { headers: { 'X-Requested-With': 'fetch' } });
                if (!resp.ok) {
                    return; // transient — a later scroll retries
                }

                var doc = new DOMParser().parseFromString(await resp.text(), 'text/html');
                // Scope to the matching panel so multi-table pages don't cross-append.
                var srcPanel = (panel.id && doc.getElementById(panel.id)) || doc.querySelector('.grid-panel');
                if (!srcPanel) {
                    return;
                }

                var frag = document.createDocumentFragment();
                srcPanel.querySelectorAll('.grid-scroll table tbody > tr').forEach(function (r) {
                    frag.appendChild(document.importNode(r, true));
                });
                tbody.appendChild(frag);

                // Advance the cursor: swap in the fetched pager (fresh Next href, or none).
                var oldPager = panel.querySelector('.pagination');
                var newPager = srcPanel.querySelector('.pagination');
                if (oldPager && newPager) {
                    oldPager.replaceWith(newPager);
                } else if (oldPager) {
                    oldPager.remove();
                }
            } catch (e) {
                // Swallow — the next scroll will retry.
            } finally {
                loading = false;
            }

            if (!nextLinkIn(panel)) {
                stop();
                return;
            }
            // Short page that didn't push the sentinel out of view: keep filling.
            requestAnimationFrame(function () {
                if (!done && isOnScreen(sentinel)) {
                    loadMore();
                }
            });
        }
    }

    function initAll() {
        document.querySelectorAll('.grid-panel').forEach(init);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll);
    } else {
        initAll();
    }

    // filter-bar.js swaps the whole .grid-panel (outerHTML) on filter/sort — wire the new one.
    // init() is idempotent (dataset guard), so a broad re-scan is safe and cheap.
    document.body.addEventListener('htmx:afterSwap', initAll);
})();
