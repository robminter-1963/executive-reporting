namespace TleReportingDashboard.Web.Services;

// Append-only audit trail for security-affecting admin actions. Powers
// the Admin → Audit Log review surface, and the corresponding SOC-2
// change-management evidence.
//
// Scope is deliberately narrow: this is NOT a per-keystroke activity log.
// Only changes that move the security / integrity / confidentiality
// boundary belong here — admin grants, role definitions, connection edits,
// schema config, table-alias scoping, shared dashboard layout, etc.
// End-user content (a user's own saved report, favorites, preferences,
// page-size choices) does NOT get logged here; that's like a personal
// Word doc, outside the SOC-2 scope.
//
// LogAsync MUST be tolerant of failure: a broken audit insert must never
// break the business action that triggered it. Implementations log the
// underlying error via ILogger and swallow the exception.
public interface IAuditLogger
{
    // Records one event. Pass `before` and/or `after` as POCOs / anonymous
    // objects — the implementation serializes to JSON. Either may be null:
    // before is null for creates, after is null for deletes. Use the
    // AuditActions and AuditResources constants below for the verb /
    // category so the review UI's filter has a stable vocabulary.
    //
    // actorEmail is the signed-in user's email (preferred_username claim).
    // null/empty means "system" — used by the appsettings-bootstrap path
    // that runs before any user is signed in. The review UI renders these
    // as "(system)".
    Task LogAsync(
        string? actorEmail,
        string action,
        string resourceType,
        string? resourceId,
        string? resourceLabel,
        object? before = null,
        object? after = null,
        string? notes = null,
        string? correlationId = null,
        CancellationToken ct = default);

    // Reads. Used by the Admin → Audit Log tab — paginated, filterable.
    Task<List<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default);

    // Distinct values for the filter dropdowns. Cheap on the indexed
    // columns; the UI populates Actor + Resource Type pickers from these.
    Task<List<string>> GetDistinctActorsAsync(CancellationToken ct = default);
}

// Verbs the review UI knows about. New verbs can be added without a
// migration — the column is NVARCHAR — but the UI's filter dropdown only
// lists the names in this static so the column stays self-describing.
public static class AuditActions
{
    public const string Create  = "create";
    public const string Update  = "update";
    public const string Delete  = "delete";
    public const string Grant   = "grant";
    public const string Revoke  = "revoke";
    public const string Enable  = "enable";
    public const string Disable = "disable";
    public const string Reorder = "reorder";
    // For resources that have an "active" toggle distinct from delete —
    // hide-on-picker (RPT_companies.is_hidden) is one of these.
    public const string Hide    = "hide";
    public const string Show    = "show";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Create, Update, Delete, Grant, Revoke, Enable, Disable, Reorder, Hide, Show
    };
}

// Resource-type vocabulary. Keep this list aligned with the resources
// we actually audit — adding a new one without wiring an emitter is fine
// (it just won't appear in the filter), but having unused values clutters
// the dropdown.
public static class AuditResources
{
    public const string Admin          = "admin";
    public const string User           = "user";
    public const string Role           = "role";
    public const string Company        = "company";
    public const string CompanyAccess  = "company_access";   // RPT_user_companies
    public const string Connection     = "connection";       // RPT_company_connections
    public const string LibrarySection = "library_section";
    public const string TableAlias     = "table_alias";      // RPT_custom_primary_tables
    public const string CustomFilter   = "custom_filter";
    public const string DashboardTab     = "dashboard_tab";
    public const string DashboardSection = "dashboard_section";
    public const string DashboardTile    = "dashboard_tile";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Admin, User, Role, Company, CompanyAccess, Connection,
        LibrarySection, TableAlias, CustomFilter,
        DashboardTab, DashboardSection, DashboardTile
    };
}

// One audit row. Mirrors the table columns 1:1 plus a deserialized
// timestamp. Strings (not enums) for action / resource_type so future
// values added in code don't trip a deserialization mismatch on rows
// written by an older build.
public sealed record AuditEntry(
    long Id,
    DateTime OccurredAtUtc,
    string? ActorEmail,
    string? ActorUserId,
    string Action,
    string ResourceType,
    string? ResourceId,
    string? ResourceLabel,
    string? BeforeJson,
    string? AfterJson,
    string? CorrelationId,
    string? Notes);

// Filter inputs for the review UI's listing. All filters are optional;
// the implementation applies only the non-null/non-empty ones, ANDed
// together. Cursor-based pagination via AfterId so the UI's "Load more"
// is stable under concurrent writes.
public sealed class AuditQuery
{
    public DateTime? FromUtc      { get; init; }
    public DateTime? ToUtc        { get; init; }
    public string?   ActorEmail   { get; init; }
    public string?   ResourceType { get; init; }
    public string?   ResourceId   { get; init; }
    public string?   Action       { get; init; }
    // Cap on rows returned. Defaults to 200 — big enough to scroll, small
    // enough that the JSON serialization for a heavy `before_json` /
    // `after_json` payload doesn't dominate the response.
    public int       Take         { get; init; } = 200;
    // Cursor: return rows with id < this. Null = newest page.
    public long?     BeforeId     { get; init; }
}
