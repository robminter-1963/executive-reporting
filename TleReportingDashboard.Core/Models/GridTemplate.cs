namespace TleReportingDashboard.Web.Models;

public class GridTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public bool IsShared { get; set; }
    public string FieldIds { get; set; } = "[]"; // JSON array
    public string? ColumnState { get; set; } // JSON: ColumnOrder, ColumnWidths, HiddenColumns, sort
    // Connection this template's field ids reference. Templates are only valid
    // against reports using the same connection since field catalogs differ
    // per connection.
    public Guid? ConnectionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
