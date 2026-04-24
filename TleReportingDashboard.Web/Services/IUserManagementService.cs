namespace TleReportingDashboard.Web.Services;

// Manages RPT_users (canonical user registry) + RPT_user_companies role
// grants. Admins pre-provision users by email; the Entra object ID (user_id)
// is back-filled on first sign-in via BindSignedInUserAsync.
//
// Separate from AdminService (RPT_admins) on purpose — AdminService is still
// the authority for the legacy 'is admin' check used across the app. This
// service mirrors the is_admin bit on RPT_users so the User Management UI
// has one place to edit it, and BindSignedInUserAsync keeps RPT_admins.user_id
// in sync as a side effect. Phase 2 collapses the two.
public interface IUserManagementService
{
    // ── Users ──
    Task<List<UserRecord>> GetAllAsync(CancellationToken ct = default);
    Task<UserRecord?> GetByEmailAsync(string email, CancellationToken ct = default);

    // Pre-provisioning. email is required; displayName optional (filled from
    // claims on first sign-in if null here). roleId is optional — picking a
    // role named "Administrator" flips is_admin on automatically; picking
    // anything else flips it off. Pass null for "no role assigned yet."
    Task<UserRecord> CreateAsync(string email, string? displayName, Guid? roleId,
                                 string? createdBy, CancellationToken ct = default);

    Task UpdateAsync(string email, string? displayName, Guid? roleId, bool isActive,
                     CancellationToken ct = default);

    // Hard-delete, including all RPT_user_companies rows for this user_id.
    // Kept simple — no "archive" state. Admin UI gates the button.
    Task DeleteAsync(string email, CancellationToken ct = default);

    // Called by the sign-in hook. Idempotent: creates an RPT_users row if
    // missing (using claims as fallback), and populates user_id on any
    // email-keyed rows that were waiting for it (RPT_users.user_id + any
    // RPT_user_companies rows whose user_id stub equals the email).
    Task BindSignedInUserAsync(string email, string userId, string? displayName,
                               CancellationToken ct = default);

    // Remembers the last-visited company so /  can redirect there on next
    // sign-in. Cheap, fire-and-forget call from the master dashboard page.
    Task SetLastVisitedCompanyAsync(string email, Guid companyId, CancellationToken ct = default);

    // ── Company access ──
    Task<List<UserCompanyAccess>> GetCompanyAccessAsync(string email, CancellationToken ct = default);

    // Grant or update role for (email, company). Uses a MERGE so callers
    // don't have to distinguish first-grant from role-change. If the user
    // has a user_id already, the row is keyed by that; otherwise the email
    // is used as the stub user_id until first sign-in.
    Task UpsertCompanyAccessAsync(string email, Guid companyId, string role,
                                  CancellationToken ct = default);

    Task RevokeCompanyAccessAsync(string email, Guid companyId, CancellationToken ct = default);

    // Per-connection external-user-id logins. A company can host multiple
    // connections (Encompass + Salesforce, etc.) and the user's login in
    // each can differ — so the lookup is keyed by connection_id. Used by
    // the row-level-scoping pipeline: for a self-scoped role running a
    // report against connection X, the pipeline looks up (user, X) and
    // injects `owner_col = @external_user_id` on match. No match = zero
    // rows (no fallback).
    Task<List<UserConnectionLogin>> GetConnectionLoginsAsync(string email, Guid companyId, CancellationToken ct = default);

    // Sets the external user id for (email, connection). A null or empty
    // value clears the row (deletes the entry) rather than storing an
    // empty string — zero-or-missing semantics are identical for the
    // scoping predicate.
    Task UpsertConnectionLoginAsync(string email, Guid connectionId, string? externalUserId,
                                     CancellationToken ct = default);

    // Resolves the self-scope predicate input: the user's external id for
    // a specific connection. Null when unset — callers interpret that as
    // "no rows match" rather than "no filter."
    Task<string?> GetExternalUserIdAsync(string email, Guid connectionId, CancellationToken ct = default);
}

// Canonical user record surfaced to the admin UI. user_id is nullable while
// a pre-provisioned user hasn't signed in yet — render "Pending first sign-in"
// next to the row when that's the case.
public sealed record UserRecord(
    string Email,
    string? UserId,
    string? DisplayName,
    bool IsAdmin,
    bool IsActive,
    Guid? LastVisitedCompanyId,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime UpdatedAt)
{
    // Job-function role (RPT_roles). Null means "no role assigned yet."
    // RoleName comes along on reads so the UI doesn't have to join against
    // RPT_roles for a label. IsAdmin is the authoritative admin flag; the
    // service keeps it in sync with role name == "Administrator".
    public Guid? RoleId { get; init; }
    public string? RoleName { get; init; }
}

// Per-(user, company) role grant. Non-admin users need at least one of these
// to see a company in the picker. Admins ignore this table — they see every
// active company automatically.
public sealed record UserCompanyAccess(
    string Email,
    Guid CompanyId,
    string CompanyCode,
    string CompanyName,
    string Role,
    bool IsDefault,
    DateTime CreatedAt);

// One row per (user, company-connection) showing the connection's label
// plus the user's login in it (null if never set). Returned by
// GetConnectionLoginsAsync so the admin UI can list every connection a
// company has and surface which ones the user has a login on.
public sealed record UserConnectionLogin(
    Guid ConnectionId,
    string ConnectionName,
    bool IsActive,
    string? ExternalUserId);

// Valid role strings for RPT_user_companies.role. Match the CHECK constraint
// in the migration (Editor / Viewer / Scheduler). Kept as constants rather
// than an enum so the strings round-trip to SQL without mapping.
public static class UserRoles
{
    public const string Editor    = "Editor";
    public const string Viewer    = "Viewer";
    public const string Scheduler = "Scheduler";

    // Sentinel used by UI gating code to represent "this user is a global
    // admin for this company." Not a real RPT_user_companies.role value —
    // admins never have a row in that table — so it's excluded from All
    // and IsValid stays strict about the DB-allowed values.
    public const string Admin     = "Admin";

    public static readonly IReadOnlyList<string> All = new[] { Editor, Viewer, Scheduler };

    public static bool IsValid(string role) =>
        role == Editor || role == Viewer || role == Scheduler;
}
