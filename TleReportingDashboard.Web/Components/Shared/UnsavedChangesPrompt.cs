using MudBlazor;

namespace TleReportingDashboard.Web.Components.Shared;

// Three-way result returned by UnsavedChangesDialog. Save and DontSave
// both mean "leave"; Cancel keeps the user where they were.
public enum UnsavedChangesResult
{
    Cancel = 0,
    Save,
    DontSave,
}

// Thin wrapper that opens UnsavedChangesDialog and unwraps the result
// into the typed enum. Callers don't need to remember the dialog name
// or DialogResult shape; this is the single seam every dirty-tracked
// dialog/page uses.
//
// Usage:
//     var choice = await UnsavedChangesPrompt.AskAsync(DialogService);
//     switch (choice)
//     {
//         case UnsavedChangesResult.Save:     await SaveAsync(); ...; break;
//         case UnsavedChangesResult.DontSave: ...; break;
//         case UnsavedChangesResult.Cancel:   return; // stay
//     }
public static class UnsavedChangesPrompt
{
    public static async Task<UnsavedChangesResult> AskAsync(
        IDialogService dialog,
        string? message = null)
    {
        var parameters = new DialogParameters
        {
            { nameof(UnsavedChangesDialog.Message), message }
        };
        var options = new DialogOptions
        {
            // Force the user to pick one of the three explicit options.
            // Without this, clicking outside / Esc closes the prompt
            // and the canonical "Cancel" path wouldn't fire — the
            // outer dismiss handler would then proceed as if the user
            // had implicitly confirmed.
            BackdropClick = false,
            CloseOnEscapeKey = false,
            CloseButton = false,
            MaxWidth = MaxWidth.ExtraSmall,
            FullWidth = true,
        };
        var dlg = await dialog.ShowAsync<UnsavedChangesDialog>("Unsaved changes", parameters, options);
        var result = await dlg.Result;
        if (result is null || result.Canceled || result.Data is not UnsavedChangesResult choice)
            return UnsavedChangesResult.Cancel;
        return choice;
    }

    // The dialog-side cancel-flow helper. Every dirty-tracked dialog
    // had a near-identical 14-line `TryCancelAsync` switch — same Save
    // → run-save / DontSave → cancel / Cancel → stay shape. This
    // collapses each to one call:
    //
    //     private Task TryCancelAsync() =>
    //         UnsavedChangesPrompt.TryCancelAsync(DialogService, MudDialog, IsDirty,
    //             saveAsync: () => { if (IsValid) Save(); return Task.CompletedTask; });
    //
    // saveAsync owns the validate-before-save decision so dialogs with
    // a Disabled OK button can no-op when invalid instead of saving an
    // incomplete record. When saveAsync is null, picking "Save" still
    // closes the dialog (treated as commit-with-no-side-effect) —
    // useful for confirmation dialogs whose Save is just "yes."
    public static async Task TryCancelAsync(
        IDialogService dialog,
        IMudDialogInstance? mudDialog,
        bool isDirty,
        Func<Task>? saveAsync = null)
    {
        if (!isDirty)
        {
            mudDialog?.Cancel();
            return;
        }
        var choice = await AskAsync(dialog);
        switch (choice)
        {
            case UnsavedChangesResult.Save:
                if (saveAsync is not null) await saveAsync();
                return;
            case UnsavedChangesResult.DontSave:
                mudDialog?.Cancel();
                return;
            case UnsavedChangesResult.Cancel:
            default:
                return; // stay where we are
        }
    }
}
