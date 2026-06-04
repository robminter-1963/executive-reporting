using Microsoft.AspNetCore.Http;

namespace TleReportingDashboard.Web.Services;

// Thin facade over IHttpContextAccessor that exposes just the two claims
// every service-layer audit-logging path needs: the signed-in user's
// email (preferred_username) and their Entra object id (oid).
//
// Why not pass these explicitly through every service method? Most
// admin write paths already capture a `createdBy` / `userEmail` parameter
// — but many don't, and threading them through service interfaces just
// to satisfy auditing would change every signature in the codebase.
// HttpContextAccessor under Blazor Server is "free" — the circuit
// preserves the auth state, so per-call resolution is a property read.
//
// Returns null/empty for background-worker contexts where there's no
// HttpContext (scheduled jobs, hosted services). The audit logger
// records those as "(system)" rather than misleading-user attribution.
public interface ICurrentUserAccessor
{
    // preferred_username claim. Null when the request is unauthenticated
    // or there's no HttpContext (background work, startup bootstrap).
    string? Email { get; }

    // oid claim — Entra object id. Stable across email changes; helps
    // long-term forensics if a user's email gets renamed later. Null
    // when running outside a circuit or before sign-in.
    string? UserId { get; }
}

public sealed class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _http;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor http)
    {
        _http = http;
    }

    public string? Email => _http.HttpContext?.User.FindFirst("preferred_username")?.Value
                            ?? _http.HttpContext?.User.FindFirst("email")?.Value
                            ?? _http.HttpContext?.User.Identity?.Name;

    public string? UserId => _http.HttpContext?.User.FindFirst("oid")?.Value;
}
