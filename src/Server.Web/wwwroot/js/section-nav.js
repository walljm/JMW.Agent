// Grouped section-nav (tab rail) behavior shared by DeviceDetail and ServiceDetail — tab
// switching, "view all" group stacking, URL sync, ARIA linking, and roving-tabindex keyboard
// navigation. Both pages render the identical .section-nav/.tabpanel markup, so this lives in
// one place instead of being copy-pasted per page.
(function () {
    var nav = document.querySelector('.section-nav');
    if (!nav) return;

    var tabs = Array.prototype.slice.call(nav.querySelectorAll('.section-nav-item'));
    var TABS = tabs.map(function (t) {
        return t.getAttribute('data-tab');
    });

    // Link each tab button to its panel for assistive tech (WAI-ARIA APG tabs pattern) — done
    // here rather than per-panel in Razor so both pages get it from one shared place.
    tabs.forEach(function (tab, i) {
        var id = tab.getAttribute('data-tab');
        var panel = document.querySelector('.tabpanel[data-panel="' + CSS.escape(id) + '"]');
        if (!tab.id) tab.id = 'tab-' + id;
        tab.setAttribute('tabindex', i === 0 ? '0' : '-1');
        if (panel) {
            if (!panel.id) panel.id = 'panel-' + id;
            tab.setAttribute('aria-controls', panel.id);
            panel.setAttribute('aria-labelledby', tab.id);
        }
    });

    // Clears whatever showGroup() left behind (injected headings, group-view layout,
    // active state on the group button) so a normal single-tab switch starts clean.
    function clearGroupView() {
        document.querySelectorAll('.group-label-btn').forEach(function (b) {
            b.classList.remove('active');
        });
        document.querySelectorAll('.tabpanel-heading').forEach(function (h) {
            h.remove();
        });
        var panels = document.querySelector('.section-panels');
        if (panels) panels.classList.remove('group-view');
    }

    function switchTab(target, focusTab) {
        if (TABS.indexOf(target) < 0) target = TABS[0];
        clearGroupView();
        tabs.forEach(function (t) {
            var active = t.getAttribute('data-tab') === target;
            t.classList.toggle('active', active);
            t.setAttribute('aria-selected', active ? 'true' : 'false');
            t.setAttribute('tabindex', active ? '0' : '-1');
            if (active && focusTab) t.focus();
        });
        document.querySelectorAll('.tabpanel').forEach(function (panel) {
            panel.hidden = panel.getAttribute('data-panel') !== target;
        });
        var url = new URL(location.href);
        url.searchParams.set('tab', target);
        history.replaceState(null, '', url);
    }

    tabs.forEach(function (tab) {
        tab.addEventListener('click', function () {
            switchTab(tab.getAttribute('data-tab'));
        });
    });

    // Roving tabindex + automatic activation (WAI-ARIA APG tabs pattern): arrow keys move
    // focus AND switch the panel immediately, matching this rail's existing click-to-switch
    // feel. Home/End jump to the first/last tab.
    nav.addEventListener('keydown', function (e) {
        var current = tabs.findIndex(function (t) {
            return t.getAttribute('tabindex') === '0';
        });
        if (current < 0) return;
        var next;
        if (e.key === 'ArrowDown' || e.key === 'ArrowRight') next = (current + 1) % tabs.length;
        else if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') next = (current - 1 + tabs.length) % tabs.length;
        else if (e.key === 'Home') next = 0;
        else if (e.key === 'End') next = tabs.length - 1;
        else return;
        e.preventDefault();
        switchTab(tabs[next].getAttribute('data-tab'), true);
    });

    // "View all" on a group label shows every section in that group stacked together
    // (scrollable), instead of one tab at a time — for correlating facts across e.g.
    // Interfaces/Ports/Advertised Services/Seen By without clicking back and forth.
    // Panel headings are injected here rather than server-rendered, since they're
    // redundant with (and would duplicate) the nav highlighting in normal single-tab mode.
    function showGroup(btn) {
        var panelIds = btn.getAttribute('data-panels').split(',');
        document.querySelectorAll('.tabpanel-heading').forEach(function (h) {
            h.remove();
        });
        tabs.forEach(function (t) {
            t.classList.remove('active');
            t.setAttribute('aria-selected', 'false');
        });
        document.querySelectorAll('.group-label-btn').forEach(function (b) {
            b.classList.toggle('active', b === btn);
        });
        document.querySelectorAll('.tabpanel').forEach(function (panel) {
            var id = panel.getAttribute('data-panel');
            var show = panelIds.indexOf(id) !== -1;
            panel.hidden = !show;
            if (show) {
                var navItem = nav.querySelector('.section-nav-item[data-tab="' + id + '"]');
                var label = navItem ? navItem.querySelector('.section-nav-label').textContent : id;
                var heading = document.createElement('div');
                heading.className = 'tabpanel-heading';
                heading.textContent = label;
                panel.prepend(heading);
            }
        });
        var sectionPanels = document.querySelector('.section-panels');
        if (sectionPanels) sectionPanels.classList.add('group-view');
        var url = new URL(location.href);
        url.searchParams.set('tab', 'group:' + btn.getAttribute('data-group-label'));
        history.replaceState(null, '', url);
    }

    document.querySelectorAll('.group-label-btn').forEach(function (btn) {
        btn.addEventListener('click', function () {
            showGroup(btn);
        });
    });

    document.addEventListener('DOMContentLoaded', function () {
        if (TABS.length === 0) return;
        // Server picks the landing section by management status; ?tab= overrides it.
        var fallback = nav.getAttribute('data-default') || TABS[0];
        var tabParam = new URLSearchParams(location.search).get('tab');
        if (tabParam && tabParam.indexOf('group:') === 0) {
            var groupBtn = document.querySelector(
                '.group-label-btn[data-group-label="' + CSS.escape(tabParam.slice('group:'.length)) + '"]');
            if (groupBtn) {
                showGroup(groupBtn);
                return;
            }
        }
        switchTab(TABS.indexOf(tabParam) >= 0 ? tabParam : fallback);
    });
})();
