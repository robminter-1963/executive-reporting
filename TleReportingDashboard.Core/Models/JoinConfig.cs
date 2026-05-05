namespace TleReportingDashboard.Web.Models;

public class JoinConfig
{
    public int Id { get; set; }
    public string? JoinId { get; set; }  // String ID from schema_config.json (e.g. "borrinfo_join")
    public string FromTable { get; set; } = string.Empty;
    public string FromColumn { get; set; } = string.Empty;
    public string ToTable { get; set; } = string.Empty;
    public string ToColumn { get; set; } = string.Empty;
    public string JoinType { get; set; } = "INNER JOIN";
    public string? RawSql { get; set; }
    // Optional primary-table scope. When set, QueryBuilder only considers
    // this join for reports whose primary matches (via name OR alias).
    // Null/empty = GENERIC (eligible for any report) — preserves the
    // pre-scoping behavior for legacy joins.
    public string? PrimaryTable { get; set; }
    public string? PrimaryAlias { get; set; }
}
