// filter-bar.js — GitLab-style token/chip filter bar.
// Auto-initialises every .filter-bar element found in the DOM.
// Requires htmx to be loaded (uses htmx.ajax for live swaps).

(function () {
    'use strict';

    const Mode = {HIDDEN: 0, KEYS: 1, VALUES: 2};

    // ── DOM helpers ────────────────────────────────────────────────────────────

    function el(tag, cls) {
        const node = document.createElement(tag);
        if (cls) node.className = cls;
        return node;
    }

    function span(cls, text) {
        const node = el('span', cls);
        node.textContent = text;
        return node;
    }

    // ── FilterBar class ────────────────────────────────────────────────────────

    class FilterBar {
        constructor(root) {
            this.el = root;
            this.chipsEl = root.querySelector('.filter-chips');
            this.inputEl = root.querySelector('.filter-input');
            this.dropEl = root.querySelector('.filter-dropdown');
            this.listEl = root.querySelector('.filter-dropdown-list');

            this.fragmentUrl = root.dataset.fragmentUrl || '';
            this.htmxTarget = root.dataset.htmxTarget || '';
            this.pageUrl = root.dataset.pageUrl || '';
            this.filterSpecs = JSON.parse(root.dataset.filters || '[]');

            // Restore server-rendered active state
            this.activeFilters = new Map();
            const saved = JSON.parse(root.dataset.activeFilters || '{}');
            for (const [k, v] of Object.entries(saved)) this.activeFilters.set(k, v);
            this.q = root.dataset.q || '';

            this.mode = Mode.HIDDEN;
            this.activeKey = null;
            this.highlighted = -1;

            this._renderChips();
            this._bind();
        }

        // ── Chips ──────────────────────────────────────────────────────────────

        _renderChips() {
            this.chipsEl.replaceChildren();
            for (const [key, value] of this.activeFilters) {
                const spec = this.filterSpecs.find(f => f.key === key);
                const keyLabel = spec ? spec.label : key;
                const valLabel = spec
                    ? (spec.values.find(v => v.value === value)?.label ?? value)
                    : value;
                this.chipsEl.appendChild(this._chip(key, keyLabel, valLabel, false));
            }
            if (this.q) this.chipsEl.appendChild(this._chip('q', 'Search', this.q, true));
        }

        _chip(key, keyLabel, valueLabel, isQ) {
            const chip = el('span', 'filter-chip' + (isQ ? ' chip-q' : ''));
            chip.setAttribute('role', 'listitem');
            chip.dataset.key = key;

            const btn = el('button');
            btn.type = 'button';
            btn.setAttribute('aria-label', 'Remove ' + keyLabel + ' filter');
            btn.textContent = '×';
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                this._removeFilter(key);
            });

            chip.append(span('chip-label', keyLabel + ':'), span('chip-value', valueLabel), btn);
            return chip;
        }

        // ── Filter state ───────────────────────────────────────────────────────

        _addFilter(key, value) {
            if (key === 'q') {
                this.q = value;
            } else {
                this.activeFilters.set(key, value);
            }
            this.inputEl.value = '';
            this.activeKey = null;
            this._renderChips();
            this._hideDropdown();
            this._swap();
        }

        _removeFilter(key) {
            if (key === 'q') {
                this.q = '';
            } else {
                this.activeFilters.delete(key);
            }
            this._renderChips();
            this._swap();
        }

        // ── HTMX swap ──────────────────────────────────────────────────────────

        _swap() {
            // Cursor is intentionally omitted — any filter change resets to page 1
            const parts = [];
            for (const [k, v] of this.activeFilters)
                parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
            if (this.q) parts.push('q=' + encodeURIComponent(this.q));
            const url = parts.length ? this.fragmentUrl + '&' + parts.join('&') : this.fragmentUrl;
            htmx.ajax('GET', url, {target: this.htmxTarget, swap: 'outerHTML'});
        }

        // ── Dropdown ───────────────────────────────────────────────────────────

        _showKeys(prefix) {
            const pl = prefix.toLowerCase();
            const matches = this.filterSpecs.filter(f =>
                !this.activeFilters.has(f.key) &&
                (pl === '' || f.label.toLowerCase().startsWith(pl) || f.key.toLowerCase().startsWith(pl))
            );
            if (matches.length === 0) {
                this._hideDropdown();
                return;
            }

            this.mode = Mode.KEYS;
            this.highlighted = -1;
            this.listEl.replaceChildren();

            if (pl === '') {
                const section = el('li', 'filter-dropdown-section');
                section.textContent = 'Filter by';
                this.listEl.appendChild(section);
            }

            matches.forEach(spec => {
                const li = el('li', 'filter-dropdown-item');
                li.setAttribute('role', 'option');
                li.append(span('item-label', spec.label), span('item-meta', spec.key + ':'));
                li.addEventListener('mousedown', (e) => {
                    e.preventDefault();
                    this._selectKey(spec);
                });
                this.listEl.appendChild(li);
            });

            this.dropEl.hidden = false;
        }

        _showValues(spec, prefix) {
            const pl = prefix.toLowerCase();
            const matches = spec.values.filter(v =>
                pl === '' || v.label.toLowerCase().startsWith(pl) || v.value.toLowerCase().startsWith(pl)
            );
            if (matches.length === 0) {
                this._hideDropdown();
                return;
            }

            this.mode = Mode.VALUES;
            this.activeKey = spec.key;
            this.highlighted = -1;
            this.listEl.replaceChildren();

            const section = el('li', 'filter-dropdown-section');
            section.textContent = spec.label;
            this.listEl.appendChild(section);

            matches.forEach(val => {
                const li = el('li', 'filter-dropdown-item');
                li.setAttribute('role', 'option');
                li.appendChild(span('item-label', val.label));
                li.addEventListener('mousedown', (e) => {
                    e.preventDefault();
                    this._selectValue(spec, val.value);
                });
                this.listEl.appendChild(li);
            });

            this.dropEl.hidden = false;
        }

        _hideDropdown() {
            this.dropEl.hidden = true;
            this.mode = Mode.HIDDEN;
            this.highlighted = -1;
        }

        _selectKey(spec) {
            this.inputEl.value = spec.key + ':';
            this.activeKey = spec.key;
            this._showValues(spec, '');
            this.inputEl.focus();
        }

        _selectValue(spec, value) {
            this._addFilter(spec.key, value);
            this.inputEl.focus();
        }

        _highlight(delta) {
            const items = Array.from(this.listEl.querySelectorAll('.filter-dropdown-item'));
            if (items.length === 0) return;
            if (this.highlighted >= 0 && this.highlighted < items.length)
                items[this.highlighted].removeAttribute('aria-selected');
            this.highlighted = Math.max(0, Math.min(items.length - 1, this.highlighted + delta));
            items[this.highlighted].setAttribute('aria-selected', 'true');
            items[this.highlighted].scrollIntoView({block: 'nearest'});
        }

        _activateHighlighted() {
            const items = Array.from(this.listEl.querySelectorAll('.filter-dropdown-item'));
            if (this.highlighted >= 0 && this.highlighted < items.length)
                items[this.highlighted].dispatchEvent(new MouseEvent('mousedown', {bubbles: true}));
        }

        // ── Event handling ─────────────────────────────────────────────────────

        _onInput() {
            const raw = this.inputEl.value;
            const colonAt = raw.indexOf(':');

            if (colonAt > 0) {
                const keyPart = raw.slice(0, colonAt).trim().toLowerCase();
                const valPart = raw.slice(colonAt + 1).trim();
                const spec = this.filterSpecs.find(
                    f => f.key.toLowerCase() === keyPart || f.label.toLowerCase() === keyPart
                );
                if (spec) {
                    this._showValues(spec, valPart);
                    return;
                }
                this._hideDropdown();
                return;
            }

            this._showKeys(raw);
        }

        _onKeydown(e) {
            switch (e.key) {
                case 'ArrowDown':
                    e.preventDefault();
                    if (this.mode === Mode.HIDDEN) this._showKeys('');
                    else this._highlight(1);
                    break;
                case 'ArrowUp':
                    e.preventDefault();
                    this._highlight(-1);
                    break;
                case 'Enter':
                    e.preventDefault();
                    if (this.mode !== Mode.HIDDEN && this.highlighted >= 0) {
                        this._activateHighlighted();
                    } else {
                        const text = this.inputEl.value.trim();
                        if (text && !text.includes(':')) this._addFilter('q', text);
                    }
                    break;
                case 'Escape':
                    if (this.mode !== Mode.HIDDEN) this._hideDropdown();
                    else this.inputEl.value = '';
                    break;
                case 'Backspace':
                    if (this.inputEl.value === '') {
                        const keys = [...this.activeFilters.keys()];
                        const lastKey = keys.length > 0 ? keys[keys.length - 1] : (this.q ? 'q' : null);
                        if (lastKey) this._removeFilter(lastKey);
                    }
                    break;
                case 'Tab':
                    if (this.mode !== Mode.HIDDEN) {
                        e.preventDefault();
                        if (this.highlighted >= 0) this._activateHighlighted();
                        else this._highlight(1);
                    }
                    break;
            }
        }

        _bind() {
            this.el.addEventListener('click', () => this.inputEl.focus());
            this.inputEl.addEventListener('input', () => this._onInput());
            this.inputEl.addEventListener('keydown', (e) => this._onKeydown(e));
            this.inputEl.addEventListener('focus', () => {
                if (this.inputEl.value === '' && this.filterSpecs.length > 0) this._showKeys('');
            });
            // Delay hide so mousedown on dropdown items fires first
            this.inputEl.addEventListener('blur', () => setTimeout(() => this._hideDropdown(), 150));
        }
    }

    // ── Auto-init ──────────────────────────────────────────────────────────────

    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('.filter-bar').forEach(root => new FilterBar(root));
    });

})();
