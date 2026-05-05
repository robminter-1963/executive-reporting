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
    // adminSections is the JSON-serialized list of admin-section keys this
    // role can access (see AdminSections.cs). Null or empty → role has no
    // admin access. Administrator role bypasses regardless of value.
    Task UpdateAsync(Guid id, string name, string? description, string scopeRule, bool isActive,
                     IReadOnlyList<string>? adminSections,
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

    // Canonical name of the System Support role — a fixed name lets the
    // app surface "this is a built-in role" semantics (no rename / no
    // delete) without storing a separate is_builtin flag on the row.
    public const string SystemSupportName = "System Support";

    // True when the role is one of the two built-in roles (Administrator
    // or System Support). Built-in roles can't be renamed or deleted —
    // both UI and service-layer guards key off this so a rogue API call
    // can't bypass the UI lock.
    public static bool IsBuiltInName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (string.Equals(name, AdministratorName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, SystemSupportName, StringComparison.OrdinalIgnoreCase));

    // Admin-section keys this role is allowed to access (see AdminSections.cs
    // for the catalog). Null or empty list = no admin access. Administrator
    // role bypasses this and always sees every section. Stored as a JSON
    // array on RPT_roles.admin_sections; persisted via RoleService.UpdateAsync.
    public IReadOnlyList<string>? AdminSections { get; init; }
}

// Allowed values for RPT_roles.scope_rule. Match the CHECK constraint in
// the migration. Kept as constants rather than an enum so the string
// round-trips to SQL without mapping.
public static class ScopeRules
{
    public const string All  = "all";
    public const string Self = "self";
    // Matches rows where the loan's owner column (chosen per team_type on
    // the connection's Team Builder config) is a member of any team the
    // user is assigned to in RPT_user_teams. Scope fails closed when the
    // connection is missing members SQL, the user has no team assignments,
    // or a team's type has no column mapping.
    public const string Team = "team";

    public static readonly IReadOnlyList<string> Allowed = new[] { All, Self, Team };

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && (value == All || value == Self || value == Team);
}
