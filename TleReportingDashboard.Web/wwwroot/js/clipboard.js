// Cross-context clipboard helper. navigator.clipboard.writeText only
// works in secure contexts (HTTPS or localhost); on plain HTTP origins
// (e.g. an internal "reportingdashboard.local" intranet host)
// navigator.clipboard is undefined and the modern API throws.
//
// Tries the modern path first; falls back to a hidden-textarea +
// document.execCommand('copy') trick that still works in HTTP contexts.
// Returns true on success, false on any failure — callers can use the
// boolean to drive their snackbar message.
window.copyToClipboard = async function (text) {
    if (text === null || text === undefined) text = "";
    if (typeof text !== "string") text = String(text);

    // Modern path — only available on HTTPS / localhost. Some browsers
    // also gate it behind a transient user gesture; the catch below
    // covers that case.
    if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Fall through to the legacy path.
        }
    }

    // Legacy fallback. The textarea must be in the DOM and visible to
    // selection APIs, but positioned off-screen so it doesn't flash.
    // execCommand('copy') is deprecated but every evergreen browser
    // still implements it for compatibility.
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.position = "fixed";
    ta.style.top = "0";
    ta.style.left = "0";
    ta.style.opacity = "0";
    ta.style.pointerEvents = "none";
    document.body.appendChild(ta);
    try {
        ta.focus();
        ta.select();
        ta.setSelectionRange(0, ta.value.length);
        return document.execCommand("copy");
    } catch {
        return false;
    } finally {
        document.body.removeChild(ta);
    }
};
