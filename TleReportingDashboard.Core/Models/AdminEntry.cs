namespace TleReportingDashboard.Web.Models;

// Row-shaped DTO for RPT_admins. Returned by IAdminService.GetAdminsAsync
// for the admin-management UI.
public enum AdminScope
{
    Global,
    Company
}

public sealed class AdminEntry
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public AdminScope Scope { get; set; }
    public Guid? CompanyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
