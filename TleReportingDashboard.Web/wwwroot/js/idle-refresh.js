// Idle auto-refresh helper.
//
// Tracks user activity (mouse, keyboard, scroll, touch) and invokes a
// .NET callback once `idleThresholdMs` passes with no activity. The
// caller is expected to do the actual refresh — this module is just
// the timer + activity listeners. Designed to be started/stopped from
// a Blazor component's lifecycle (OnAfterRenderAsync / Dispose).
//
// Usage from .NET:
//     await JS.InvokeVoidAsync("startIdleWatcher", dotNetRef, 300000); // 5 min
//     // ... later:
//     await JS.InvokeVoidAsync("stopIdleWatcher");
//
// The .NET callback method must be marked [JSInvokable] and named
// "OnIdleRefresh" (no args).
(function () {
    let lastActivityAt = 0;
    let tickHandle = null;
    let dotNetRef = null;
    let thresholdMs = 0;
    // Used to debounce the activity listener. Mousemove fires hundreds of
    // times per second; we only need to bump the timestamp once per
    // animation frame.
    let pendingBump = false;

    const bump = () => {
        lastActivityAt = Date.now();
        pendingBump = false;
    };

    const onActivity = () => {
        if (pendingBump) return;
        pendingBump = true;
        requestAnimationFrame(bump);
    };

    const events = ['mousemove', 'mousedown', 'keydown', 'click', 'scroll', 'touchstart', 'wheel'];

    const tick = () => {
        if (!dotNetRef || !thresholdMs) return;
        const idle = Date.now() - lastActivityAt;
        if (idle >= thresholdMs) {
            // Reset activity timestamp BEFORE invoking so a long-running
            // refresh on the .NET side doesn't immediately re-trigger
            // when the next tick fires.
            lastActivityAt = Date.now();
            // Fire-and-forget. If the circuit is gone we just ignore.
            try {
                dotNetRef.invokeMethodAsync('OnIdleRefresh');
            } catch (e) {
                // Component disposed between tick and invoke. Stop self.
                window.stopIdleWatcher();
            }
        }
    };

    window.startIdleWatcher = function (ref, ms) {
        // Idempotent: stop any prior watcher before starting a new one,
        // so a re-init from the same circuit doesn't pile up listeners.
        window.stopIdleWatcher();
        dotNetRef = ref;
        thresholdMs = ms;
        lastActivityAt = Date.now();
        events.forEach(ev => document.addEventListener(ev, onActivity, { passive: true, capture: true }));
        // Tick every 30s — cheap and gives us at-most ~30s overshoot
        // beyond the configured threshold, which is fine for a 5-minute
        // idle target.
        tickHandle = setInterval(tick, 30000);
    };

    window.stopIdleWatcher = function () {
        if (tickHandle) clearInterval(tickHandle);
        tickHandle = null;
        events.forEach(ev => document.removeEventListener(ev, onActivity, { capture: true }));
        dotNetRef = null;
        thresholdMs = 0;
    };

    // Expose a manual nudge so Blazor-side actions (clicking a tile,
    // running an export) can flag activity even if no DOM event fired
    // through the listener (rare, but defensive).
    window.bumpIdleWatcher = function () {
        lastActivityAt = Date.now();
    };
})();
