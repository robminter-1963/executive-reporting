using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// Translates an exception into a user-safe message and logs the original
// with full technical detail. Use anywhere we'd otherwise be tempted to
// write `Snackbar.Add($"Failed: {ex.Message}", Severity.Error)` — that
// pattern leaks SQL schema names, connection details, and stack traces
// into the UI, which is both confusing for end users and a small info
// leak.
//
// Routing rules:
//   * InvalidOperationException / ArgumentException — these are usually
//     thrown by our own services with user-appropriate copy ("A role with
//     that name already exists", "User not found"). Pass through if the
//     message is short enough to be intentional (cap at 250 chars to
//     defend against accidental ToString() of a long object).
//   * SqlException / NpgsqlException — generic database message; the
//     real detail goes to the log.
//   * HttpRequestException / TimeoutException — generic network/timeout
//     message.
//   * UnauthorizedAccessException — "you don't have permission".
//   * Anything else — last-resort generic copy with a "contact admin" hint.
public static class FriendlyError
{
    public static string FromException(Exception ex, ILogger logger, string action)
    {
        // Always log the underlying exception so admins can diagnose from
        // server logs, regardless of what we show in the UI.
        logger.LogError(ex, "User-facing error during {Action}", action);

        return ex switch
        {
            InvalidOperationException ioe when IsUserSafe(ioe.Message) => ioe.Message,
            ArgumentException ae when IsUserSafe(ae.Message) => ae.Message,
            SqlException => "A database error occurred. Admins have been notified — please try again in a moment.",
            HttpRequestException => "A network error occurred — please retry.",
            TimeoutException => "The operation timed out. Try again, and if it persists contact an admin.",
            UnauthorizedAccessException => "You don't have permission to do that.",
            _ when ex.GetType().FullName == "Npgsql.NpgsqlException" =>
                "A database error occurred. Admins have been notified — please try again in a moment.",
            _ => "Something went wrong. Please try again. If it persists, contact an admin."
        };
    }

    // Heuristic: a "user-safe" message is one our own code likely wrote
    // intentionally, not a runtime ToString() of a complex object. Anything
    // bigger than 250 chars or containing newlines is almost certainly the
    // latter, so we replace with generic copy.
    private static bool IsUserSafe(string? msg) =>
        !string.IsNullOrWhiteSpace(msg)
        && msg.Length <= 250
        && !msg.Contains('\n');
}
