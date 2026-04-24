namespace TleReportingDashboard.Web.Services;

// Admin-managed job-function role catalog. Separate from:
//   * RPT_user_companies.role — per-company permission tier (Editor / Viewer
//     / Scheduler). Phase 4+ decides whether these stay independent.
//   * RPT_users.is_admin — system-level boolean. Kept in sync with the
//     "Administrator" role by UserManagementService (picking role ==
//     Administrator sets is_admin = true; anything else clears it).
public interface IRoleService
{
    Task<List<RoleRecord>> GetAllAsync(CancellationToken ct = default);
    Task<RoleRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<RoleRecord?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<RoleRecord> CreateAsync(string name, string? description, string scopeRule,
                                  string? createdBy, CancellationToken ct = default);
    Task UpdateAsync(Guid id, string name, string? description, string scopeRule, bool isActive,
                     CancellationToken ct = default);
    // Hard-delete. If any RPT_users row still points at the role, the FK
    // ON DELETE SET NULL clears their role_id — the users aren't orphaned.
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    // Updates sort_order for every role in the list to its index (0-based),
    // driving the ordering on the Roles tab and any UI dropdowns.
    Task UpdateSortOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);
}

public sealed record RoleRecord(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    int SortOrder,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime UpdatedAt)
{
    // Row-level scoping behavior for queries run by users with this role.
    // 'all'  — no auto-filter, user sees every row the report returns.
    // 'self' — QueryBuilder injects `owner_col = @external_user_id` into
    //          the WHERE clause when the schema defines an owner_field_id
    //          for the primary table and the user has an external_user_id
    //          grant for the current company. See ScopeRules constants.
    public string ScopeRule { get; init; } = ScopeRules.All;

    // The canonical name of the admin role — hard-coded here so service /
    // UI code can identify "is this the admin row" by name after the seed.
    // Renaming the seed row in the UI is allowed but stops the is_admin
    // auto-sync from firing; advise admins not to rename this one.
    public const string AdministratorName = "Administrator";
}

// Allowed values for RPT_roles.scope_rule. Match the CHECK constraint in
// the migration. Kept as constants rather than an enum so the string
// round-trips to SQL without mapping.
public static class ScopeRules
{
    public const string All  = "all";
    public const string Self = "self";

    public static readonly IReadOnlyList<string> Allowed = new[] { All, Self };

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && (value == All || value == Self);
}
