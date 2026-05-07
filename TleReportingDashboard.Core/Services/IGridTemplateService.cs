using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IGridTemplateService
{
    // When connectionId is provided, only templates built against that
    // connection's schema are returned. Use this in the Apply Template flow so
    // reports don't pick up templates with field ids from a different catalog.
    Task<List<GridTemplate>> GetTemplatesAsync(string userId, Guid? connectionId = null);
    Task<GridTemplate?> GetTemplateAsync(Guid id);
    Task<GridTemplate> SaveTemplateAsync(GridTemplate template);
    Task UpdateTemplateAsync(GridTemplate template);
    Task DeleteTemplateAsync(Guid id, string userId);

    /// <summary>
    /// Resolves the linked template's field IDs and column state for a report.
    /// Returns null if the report has no linked template or the template doesn't exist.
    /// </summary>
    Task<ResolvedTemplate?> ResolveTemplateAsync(Guid? gridTemplateId);
}

public record ResolvedTemplate(
    List<string> FieldIds,
    List<string>? ColumnOrder,
    List<string>? HiddenColumns,
    string? DefaultSortField,
    string? DefaultSortDirection,
    // Per-template column-header overrides — key = field id, value =
    // custom label. Reports applying this template render the override
    // in the Table grid; missing keys fall back to the schema field's
    // Label. Null when the template was saved before this feature
    // existed (legacy templates) or no admin set any overrides.
    Dictionary<string, string>? CustomColumnLabels);

