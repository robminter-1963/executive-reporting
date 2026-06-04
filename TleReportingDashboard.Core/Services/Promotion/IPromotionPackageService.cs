namespace TleReportingDashboard.Web.Services.Promotion;

// Builds and consumes promotion packages — the staging→production
// transport mechanism. Two-instance model: Staging exports a JSON
// package; Production imports it.
//
// Why a file-based bundle instead of a live DB-to-DB push: keeps the
// two environments isolated (no prod credentials on staging), gives
// admins a reviewable artifact, and lets the import preview show
// exactly what will change before anything writes.
//
// Two mapping dimensions on import:
//   * Schema configs map to a target CONNECTION (ImportSchemaConfigAsync).
//   * Everything company-scoped — companies, library sections, grid
//     templates, reports, dashboards — maps to a target COMPANY in a
//     single pass (ImportCompanyScopedAsync), because those entities form
//     a dependency graph the importer has to resolve together.
public interface IPromotionPackageService
{
    // Serializes the requested sections into a single package tagged with
    // this instance's environment label. Returns the JSON bytes ready to
    // stream as a file download.
    Task<byte[]> ExportAsync(
        PromotionExportRequest request,
        string? exportedBy,
        string? notes,
        CancellationToken ct = default);

    // Parses a package's bytes without applying anything. Used by the
    // import UI to render a preview (entry counts, source env, who
    // exported, when) before the admin confirms.
    PromotionPackage Parse(byte[] packageBytes);

    // Applies one schema-config entry against a target connection in
    // this instance's ConfigDb. Each entry is imported in isolation so
    // a single failed mapping doesn't roll back the rest. Returns a
    // human-readable result the UI can show in a per-entry status row.
    Task<ImportResult> ImportSchemaConfigAsync(
        PromotionPackage.SchemaConfigEntry entry,
        Guid targetConnectionId,
        string? importedBy,
        CancellationToken ct = default);

    // Applies the company-scoped sections of a package against this
    // instance, resolving every cross-reference through the supplied
    // company mappings. Sections the package didn't include are skipped.
    // Each source company is processed independently so one bad mapping
    // doesn't abort the rest; per-item outcomes accumulate into the report.
    Task<PromotionImportReport> ImportCompanyScopedAsync(
        PromotionPackage package,
        IReadOnlyList<CompanyImportMapping> companyMappings,
        string? importedBy,
        CancellationToken ct = default);
}

// What to bundle. SchemaConfigConnectionIds drives the (existing) per-
// connection schema export; CompanyIds + the Include* flags drive the
// company-scoped sections. A company with no flag set contributes nothing
// beyond its own CompanyEntry (and only when IncludeCompanies is true).
public sealed class PromotionExportRequest
{
    public IReadOnlyList<Guid> SchemaConfigConnectionIds { get; init; } = Array.Empty<Guid>();

    // Companies whose reports / dashboards / sections / templates get
    // bundled. Also the set of CompanyEntry rows emitted when
    // IncludeCompanies is true.
    public IReadOnlyList<Guid> CompanyIds { get; init; } = Array.Empty<Guid>();

    public bool IncludeCompanies { get; init; }

    // Report Library sections + the grid templates reports reference.
    // Bundled so report imports don't land orphaned.
    public bool IncludeLibraryAndGridTaxonomy { get; init; }

    public bool IncludeReports { get; init; }

    public bool IncludeDashboards { get; init; }
}

// How a source company maps onto this environment. Exactly one of the two
// outcomes is intended:
//   * TargetCompanyId set        → land items on that existing company.
//   * CreateIfMissing = true     → create a company from the CompanyEntry
//                                   (requires the package to include it).
// Both unset → the source company's items are skipped with a message.
public sealed record CompanyImportMapping(
    Guid SourceCompanyId,
    Guid? TargetCompanyId,
    bool CreateIfMissing);

public sealed record ImportResult(bool Success, string Message);

// Roll-up of a company-scoped import. Counts are best-effort tallies for
// the success snackbar; Messages carries the per-item detail (skips,
// matches, failures) the UI renders in a results panel.
public sealed record PromotionImportReport(
    int CompaniesCreated,
    int CompaniesMatched,
    int LibrarySections,
    int GridTemplates,
    int Reports,
    int DashboardTabs,
    int DashboardTiles,
    IReadOnlyList<string> Messages)
{
    public bool AnyFailures => Messages.Any(m =>
        m.StartsWith("✗", StringComparison.Ordinal) ||
        m.Contains("failed", StringComparison.OrdinalIgnoreCase));
}
