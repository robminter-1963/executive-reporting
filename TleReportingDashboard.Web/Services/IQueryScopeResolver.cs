using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Resolves QueryRequest.Scoping from the signed-in user's role + the
// report's connection / primary table. One call per query, from the page
// layer (ReportViewer, DetailViewer, MasterDashboard tiles, Report
// Builder preview). Admins always resolve to null (no scope).
public interface IQueryScopeResolver
{
    // Returns the scoping block to attach to the request, or null when the
    // query should run unscoped (admin, or the user's role is 'all').
    // ForceNoMatch = true is set when the role is self-scoped but either
    // the primary table has no owner_field_id OR the user has no
    // external_user_id for the connection — either means we can't safely
    // filter, so we fail safe to zero rows.
    Task<QueryScopingInfo?> ResolveAsync(
        string? userEmail,
        Guid? connectionId,
        string? primaryTable,
        CancellationToken ct = default);
}
