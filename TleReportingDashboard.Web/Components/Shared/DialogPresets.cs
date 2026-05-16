using MudBlazor;

namespace TleReportingDashboard.Web.Components.Shared;

// Single source of truth for the DialogOptions shapes we use across
// the app. Replaces ~30 inline `new DialogOptions { ... }` literals
// that had to remember to set BackdropClick = false + CloseOnEscapeKey
// = false for every edit-state dialog — historically the source of
// "Esc dismissed my dialog without prompting" bugs.
//
// Usage:
//     var dlg = await DialogService.ShowAsync<MyDialog>(title, parameters,
//         DialogPresets.Locked(MaxWidth.Small));
//
// "Locked" means the dialog must be dismissed via an explicit button —
// no backdrop click, no Esc. Required for any dialog that wraps its
// cancel path through UnsavedChangesPrompt.TryCancelAsync (otherwise
// MudBlazor's default dismiss path bypasses the dirty-check prompt).
public static class DialogPresets
{
    // The canonical "locked, full-width, no-default-dismiss" shape.
    // Every edit dialog in the app should use this. MaxWidth varies by
    // dialog content: ExtraSmall for single-input dialogs (Rename),
    // Small for typical forms, Medium for multi-field editors, Large
    // for the wide ones (CalcColumnDialog, JoinEditDialog).
    public static DialogOptions Locked(MaxWidth maxWidth = MaxWidth.Small) => new()
    {
        MaxWidth = maxWidth,
        FullWidth = true,
        BackdropClick = false,
        CloseOnEscapeKey = false,
    };

    // Same as Locked but adds a top-right "X" close button. The X click
    // routes through the dialog's own cancel logic — typically the
    // dialog's TryCancelAsync — so unsaved-changes prompts still fire
    // on dismiss. Useful for dialogs that benefit from a visible exit
    // affordance (long forms, scrollable content).
    public static DialogOptions LockedWithCloseButton(MaxWidth maxWidth = MaxWidth.Small) => new()
    {
        MaxWidth = maxWidth,
        FullWidth = true,
        BackdropClick = false,
        CloseOnEscapeKey = false,
        CloseButton = true,
    };
}
