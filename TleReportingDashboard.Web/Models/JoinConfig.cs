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
}
