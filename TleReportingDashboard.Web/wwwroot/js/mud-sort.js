// Simulates MudDataGrid header clicks to apply a saved multi-sort on load.
// Clicking the headers via DOM events exercises MudDataGrid's real internal
// state machinery, so the priority badges ("1", "2") render correctly —
// something none of the SetSortAsync / direct-SortDefinitions approaches
// reliably produce when called from Blazor's OnAfterRenderAsync.

window.mudGridSort = {
    // specs: array of { label, direction }  (direction = "asc" | "desc")
    // Dispatches a click on each matching header element, Ctrl-modified for
    // every spec after the first so MudDataGrid builds its multi-sort stack.
    applyInitial: function (gridSelector, specs) {
        if (!Array.isArray(specs) || specs.length === 0) return false;

        // Wait for the grid's header cells to exist. Blazor may still be
        // settling the DOM when the C# side invokes us, so poll briefly.
        const start = Date.now();
        const waitForHeaders = () => new Promise(resolve => {
            const tick = () => {
                const host = document.querySelector(gridSelector);
                if (host) {
                    const cells = host.querySelectorAll('th.mud-table-cell');
                    if (cells.length > 0) { resolve(cells); return; }
                }
                if (Date.now() - start > 2000) { resolve(null); return; }
                setTimeout(tick, 50);
            };
            tick();
        });

        return waitForHeaders().then(cells => {
            if (!cells) return false;

            const findCellByTitle = (label) => {
                for (const th of cells) {
                    // MudDataGrid renders the column title inside a span; the
                    // th's trimmed text is the column label we set in C#.
                    const text = (th.innerText || th.textContent || '').trim();
                    if (text === label) return th;
                }
                return null;
            };

            // Cycle is: none -> asc -> desc -> none. So asc needs 1 click,
            // desc needs 2 clicks from a clean state.
            for (let i = 0; i < specs.length; i++) {
                const spec = specs[i];
                const th = findCellByTitle(spec.label);
                if (!th) continue;

                // Click the sortable element inside (MudDataGrid puts the
                // click handler on an inner button/span in recent versions).
                const target = th.querySelector('.mud-clickable, button, span.mud-button-root') || th;
                const ctrl = i > 0;  // modifier for everything after the primary

                target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: ctrl }));
                if (spec.direction === 'desc') {
                    target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: ctrl }));
                }
            }
            return true;
        });
    },

    // Installs a capturing click listener on the grid's header row that
    // swallows user clicks before MudDataGrid's sort handler can see them.
    // Used by the Report Viewer where the sort is locked to what was saved
    // with the report. Capturing phase + stopImmediatePropagation means the
    // event never reaches any MudDataGrid-registered listener, regardless
    // of how deeply it's wired internally.
    lockHeaders: function (gridSelector) {
        const start = Date.now();
        const tick = () => {
            const host = document.querySelector(gridSelector);
            const thead = host ? host.querySelector('thead') : null;
            if (thead) {
                if (!thead.__mudLockInstalled) {
                    const blocker = (e) => {
                        e.stopImmediatePropagation();
                        e.preventDefault();
                    };
                    thead.addEventListener('click', blocker, true);
                    thead.addEventListener('mousedown', blocker, true);
                    thead.addEventListener('pointerdown', blocker, true);
                    thead.__mudLockInstalled = true;
                }
                return;
            }
            if (Date.now() - start > 3000) return;
            setTimeout(tick, 100);
        };
        tick();
    }
};
