// Scroll-to-selected for MudBlazor popovers.
//
// MudSelect / MudAutocomplete render their item list inside a popover
// that, by default, always opens scrolled to the top — so on a list with
// 200 currencies and "USD" already chosen, the user has to scroll to see
// their current pick. This module watches every popover for the
// transition into the open state, then nudges the inner list so the
// selected item is visible. Only the list's own scroller is moved; the
// page itself never jumps.
(function () {
    const SELECTED_SELECTOR = [
        '.mud-list-item.mud-selected-item',
        '.mud-list-item-clickable.mud-selected-item',
        '.mud-list-item[aria-selected="true"]'
    ].join(',');

    // Walk up from the selected item until we find an element with its
    // own vertical scrollbar. scrollIntoView() bubbles past these into
    // the window if the popover is sized to its content, which is the
    // wrong behavior — we want only the inner list to move.
    const findScrollContainer = (el) => {
        let p = el.parentElement;
        while (p) {
            const style = getComputedStyle(p);
            const oy = style.overflowY;
            if ((oy === 'auto' || oy === 'scroll') && p.scrollHeight > p.clientHeight) {
                return p;
            }
            p = p.parentElement;
        }
        return null;
    };

    const scrollSelectedIntoView = (popover) => {
        // Defer one frame: MudBlazor adds mud-popover-open before its
        // list items mount, so a synchronous query runs against an
        // empty popover. rAF lets the items render first.
        requestAnimationFrame(() => {
            const selected = popover.querySelector(SELECTED_SELECTOR);
            if (!selected) return;
            const scroller = findScrollContainer(selected);
            if (!scroller) {
                // No inner scroller — list fits in the popover, nothing
                // to scroll. Bail rather than letting scrollIntoView
                // walk up and yank the page.
                return;
            }
            const sRect = scroller.getBoundingClientRect();
            const iRect = selected.getBoundingClientRect();
            // Only act when the item isn't already visible — otherwise
            // we'd re-center already-visible picks for no reason.
            if (iRect.top >= sRect.top && iRect.bottom <= sRect.bottom) return;
            // Center the selection in the visible area.
            const offsetWithin = selected.offsetTop - scroller.offsetTop;
            scroller.scrollTop = offsetWithin - (scroller.clientHeight - selected.clientHeight) / 2;
        });
    };

    const observer = new MutationObserver((mutations) => {
        for (const m of mutations) {
            if (m.type !== 'attributes' || m.attributeName !== 'class') continue;
            const t = m.target;
            if (!(t instanceof HTMLElement)) continue;
            if (!t.classList.contains('mud-popover')) continue;
            // Only fire on the open transition, not every class change
            // (MudBlazor adjusts popover classes during drag/resize too).
            const isOpenNow = t.classList.contains('mud-popover-open');
            const wasOpen = (m.oldValue || '').split(/\s+/).includes('mud-popover-open');
            if (!isOpenNow || wasOpen) continue;
            scrollSelectedIntoView(t);
        }
    });

    const start = () => {
        observer.observe(document.body, {
            subtree: true,
            attributes: true,
            attributeFilter: ['class'],
            attributeOldValue: true
        });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
