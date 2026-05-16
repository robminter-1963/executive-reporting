using Microsoft.Data.SqlClient;
using MudBlazor;
using TleReportingDashboard.Web.Services;

namespace TleReportingDashboard.Web.Components.Shared;

// One-liner wrapper for the 80+ try/Snackbar/catch sites peppered
// across every Admin*Tab.razor / dialog / page. Each one repeats:
//
//     try {
//         await Svc.X(...);
//         Snackbar.Add("X-ed.", Severity.Success);
//     } catch (SqlException ex) when (ex.IsDuplicateKey()) {
//         Snackbar.Add("…already exists.", Severity.Warning);
//     } catch (Exception ex) {
//         Snackbar.Add($"Failed: {ex.Message}", Severity.Error);
//     }
//
// The last branch is the worst — it leaks raw exception text (and
// occasionally SQL schema/connection detail) into the UI. We already
// have FriendlyError.FromException for that exact problem; RunAsync
// routes through it. Net effect: one method, one place to add new
// exception-type translations, no info-disclosure regressions.
//
// Lives in the Web project (not Core) because Core deliberately
// doesn't reference MudBlazor — keeping the dependency direction
// Web → Core, never the reverse.
public static class SnackbarExtensions
{
    // Fire-and-forget variant. Returns true on success so callers can
    // chain follow-up work (re-load, navigate, etc.) only when the call
    // actually succeeded:
    //
    //     if (await Snackbar.RunAsync(Logger, "deleting role",
    //             () => RoleSvc.DeleteAsync(id),
    //             successMessage: "Role deleted",
    //             duplicateMessage: "Role is in use — reassign users first."))
    //         await ReloadAsync();
    public static async Task<bool> RunAsync(
        this ISnackbar snackbar,
        ILogger logger,
        string action,
        Func<Task> work,
        string? successMessage = null,
        string? duplicateMessage = null)
    {
        var (ok, _) = await RunCoreAsync<int>(snackbar, logger, action,
            async () => { await work(); return 0; },
            successMessage, duplicateMessage);
        return ok;
    }

    // Value-returning variant. Returns (true, value) on success, or
    // (false, default) when an exception was caught and shown.
    public static Task<(bool Ok, T? Value)> RunAsync<T>(
        this ISnackbar snackbar,
        ILogger logger,
        string action,
        Func<Task<T>> work,
        string? successMessage = null,
        string? duplicateMessage = null)
        => RunCoreAsync(snackbar, logger, action, work, successMessage, duplicateMessage);

    private static async Task<(bool Ok, T? Value)> RunCoreAsync<T>(
        ISnackbar snackbar,
        ILogger logger,
        string action,
        Func<Task<T>> work,
        string? successMessage,
        string? duplicateMessage)
    {
        try
        {
            var value = await work();
            if (!string.IsNullOrEmpty(successMessage))
                snackbar.Add(successMessage, Severity.Success);
            return (true, value);
        }
        catch (SqlException ex) when (ex.IsDuplicateKey())
        {
            // Unique-index hit. Caller-specific copy beats the generic
            // "already exists" fallback so the message names the entity
            // (company / role / connection / schedule).
            snackbar.Add(duplicateMessage ?? "That value is already taken.", Severity.Warning);
            logger.LogInformation("Duplicate-key violation during {Action}: {Message}", action, ex.Message);
            return (false, default);
        }
        catch (Exception ex)
        {
            // FriendlyError logs the full exception and returns
            // user-safe copy. Everything else flows through it.
            snackbar.Add(FriendlyError.FromException(ex, logger, action), Severity.Error);
            return (false, default);
        }
    }
}
