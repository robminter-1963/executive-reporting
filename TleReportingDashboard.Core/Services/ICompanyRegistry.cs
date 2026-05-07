namespace TleReportingDashboard.Web.Services;

// Lightweight read-only reader for RPT_companies. Used by dropdowns and
// admin UIs that need the company list without taking a dependency on the
// (richer) IDataSourceFactory coming in Phase 2.
public interface ICompanyRegistry
{
    Task<List<CompanySummary>> GetActiveAsync(CancellationToken ct = default);
    Task<CompanySummary?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CompanySummary?> GetByCodeAsync(string code, CancellationToken ct = default);
    void Invalidate();
}

// Full row shape for every active company. Logo + content type are carried
// inline so the /  picker page can render each card without a second
// round-trip per company. DisplayOrder drives the picker's sort — admins
// set it via AdminCompaniesTab; ties resolve by Name.
public sealed record CompanySummary(Guid Id, string Code, string Name, bool IsActive)
{
    public int DisplayOrder { get; init; }
    public byte[]? Logo { get; init; }
    public string? LogoContentType { get; init; }
    // Optional outbound link. When set, the master dashboard's header logo
    // becomes a clickable link to this URL (opened in a new tab).
    public string? WebsiteUrl { get; init; }
    // True when the company is intentionally suppressed from the all-
    // companies picker grid. Independent of IsActive — a hidden company
    // is still fully functional via direct nav, dropdowns, and admin
    // surfaces; it just doesn't render as a tile on the front door.
    public bool IsHidden { get; init; }
}
