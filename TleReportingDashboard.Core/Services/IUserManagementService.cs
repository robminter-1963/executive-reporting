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
    // forceRefresh=true bypasses the per-email cache and reads fresh from
    // the DB. Used by callers that depend on near-real-time reads of fields
    // toggled elsewhere in the same session (e.g. CompanyPicker checking
    // prefers_company_picker right after SetPrefersCompanyPickerAsync) —
    // the cache's race window between invalidate-and-in-flight-factory
    // could otherwise serve a stale value.
    Task<UserRecord?> GetByEmailAsync(string email, CancellationToken ct = default)
        => GetByEmailAsync(email, forceRefresh: false, ct);

    Task<UserRecord?> GetByEmailAsync(string email, bool forceRefresh, CancellationToken ct = default);

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

    // Clears the last-visited company so /  shows the company picker on
    // next sign-in. Called when the user explicitly navigates to the picker
    // ("All Companies" view) — that choice IS their preference, persist it.
    Task ClearLastVisitedCompanyAsync(string email, CancellationToken ct = default);

    // Sets the sticky "always start at the picker" flag. TRUE when the
    // user landed on /?all=1; FALSE when they pick a company from the
    // picker. Survives across sessions even when last_visited_company_id
    // gets re-set by Master Dashboard URL navigations.
    Task SetPrefersCompanyPickerAsync(string email, bool prefers, CancellationToken ct = default);

    // ── Company access ──
    Task<List<UserCompanyAccess>> GetCompanyAccessAsync(string email, CancellationToken ct = default);

    // Resolves the canonical login email for a user given the address they
    // actually authenticated with. Supports the multi-tenant Entra case
    // where one logical person has accounts in several ADs (e.g.
    // paul.bae@theloanexchange.com in TLE's tenant + paul.bae@cashcall.com
    // in CashCall's tenant). The per-company email override on
    // RPT_user_companies doubles as the alias map: when an admin assigns
    // a user to CompanyB and types the user's CompanyB AD address as the
    // company email, signing in with that address resolves back to the
    // canonical RPT_users row.
    //
    // Returns the matching RPT_users.email, or null when no match is found
    // (caller treats null as "user not registered" — same as today).
    // Caller can compare the result to `loginEmail` to decide whether
    // the address was a direct hit or routed through a per-company alias.
    Task<string?> ResolveCanonicalUserEmailAsync(string loginEmail, CancellationToken ct = default);

    // Grant or update role for (email, company). Uses a MERGE so callers
    // don't have to distinguish first-grant from role-change. If the user
    // has a user_id already, the row is keyed by that; otherwise the email
    // is used as the stub user_id until first sign-in.
    //
    // `companyEmail` is the optional per-company email override. Pass null
    // (the default) to leave any existing override alone — useful when the
    // caller is only changing role. Pass an explicit empty string to clear
    // the override and fall back to the user's login email.
    Task UpsertCompanyAccessAsync(string email, Guid companyId, string role,
                                  string? companyEmail = null,
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

    // Reverse of GetExternalUserIdAsync. Resolves a Team Builder
    // member_ext_id back to the registered app user on this connection.
    // Used by the scheduled-report Worker on Individual schedules to
    // turn a team's roster of LOS logins into emails the resolver can
    // scope. Null when no app user is mapped to that ext id on this
    // connection — caller skips that team member with a logged warning.
    Task<UserRecord?> GetByExternalUserIdAsync(Guid connectionId, string externalUserId,
                                                CancellationToken ct = default);

    // ── Team assignments (team-scope role input) ───────────────────────────

    // Teams the user could be assigned to. Restricted to teams on connections
    // that belong to companies the user already has a grant for
    // (RPT_user_companies) — so revoking company access also removes the
    // team picker candidates without a separate mutation.
    Task<List<AssignableTeam>> GetAssignableTeamsAsync(string email, CancellationToken ct = default);

    // Current team assignments for this user. Returned regardless of current
    // role scope — rows stay dormant when scope isn't 'team' so toggling
    // back doesn't lose prior picks.
    Task<List<UserTeamAssignment>> GetUserTeamsAsync(string email, CancellationToken ct = default);

    // Full replace of team assignments for this user. Admin UI calls this
    // from the User editor when the selected role's scope is 'team'. No-op
    // gating (skipping this when scope isn't 'team') lives on the caller;
    // the service doesn't inspect role scope so callers can also use this
    // for direct management flows.
    Task SetUserTeamsAsync(string email, IReadOnlyList<UserTeamAssignment> teams,
                           CancellationToken ct = default);
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
    // Mirror of RPT_roles.admin_sections for the user's role. Pulled
    // through the same LEFT JOIN that populates RoleId/RoleName so a
    // single user fetch carries everything access checks need. Null /
    // empty = no admin sections allowed; Administrator role bypasses.
    public IReadOnlyList<string>? RoleAdminSections { get; init; }

    // Sticky "default to the company picker" preference. Set TRUE when the
    // user lands on /?all=1; reset FALSE when they pick a specific company
    // FROM the picker. Direct URL navigation to /master-dashboard/<code>
    // doesn't touch this flag, so the preference survives bookmarks /
    // browser session restore even though last_visited_company_id gets
    // re-set on every Master Dashboard load.
    public bool PrefersCompanyPicker { get; init; }
}

// Per-(user, company) role grant. Non-admin users need at least one of these
// to see a company in the picker. Admins ignore this table — they see every
// active company automatically.
//
// `Email` is the user's login/Entra address (matches RPT_users.email). It's
// also the lookup key into RPT_users for everything auth-related.
//
// `CompanyEmail` is an optional per-company override — set when the user has
// a separate address inside a particular tenant (e.g. one Outlook account per
// company). NULL = fall back to `Email`. Auth still goes by Entra OID; this
// is for code paths that need to REACH the user in a particular company's
// context (scheduled deliveries, display labels, share notifications).
public sealed record UserCompanyAccess(
    string Email,
    Guid CompanyId,
    string CompanyCode,
    string CompanyName,
    string Role,
    bool IsDefault,
    DateTime CreatedAt,
    string? CompanyEmail = null);

// One row per (user, company-connection) showing the connection's label
// plus the user's login in it (null if never set). Returned by
// GetConnectionLoginsAsync so the admin UI can list every connection a
// company has and surface which ones the user has a login on.
public sealed record UserConnectionLogin(
    Guid ConnectionId,
    string ConnectionName,
    bool IsActive,
    string? ExternalUserId);

// A team option in the User editor's team selector. Denormalized labels
// (company / connection names, team type) so the UI can group and filter
// without follow-up joins.
public sealed record AssignableTeam(
    Guid ConnectionId,
    int TeamId,
    string? TeamName,
    string? TeamType,
    Guid CompanyId,
    string CompanyName,
    string ConnectionName);

// Composite key of a single team assignment. Matches the PK on
// RPT_user_teams (user_id, connection_id, team_id) minus the user_id —
// callers supply the user via email, the service resolves the stable key.
public sealed record UserTeamAssignment(
    Guid ConnectionId,
    int TeamId);

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
