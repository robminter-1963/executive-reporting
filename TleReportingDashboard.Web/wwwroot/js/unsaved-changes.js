// Browser-close / refresh / external-nav guard.
//
// In-app navigation is intercepted on the .NET side via
// NavigationManager.RegisterLocationChangingHandler. This module
// covers the cases the Blazor handler can't reach: closing the tab,
// hitting refresh, typing a new URL, following an external link.
//
// Usage from .NET:
//     await JS.InvokeVoidAsync("setBeforeUnloadGuard", true);  // arm
//     await JS.InvokeVoidAsync("setBeforeUnloadGuard", false); // disarm
//
// The handler stays installed for the page lifetime — cheap, and the
// browser only consults it when an unload is actually happening.
(function () {
    let armed = false;

    const handler = (e) => {
        if (!armed) return;
        // Modern browsers ignore the actual string and show their
        // own canned message, but the spec still requires both
        // returnValue + preventDefault for the prompt to fire.
        e.preventDefault();
        e.returnValue = '';
        return '';
    };

    window.addEventListener('beforeunload', handler);

    window.setBeforeUnloadGuard = function (on) {
        armed = !!on;
    };
})();
