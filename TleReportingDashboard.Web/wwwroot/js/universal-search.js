// Universal-search keybinding helper.
//
// Cmd/Ctrl-K opens the search palette. The .NET callback owns the open
// state — this module is just the global keydown listener. Suppressed
// when an input/textarea/contentEditable element has focus so the
// shortcut doesn't fight the user typing into a real form field.
//
// Usage from .NET:
//     await JS.InvokeVoidAsync("startUniversalSearchHotkey", dotNetRef);
//     // ... later (component disposing):
//     await JS.InvokeVoidAsync("stopUniversalSearchHotkey");
//
// The .NET callback method must be marked [JSInvokable] and named
// "OpenUniversalSearch" (no args).
(function () {
    let dotNetRef = null;

    const isTypingTarget = (el) => {
        if (!el) return false;
        const tag = (el.tagName || '').toUpperCase();
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
        if (el.isContentEditable) return true;
        return false;
    };

    const onKeyDown = (e) => {
        if (!dotNetRef) return;
        const key = (e.key || '').toLowerCase();
        if (key !== 'k') return;
        if (!(e.metaKey || e.ctrlKey)) return;
        // When the user is typing into a real text field we let the
        // browser's native behavior win (some textareas use Ctrl-K too).
        // The palette has its own input that re-binds Esc to close.
        if (isTypingTarget(document.activeElement)) return;
        e.preventDefault();
        try {
            dotNetRef.invokeMethodAsync('OpenUniversalSearch');
        } catch (err) {
            // Circuit gone — clean up so we don't keep firing.
            window.stopUniversalSearchHotkey();
        }
    };

    window.startUniversalSearchHotkey = function (ref) {
        // Idempotent: drop any prior ref first so a re-mount doesn't
        // pile up listeners (capture-phase listener, would otherwise
        // double-fire).
        window.stopUniversalSearchHotkey();
        dotNetRef = ref;
        document.addEventListener('keydown', onKeyDown, { capture: true });
    };

    window.stopUniversalSearchHotkey = function () {
        document.removeEventListener('keydown', onKeyDown, { capture: true });
        dotNetRef = null;
    };

    // Focus helper for the palette's search input. Called from
    // OnAfterRenderAsync after the dialog mounts so the cursor lands
    // in the field without an extra click.
    window.focusUniversalSearchInput = function () {
        const el = document.getElementById('universal-search-input');
        if (el) el.focus();
    };
})();
