namespace TleReportingDashboard.Web.Services;

// Write-path admin service for RPT_companies. Reads still go through
// ICompanyRegistry (which caches); this service deliberately invalidates
// the registry cache after every mutation so reads stay consistent.
public interface ICompanyAdminService
{
    Task<List<CompanyRecord>> GetAllAsync(CancellationToken ct = default);
    Task<CompanyRecord> CreateAsync(string code, string name, string dataSourceType, string connectionRef,
                                     string? websiteUrl, string? createdBy, CancellationToken ct = default);
    Task UpdateAsync(Guid id, string code, string name, string dataSourceType, string connectionRef,
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
}

// Full-shape row for the admin UI. Distinct from CompanySummary (which hides
// inactive rows and the connection_ref pointer). Logo bytes come along on
// read so the admin tab can render a preview thumbnail without a second
// round-trip; callers that don't need the blob can ignore the property.
public sealed record CompanyRecord(
    Guid Id,
    string Code,
    string Name,
    string DataSourceType,
    string ConnectionRef,
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
