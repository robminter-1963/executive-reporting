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
                         string? updatedBy, CancellationToken ct = default);

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

// Free-form teams + members query + metadata. Loaded by the Team Builder tab
// and by the User editor (teams) / query pipeline (members). members_sql is
// optional at the interface level — not all connections need team scope
// wired, and Team Builder can save just teams_sql first.
public sealed record TeamSourceConfig(
    Guid ConnectionId,
    string TeamsSql,
    string? MembersSql,
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
