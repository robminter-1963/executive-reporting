using Hangfire.Dashboard;

namespace TleReportingDashboard.Worker.Jobs;

// Authorization filter for the /hangfire dashboard. Hangfire's default
// allows anonymous access from non-localhost — fine for dev, dangerous
// in production where the URL is reachable. This filter:
//
//   * In Development: allows all (so dev can browse the dashboard locally).
//   * In any other environment: requires the request principal's email
//     (from preferred_username, ClaimTypes.Email, or HttpContext.User.Identity.Name)
//     to appear in the configured Admins:Emails list — same source the
//     Web app uses for global-admin allowlisting.
//
// If no Admins:Emails are configured in non-Development, the filter
// denies everyone (fail-closed). That avoids accidentally shipping an
// open dashboard if config is missing.
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        var env = http.RequestServices.GetRequiredService<IHostEnvironment>();
        var config = http.RequestServices.GetRequiredService<IConfiguration>();

        // Dev: open access — same convention as the rest of the app's
        // dev-only affordances (impersonation, no-Entra auth, etc.).
        if (env.IsDevelopment()) return true;

        var admins = config.GetSection("Admins:Emails").Get<string[]>()
                     ?? Array.Empty<string>();
        if (admins.Length == 0) return false;

        var email = http.User?.FindFirst("preferred_username")?.Value
                    ?? http.User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                    ?? http.User?.Identity?.Name
                    ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email)) return false;

        return admins.Any(a => string.Equals(a, email, StringComparison.OrdinalIgnoreCase));
    }
}
