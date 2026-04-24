window.initColumnResize = function (gridSelector, dotNetRef) {
    const grid = document.querySelector(gridSelector);
    if (!grid) return;

    // Get only data column headers (exclude filler)
    const allHeaders = Array.from(grid.querySelectorAll('th'));
    const dataHeaders = allHeaders.filter(function (th) {
        return !th.classList.contains('grid-filler');
    });

    dataHeaders.forEach(function (th, dataIndex) {
        if (th.querySelector('.col-resize-handle')) return;

        var handle = document.createElement('div');
        handle.className = 'col-resize-handle';
        th.appendChild(handle);

        var startX, startWidth;
        // DOM index for matching td cells
        var domIndex = allHeaders.indexOf(th);

        handle.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();
            startX = e.pageX;
            startWidth = th.offsetWidth;
            handle.classList.add('active');

            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';

            function onMouseMove(e) {
                // Only set width / max-width. Leave min-width alone — the inline min-width
                // set from C# (label-aware) acts as the hard floor so the column can't be
                // shrunk below its header text.
                var newWidth = Math.max(30, startWidth + (e.pageX - startX));
                th.style.setProperty('width', newWidth + 'px', 'important');
                th.style.setProperty('max-width', newWidth + 'px', 'important');

                var rows = grid.querySelectorAll('tbody tr');
                rows.forEach(function (row) {
                    var td = row.children[domIndex];
                    if (td) {
                        td.style.setProperty('width', newWidth + 'px', 'important');
                        td.style.setProperty('max-width', newWidth + 'px', 'important');
                    }
                });
            }

            function onMouseUp() {
                handle.classList.remove('active');
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);

                // Notify Blazor with the data column index (not DOM index)
                if (dotNetRef) {
                    var finalWidth = th.offsetWidth;
                    dotNetRef.invokeMethodAsync('OnColumnResized', dataIndex, finalWidth);
                }
            }

            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
        });
    });
};

window.downloadFile = function (fileName, base64Data, mimeType) {
    const byteCharacters = atob(base64Data);
    const byteNumbers = new Array(byteCharacters.length);
    for (let i = 0; i < byteCharacters.length; i++) {
        byteNumbers[i] = byteCharacters.charCodeAt(i);
    }
    const byteArray = new Uint8Array(byteNumbers);
    const blob = new Blob([byteArray], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
};

window.exportToCsv = function (filename, columns, rows) {
    let csv = columns.map(function (c) {
        return '"' + c.label.replace(/"/g, '""') + '"';
    }).join(',') + '\n';

    rows.forEach(function (row) {
        csv += columns.map(function (c) {
            let val = row[c.fieldId];
            if (val === null || val === undefined) return '';
            let str = String(val);
            if (str.includes(',') || str.includes('"') || str.includes('\n')) {
                str = '"' + str.replace(/"/g, '""') + '"';
            }
            return str;
        }).join(',') + '\n';
    });

    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
};

// ── Master Dashboard Tile Resize ──
window.initTileResize = function (tileId, dotNetRef, gridColumns) {
    var tileEl = document.getElementById('master-tile-' + tileId);
    if (!tileEl) return;
    var handle = tileEl.querySelector('.tile-resize-handle');
    if (!handle || handle.dataset.resizeWired === '1') return;
    handle.dataset.resizeWired = '1';

    var startX, startY, startW, startH, gridEl, colW;

    var colUnit = 60; // pixels per column unit

    handle.addEventListener('mousedown', function (e) {
        e.preventDefault();
        e.stopPropagation();
        startX = e.clientX;
        startY = e.clientY;
        startW = tileEl.offsetWidth;
        startH = tileEl.offsetHeight;

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
        tileEl.classList.add('tile-resizing');
    });

    function onMove(e) {
        var dx = e.clientX - startX;
        var dy = e.clientY - startY;

        // Snap width in 4px increments
        var rawW = startW + dx;
        var snappedW = Math.max(colUnit * 4, Math.round(rawW / 4) * 4);

        // Snap height in 4px increments
        var rawH = startH + dy;
        var snappedH = Math.max(200, Math.round(rawH / 4) * 4);

        tileEl.style.width = snappedW + 'px';
        tileEl.style.height = snappedH + 'px';

        // Convert pixel width back to column units for storage
        tileEl.dataset.cols = Math.max(4, Math.round(snappedW / colUnit));
        tileEl.dataset.height = snappedH;
    }

    function onUp() {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        tileEl.classList.remove('tile-resizing');

        var cols = parseInt(tileEl.dataset.cols) || 12;
        var h = parseInt(tileEl.dataset.height) || 500;
        dotNetRef.invokeMethodAsync('OnTileResized', tileId, cols, h);
    }
};

// ── Truncation tooltips ─────────────────────────────────────────────────
// For every <td> under the given selector, set title=textContent when the
// content is actually clipped (scrollWidth > clientWidth), clear it otherwise.
// Called after render by MasterDashboard so only truncated cells get a tooltip.
window.applyCellTooltips = function (selector) {
    var roots = document.querySelectorAll(selector);
    if (!roots || roots.length === 0) return;
    roots.forEach(function (root) {
        var cells = root.querySelectorAll('tbody td');
        cells.forEach(function (td) {
            // Measure the cell's content: check both the td and any single child
            // wrapper (MudText renders as <p>/<span>) so we catch either source of overflow.
            var inner = td.firstElementChild;
            var overflowing =
                td.scrollWidth > td.clientWidth ||
                (inner && inner.scrollWidth > inner.clientWidth);
            if (overflowing) {
                var text = (td.textContent || '').trim();
                if (text) td.setAttribute('title', text);
                else td.removeAttribute('title');
            } else {
                td.removeAttribute('title');
            }
        });
    });
};

// ── Chart legend + tooltip cleanup ──────────────────────────────────────
// Hides MudBlazor's built-in chart legend inside master-dashboard tiles.
// The chart data is readable from the summary table; the legend just clutters
// the compact tile view.
window.hideChartLegends = function (selector) {
    document.querySelectorAll(selector + ' .mud-chart-legend').forEach(function (el) {
        el.style.display = 'none';
    });
};
