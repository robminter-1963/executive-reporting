namespace TleReportingDashboard.Web.Services;

// Team Builder — dynamic team resolution. No ConfigDB mirror; each source
// connection stores a custom SELECT in RPT_team_sources, and the User editor
// runs it live when an admin opens the team picker.
//
// The SELECT must return the shape documented on TeamRecord. Customers with
// schemas that differ from the default EMPOWER.U_SET_TEAMS layout write their
// own query — which is why the SQL is free-form rather than a column map.
public interface ITeamSourceService
{
    Task<TeamSourceConfig?> GetConfigAsync(Guid connectionId, CancellationToken ct = default);

    Task SaveConfigAsync(Guid connectionId, string teamsSql, string? membersSql,
                         string? userEmailsSql, string? updatedBy, CancellationToken ct = default);

    // Opens the source connection, runs the configured teams_sql, and returns
    // the rows. Returns an empty list if no config exists for this connection.
    // Callers are expected to handle exceptions — a bad query or a dead source
    // shouldn't silently return [] because that would hide misconfiguration.
    Task<List<TeamRecord>> QueryTeamsAsync(Guid connectionId, CancellationToken ct = default);

    // Preview variant — runs an admin-supplied SELECT against the source
    // without persisting it. Used by the Team Builder's Preview button so
    // admins can iterate on the SQL before committing.
    Task<List<TeamRecord>> PreviewTeamsAsync(Guid connectionId, string teamsSql,
                                             CancellationToken ct = default);

    // Runs the admin-supplied members SELECT against the source DB and
    // returns every row. Used by the Team Builder preview when an admin
    // clicks a team to see who's on it. Filtering by team_id happens
    // client-side after this single fetch — keeps the admin SELECT
    // verbatim (no WHERE-clause splicing) and the entire roster is small
    // enough that one round-trip is cheaper than per-team queries.
    Task<List<TeamMemberRecord>> PreviewMembersAsync(Guid connectionId, string membersSql,
                                                    CancellationToken ct = default);

    // Runtime sibling of QueryTeamsAsync — reads the saved members_sql
    // for the connection (no admin-supplied override) and returns the
    // full members roster. Used by the scheduled-report Worker on
    // Individual schedules to expand a team_id into the list of
    // member_ext_id values. Returns an empty list when no members_sql
    // is configured; surfaces source-DB errors to the caller for
    // last_run_status visibility.
    Task<List<TeamMemberRecord>> QueryMembersAsync(Guid connectionId, CancellationToken ct = default);

    // Preview variant of the user-emails resolver — runs an admin-
    // supplied SELECT against the source DB to verify the
    // member_ext_id → email mapping before saving. Mirrors the same
    // shape as PreviewMembersAsync.
    Task<List<TeamMemberEmailRecord>> PreviewUserEmailsAsync(Guid connectionId, string userEmailsSql,
                                                              CancellationToken ct = default);

    // Runtime resolver for member_ext_id → email. Reads the saved
    // user_emails_sql for the connection. Used by the Worker on
    // Individual schedules to look up addresses without requiring
    // every team member to also have an RPT_user_connection_logins
    // mapping. Returns an empty list when no user_emails_sql is
    // configured — caller falls back to the older GetByExternalUserIdAsync
    // chain for backwards compatibility.
    Task<List<TeamMemberEmailRecord>> QueryUserEmailsAsync(Guid connectionId, CancellationToken ct = default);

    // Team-type → primary-table owner-column map. Used by the query pipeline
    // to decide which column to filter against for each of a user's teams.
    // Map keys are case-insensitive.
    Task<Dictionary<string, string>> GetTypeColumnsAsync(Guid connectionId, CancellationToken ct = default);

    Task SaveTypeColumnsAsync(Guid connectionId,
                              IReadOnlyDictionary<string, string> typeColumns,
                              CancellationToken ct = default);

    // Lists every column on a source-DB table by querying its
    // INFORMATION_SCHEMA. Used by the Team Builder UI's column dropdown so
    // admins can pick any column on the table — not just the schema-modeled
    // fields. tableName accepts "schema.table" or bare "table" (defaults to
    // dbo). Throws on unknown connections, non-SQL-Server connections, or
    // unreachable source DBs — caller handles for the dialog.
    Task<List<string>> GetSourceTableColumnsAsync(Guid connectionId, string tableName,
                                                  CancellationToken ct = default);
}

// Free-form teams + members + user-emails queries + metadata. Loaded
// by the Team Builder tab and by downstream consumers (User editor for
// teams, query pipeline for members, scheduled-report Worker for
// user_emails). members_sql and user_emails_sql are both optional at
// the interface level — Team Builder can save just teams_sql first;
// user_emails_sql is only read by the Individual-schedule path and
// stays null in envs that don't use it.
public sealed record TeamSourceConfig(
    Guid ConnectionId,
    string TeamsSql,
    string? MembersSql,
    string? UserEmailsSql,
    DateTime UpdatedAt,
    string? UpdatedBy);

// One team row as returned by a teams_sql SELECT. The column aliases on the
// admin's SELECT must match these names exactly — we read by ordinal after
// asserting the expected columns are present, so order is flexible but names
// are enforced.
public sealed record TeamRecord(
    int TeamId,
    string? TeamName,
    string? ManagerExtId,
    string? ManagerName,
    string? TeamType);

// One team-member row as returned by a members_sql SELECT. team_id and
// member_ext_id are the contract-required columns (TeamSourceDefaults
// .RequiredMemberColumns). MemberName is read opportunistically when the
// admin's SELECT happens to include a member_name column — purely for
// human display in the Team Builder preview, never required by the
// query pipeline. Other columns the admin SELECTs are ignored.
public sealed record TeamMemberRecord(
    int TeamId,
    string MemberExtId,
    string? MemberName);

// One member_ext_id → email row as returned by a user_emails_sql
// SELECT. Required columns: member_ext_id, email
// (TeamSourceDefaults.RequiredUserEmailColumns). Used by the Worker
// on Individual schedules to turn a team's roster of LOS logins
// into mailable addresses without going through the
// RPT_user_connection_logins mapping. The customer's SELECT does
// the join into whichever LOS user table holds the email.
public sealed record TeamMemberEmailRecord(
    string MemberExtId,
    string Email);
