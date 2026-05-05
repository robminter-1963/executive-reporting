using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IAdminService
{
    // Back-compat shim — semantically equivalent to IsGlobalAdmin. Existing
    // call sites (gates that don't care about company scope) keep working
    // without an async rewrite.
    bool IsAdmin(string? userEmail);

    // True when the user has a 'global' admin row.
    bool IsGlobalAdmin(string? userEmail);

    // True when the user is a global admin OR has a 'company' admin row for
    // the specified company.
    bool IsCompanyAdmin(string? userEmail, Guid companyId);

    // Full list for the admin-management UI.
    Task<List<AdminEntry>> GetAdminsAsync();

    // Grants an admin role. scope='global' → companyId must be null.
    // scope='company' → companyId must be provided.
    Task<AdminEntry> AssignAsync(string email, AdminScope scope, Guid? companyId, string? createdBy);

    // Revokes a specific admin row by its id.
    Task RevokeAsync(Guid adminId);

    // Drops the in-memory cache so the next check re-reads the DB. Called
    // automatically after Assign/Revoke; exposed for the admin UI to force a
    // refresh if an out-of-band edit happens.
    void Invalidate();
}
