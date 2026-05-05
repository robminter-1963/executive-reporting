using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TleReportingDashboard.Web.Services;

/// <summary>
/// Stub authentication handler for development purposes.
/// Produces an authenticated ClaimsPrincipal with dev user claims.
///
/// Supports impersonation via the <c>dev_as_email</c> cookie — when set, the
/// handler returns claims for that email (pulled from RPT_users for the real
/// oid + display_name + is_admin) instead of the default seed. Set by the
/// /dev/switch-user endpoint; cleared by /dev/clear-user.
///
/// Defense-in-depth: refuses to authenticate outside Development.
/// </summary>
public class StubAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IHostEnvironment _environment;

    // Cookie name must stay in sync with the /dev/switch-user endpoint in Program.cs.
    public const string ImpersonationCookieName = "dev_as_email";

    // Default identity when no impersonation cookie is set. Matches the
    // original hardcoded seed used by this handler so existing dev workflows
    // keep working.
    private const string DefaultEmail = "rob.minter@ralisservices.com";
    private const string DefaultName  = "Rob Minter";
    private const string DefaultOid   = "00000000-0000-0000-0000-000000000001";

    public StubAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHostEnvironment environment)
        : base(options, logger, encoder)
    {
        _environment = environment;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_environment.IsDevelopment())
        {
            return AuthenticateResult.Fail(
                "StubAuthenticationHandler is only allowed in Development environment.");
        }

        var impersonatedEmail = Context.Request.Cookies[ImpersonationCookieName];

        string name, email, oid;
        bool isAdmin;

        if (string.IsNullOrWhiteSpace(impersonatedEmail))
        {
            name    = DefaultName;
            email   = DefaultEmail;
            oid     = DefaultOid;
            isAdmin = true;
        }
        else
        {
            // Look up the impersonated user in RPT_users so their real oid +
            // display name end up on the identity. If the email isn't
            // registered yet, synthesize an identity using the email itself
            // as the stub oid — the app will still load but access checks
            // will reject everything (which is what you want for testing a
            // "no-access" empty state).
            var userMgmt = Context.RequestServices.GetRequiredService<IUserManagementService>();
            var user = await userMgmt.GetByEmailAsync(impersonatedEmail);
            if (user is not null)
            {
                name    = user.DisplayName ?? user.Email;
                email   = user.Email;
                oid     = user.UserId ?? user.Email;  // pre-signed-in stub
                isAdmin = user.IsAdmin;
            }
            else
            {
                name    = impersonatedEmail;
                email   = impersonatedEmail;
                oid     = impersonatedEmail;
                isAdmin = false;
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name),
            new("preferred_username", email),
            new("oid", oid),
            new(ClaimTypes.Role, "Dashboard.User"),
        };
        if (isAdmin)
        {
            claims.Add(new(ClaimTypes.Role, "Dashboard.Admin"));
        }

        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
