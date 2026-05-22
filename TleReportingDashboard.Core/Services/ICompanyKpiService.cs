using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// CRUD + visibility toggle for the per-company KPI band rendered above the
// Master Dashboard's tab strip. KPIs live at the company level (not the
// tab level) — one shared list per company, drag-reorderable, individually
// resizable via col_span.
//
// The band's overall visibility is gated by RPT_companies.show_kpi_band:
// the same toggle a global admin would flip in the Manage KPIs dialog.
// When that's off, the dashboard hides the band entirely regardless of
// how many KPIs are defined.
public interface ICompanyKpiService
{
    // Lists this company's KPIs in sort_order. Cached at the service
    // layer so the dashboard's render path stays cheap.
    Task<List<CompanyKpi>> GetByCompanyAsync(Guid companyId);

    // Reads the show_kpi_band toggle on RPT_companies. Separated from
    // the KPI list so callers can decide "render at all?" without
    // fetching every KPI row.
    Task<bool> IsBandVisibleAsync(Guid companyId);

    // Flips show_kpi_band. Invalidates the cache key.
    Task SetBandVisibleAsync(Guid companyId, bool visible);

    // Creates a KPI. Returns the persisted record (id assigned). Appends
    // to the end of the company's existing list (sort_order = MAX + 1).
    Task<CompanyKpi> CreateAsync(CompanyKpi kpi, string? createdByEmail);

    // Updates everything except sort_order — reorder goes through
    // ReorderAsync so the two paths can't fight over the same column.
    Task UpdateAsync(CompanyKpi kpi);

    // Reorder by passing the full ordered id list. Writes sort_order =
    // index in a single transaction so a partial failure can't leave
    // the band in mixed-order state. Same shape as
    // ILibrarySectionService.ReorderSectionsAsync.
    Task ReorderAsync(Guid companyId, IList<Guid> orderedIds);

    Task DeleteAsync(Guid kpiId);
}
