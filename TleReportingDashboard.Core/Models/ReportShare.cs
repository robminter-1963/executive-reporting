namespace TleReportingDashboard.Web.Models;

public class ReportShare
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public string SharedWithId { get; set; } = string.Empty; // Entra object ID
    public string SharedWithType { get; set; } = string.Empty; // "user" or "group"
    public string Permission { get; set; } = "viewer"; // "viewer" or "editor"
    public string SharedById { get; set; } = string.Empty; // Entra object ID of sharer
    public DateTime CreatedAt { get; set; }
}
