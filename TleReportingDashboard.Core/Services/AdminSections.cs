namespace TleReportingDashboard.Web.Services;

// Canonical list of admin-section keys + their human labels.
//
// Stored on RPT_roles.admin_sections as a JSON array of these keys. The
// Administrator role bypasses this list entirely (always sees every tab);
// every other role's access is the intersection of "what's defined here"
// and "what's in their JSON list."
//
// Adding a section: append a new key constant + an entry in `All`. Existing
// roles stay backward-compatible (their JSON just doesn't carry the new
// key, so they default to "no access" for that section until an admin
// explicitly grants it).
public static class AdminSections
{
    public const string Companies     = "companies";
    public const string DbConnections = "db_connections";
    public const string Users         = "users";
    public const string Roles         = "roles";
    public const string TeamBuilder   = "team_builder";
    public const string Schedules     = "schedules";
    public const string SchemaHistory = "schema_history";
    public const string SchemaBuilder = "schema_builder";
    public const string Promotion     = "promotion";
    public const string Theme         = "theme";
    public const string AppSettings   = "app_settings";
    public const string ColumnWidths  = "column_widths";
    // SOC-2 change-management trail for security-affecting admin actions.
    // Lives at the end of the strip because it's a review surface, not an
    // authoring surface — separating it visually helps reinforce that.
    public const string AuditLog      = "audit_log";

    // Ordered as they appear in the Admin tab strip; the Roles tab editor
    // uses the same order so the checkbox grid mirrors the live UI.
    public static readonly IReadOnlyList<(string Key, string Label)> All = new[]
    {
        (Companies,     "Companies"),
        (DbConnections, "DB Connections"),
        (Users,         "Users"),
        (Roles,         "Roles"),
        (TeamBuilder,   "Team Builder"),
        (Schedules,     "Schedules"),
        (SchemaHistory, "Schema History"),
        (SchemaBuilder, "Schema Builder"),
        (Promotion,     "Promotion"),
        (Theme,         "Theme"),
        (AppSettings,   "App Settings"),
        (ColumnWidths,  "Column Widths"),
        (AuditLog,      "Audit Log"),
    };

    public static bool IsValid(string? key) =>
        !string.IsNullOrWhiteSpace(key) && All.Any(s => s.Key == key);
}
