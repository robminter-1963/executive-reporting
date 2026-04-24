// Dynamically computes the height a MudDataGrid should use to exactly fill
// the space from its top edge to the bottom of the viewport, minus the
// pager footer and a small 10px gap. Called by ReportGrid when FillViewport
// is true and the user hasn't chosen a specific rows-per-page yet.

window.fillViewportGrid = {
    _instances: new Map(),

    // Attaches a resize-aware measurer to the element matching `selector`.
    // Each measure call invokes `dotNetRef.SetFillHeight(px)` with the
    // computed body height. Call `dispose` with the same selector to clean up.
    init: function (selector, dotNetRef) {
        // Iterative approach: after a render cycle, measure where the pager's
        // bottom actually landed. Compute the delta to the target (viewport − 10),
        // and shrink/grow the scroll container by that delta. Converges in one
        // or two cycles regardless of unknown chrome (toolbars, borders, custom
        // theme paddings) — no class-name assumptions beyond finding the pager.
        let lastHeight = 0;
        const compute = () => {
            const el = document.querySelector(selector);
            if (!el) return;

            // Try each known class name the different MudBlazor versions use.
            const scroll = el.querySelector('.mud-table-container, .mud-data-grid-container');
            const pager = el.querySelector(
                '.mud-table-pagination, .mud-data-grid-pagination, .mud-table-pager, .mud-data-grid-paging');
            if (!scroll || !pager) return;

            const pagerBottom = pager.getBoundingClientRect().bottom;
            const target = window.innerHeight - 10;
            const scrollHeight = scroll.getBoundingClientRect().height;

            // delta > 0 means grid ends above target (room to grow)
            // delta < 0 means grid overflows target (must shrink)
            const delta = target - pagerBottom;
            if (Math.abs(delta) < 2) return;  // stable, stop adjusting

            const newHeight = Math.max(120, Math.floor(scrollHeight + delta));
            if (newHeight === lastHeight) return;
            lastHeight = newHeight;
            dotNetRef.invokeMethodAsync('SetFillHeight', newHeight);
        };

        // Belt-and-braces: hide the outer viewport scrollbar while the grid is
        // managing its own internal scroll. Any residual 1-2px overflow from
        // borders/rounding won't cause an outer scrollbar. Restored on dispose.
        const prevHtmlOverflow = document.documentElement.style.overflowY;
        const prevBodyOverflow = document.body.style.overflowY;
        document.documentElement.style.overflowY = 'hidden';
        document.body.style.overflowY = 'hidden';

        // Kick off, then listen for layout events that could shift things.
        compute();
        const onResize = () => compute();
        window.addEventListener('resize', onResize);

        // Watch body-level size changes too (e.g. DevTools, zoom) — plain
        // resize doesn't always fire on zoom changes in Chromium.
        let observer = null;
        if (typeof ResizeObserver !== 'undefined') {
            observer = new ResizeObserver(compute);
            observer.observe(document.body);
        }

        this._instances.set(selector, { onResize, observer, prevHtmlOverflow, prevBodyOverflow });
        return true;
    },

    // Legacy placeholder so the dispose call above still resolves.
    _noop: function () { },

    dispose: function (selector) {
        const entry = this._instances.get(selector);
        if (!entry) return;
        window.removeEventListener('resize', entry.onResize);
        if (entry.observer) entry.observer.disconnect();
        document.documentElement.style.overflowY = entry.prevHtmlOverflow || '';
        document.body.style.overflowY = entry.prevBodyOverflow || '';
        this._instances.delete(selector);
    }
};
