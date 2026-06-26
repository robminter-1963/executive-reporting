namespace TleReportingDashboard.Web.Services;

// Write-path admin service for RPT_companies. Reads still go through
// ICompanyRegistry (which caches); this service deliberately invalidates
// the registry cache after every mutation so reads stay consistent.
public interface ICompanyAdminService
{
    Task<List<CompanyRecord>> GetAllAsync(CancellationToken ct = default);
    Task<CompanyRecord> CreateAsync(string code, string name,
                                     string? websiteUrl, string? createdBy, CancellationToken ct = default);
    Task UpdateAsync(Guid id, string code, string name,
                     string? websiteUrl, bool isActive, bool isHidden, CancellationToken ct = default);
    Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);
    // Toggle the picker-tile visibility flag. Distinct from SetActiveAsync
    // — a hidden-but-active company stays usable everywhere except the
    // all-companies picker grid.
    Task SetHiddenAsync(Guid id, bool isHidden, CancellationToken ct = default);

    // Logo management. Kept on separate methods (not folded into UpdateAsync)
    // so the usual edit path doesn't have to re-send the blob every time —
    // admins only upload the logo occasionally, but edit other fields often.
    // bytes must be non-null/non-empty; use ClearLogoAsync to delete.
    Task UploadLogoAsync(Guid id, byte[] bytes, string contentType, CancellationToken ct = default);
    Task ClearLogoAsync(Guid id, CancellationToken ct = default);

    // Admin-controlled ordering for the company picker page. Pass the full
    // ordered list of company ids — each row gets display_order = its index
    // in the list. Any rows not in the list keep their current order (they
    // sort after the provided ones by name).
    Task UpdateDisplayOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);

    // Pre-flight count of dependent rows that would be destroyed by a
    // DeleteAsync call. Drives the confirmation dialog so admins see the
    // blast radius before clicking through. Counts are cheap (COUNT(*)
    // on indexed columns); the actual cascade re-walks the tables.
    Task<CompanyDeleteImpact> GetDeleteImpactAsync(Guid id, CancellationToken ct = default);

    // Hard-delete a company and everything that belongs to it. Walks the
    // tables that have NO ACTION FKs to RPT_companies (saved_reports,
    // shares, schedules, grid_templates, master_dashboard_tabs/tiles,
    // schema_config) in dependency order inside a transaction, then
    // deletes the company row — the remaining children (user_companies,
    // company_connections + their cascades, admins, kpis, library_sections,
    // personal_tiles, schema_config_history, user_preferences) drop via FK
    // CASCADE. Audit-logged with the impact summary captured pre-delete.
    // Irreversible — callers MUST confirm with the user first.
    Task DeleteAsync(Guid id, string? deletedBy, CancellationToken ct = default);
}

// Pre-delete blast-radius summary. Each count is rows that would be
// destroyed (or cascaded) if DeleteAsync were called for the company.
// Zero counts are kept (renders cleanly as "0 reports" in the confirm
// dialog and surfaces what the admin can verify in the audit log).
public sealed record CompanyDeleteImpact(
    Guid CompanyId,
    string CompanyName,
    int Connections,
    int SavedReports,
    int ReportShares,
    int ReportSchedules,
    int GridTemplates,
    int DashboardTabs,
    int DashboardTiles,
    int LibrarySections,
    int Kpis,
    int UserGrants,
    int Admins,
    int PersonalPins,
    int SchemaConfigs,
    int SchemaConfigHistoryRows);

// Full-shape row for the admin UI. Distinct from CompanySummary (which
// hides inactive rows). Logo bytes come along on read so the admin tab
// can render a preview thumbnail without a second round-trip; callers
// that don't need the blob can ignore the property.
public sealed record CompanyRecord(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public byte[]? Logo { get; init; }
    public string? LogoContentType { get; init; }
    public int DisplayOrder { get; init; }
    // Optional outbound link (e.g. the company's corporate site). Surfaced
    // on the master dashboard — clicking the company logo opens it in a
    // new tab. NULL means "logo isn't a link."
    public string? WebsiteUrl { get; init; }
    // True when the picker grid should suppress this company's tile.
    // Independent of IsActive — see CompanySummary for the full
    // semantic. Toggle via Admin → Companies.
    public bool IsHidden { get; init; }
}
