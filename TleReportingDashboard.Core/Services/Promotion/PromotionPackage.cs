using System.Text.Json.Serialization;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.Promotion;

// Wire-format for a "push these changes from staging to production" bundle.
// Serialized to JSON in Staging (Admin → Promotion → Export), saved to a
// file the admin downloads, then re-uploaded in Production (Admin →
// Promotion → Import) where it gets applied against named targets.
//
// Versioning: PackageVersion is bumped when the wire format changes in a
// non-additive way; the import path refuses unknown versions instead of
// silently mis-mapping fields. Additive changes (new optional sections,
// new fields with defaults) keep the version where it is.
//
// Cross-environment identity: GUIDs differ per environment, so nothing is
// matched by id across the wire. Companies match by Code, connections by
// Name (within a company), library sections by Name, grid templates by
// (Name + connection), reports by InternalName/Name. Each entry that other
// entries point at also carries its SOURCE GUID (SourceId) purely as an
// intra-package link key — the importer builds a source-id → target-id map
// as it creates/matches each target, then resolves the dependent entries
// against that map. SourceIds are never written to the target DB.
public sealed class PromotionPackage
{
    // Stays at 1 until a non-additive wire change forces a bump. Import
    // refuses anything that doesn't match what it knows how to read.
    // Reports / dashboards / companies / taxonomy were all added as
    // additive sections, so the version stayed at 1.
    public const int CurrentVersion = 1;

    [JsonPropertyName("packageVersion")]
    public int PackageVersion { get; set; } = CurrentVersion;

    // Source-environment label so the importer can show "from STAGING"
    // and gate against Promotion:AllowedSourceEnvironments. Free-form
    // string — comparison is case-insensitive in the importer.
    [JsonPropertyName("sourceEnvironment")]
    public string SourceEnvironment { get; set; } = string.Empty;

    [JsonPropertyName("exportedAtUtc")]
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("exportedBy")]
    public string? ExportedBy { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    // Sections — each is independently importable. Only what the admin
    // chose to export gets populated; the rest stay empty. Adding a new
    // section here is additive and doesn't bump PackageVersion.
    //
    // DB Connections are intentionally NOT a section: they carry live
    // credentials and are environment-specific. Reports / dashboards /
    // grid templates that reference a connection travel with the source
    // connection's NAME and resolve against a connection that already
    // exists in the target environment.

    [JsonPropertyName("schemaConfigs")]
    public List<SchemaConfigEntry> SchemaConfigs { get; set; } = new();

    [JsonPropertyName("companies")]
    public List<CompanyEntry> Companies { get; set; } = new();

    [JsonPropertyName("librarySections")]
    public List<LibrarySectionEntry> LibrarySections { get; set; } = new();

    [JsonPropertyName("gridTemplates")]
    public List<GridTemplateEntry> GridTemplates { get; set; } = new();

    [JsonPropertyName("reports")]
    public List<ReportEntry> Reports { get; set; } = new();

    [JsonPropertyName("dashboards")]
    public List<DashboardEntry> Dashboards { get; set; } = new();

    public sealed class SchemaConfigEntry
    {
        // Identifier the importer maps to a target connection — admins
        // pick which prod connection a given entry lands on, since the
        // GUIDs differ per environment. SourceConnectionName is the
        // human label the source connection had in staging, so the
        // import UI can show "Schema for connection 'TLE Empower'".
        [JsonPropertyName("sourceConnectionName")]
        public string SourceConnectionName { get; set; } = string.Empty;

        [JsonPropertyName("sourceCompanyName")]
        public string SourceCompanyName { get; set; } = string.Empty;

        [JsonPropertyName("schema")]
        public SchemaConfig Schema { get; set; } = new();
    }

    // A tenant company. Matched on import by Code (the stable natural key);
    // when no target company shares the code the importer can create one.
    public sealed class CompanyEntry
    {
        [JsonPropertyName("sourceId")]
        public Guid SourceId { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("websiteUrl")]
        public string? WebsiteUrl { get; set; }

        [JsonPropertyName("displayOrder")]
        public int DisplayOrder { get; set; }

        [JsonPropertyName("isHidden")]
        public bool IsHidden { get; set; }
    }

    // A Report Library section. Per-company; matched by (company, Name).
    public sealed class LibrarySectionEntry
    {
        [JsonPropertyName("sourceId")]
        public Guid SourceId { get; set; }

        // Owning company's source GUID — resolved through the company map.
        [JsonPropertyName("sourceCompanyId")]
        public Guid SourceCompanyId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }
    }

    // A grid template. Owner-scoped + connection-scoped in the model.
    // Matched on import by (Name + resolved target connection); owner is
    // reassigned to the importing admin. Connection travels by name.
    public sealed class GridTemplateEntry
    {
        [JsonPropertyName("sourceId")]
        public Guid SourceId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("isShared")]
        public bool IsShared { get; set; }

        [JsonPropertyName("fieldIds")]
        public string FieldIds { get; set; } = "[]";

        [JsonPropertyName("columnState")]
        public string? ColumnState { get; set; }

        // The owning connection's source GUID + name. The company the
        // connection belongs to lets the importer resolve the target
        // connection within the right company.
        [JsonPropertyName("sourceConnectionId")]
        public Guid? SourceConnectionId { get; set; }

        [JsonPropertyName("sourceConnectionName")]
        public string? SourceConnectionName { get; set; }

        [JsonPropertyName("sourceCompanyId")]
        public Guid? SourceCompanyId { get; set; }
    }

    // A saved report. Matched on import by (target company, InternalName ||
    // Name); owner reassigned to the importing admin. All foreign keys
    // travel as source GUIDs / names and resolve through the import maps.
    public sealed class ReportEntry
    {
        [JsonPropertyName("sourceId")]
        public Guid SourceId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("internalName")]
        public string? InternalName { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        // Owning company (source GUID, resolved via company map). The name
        // travels too so the import UI can label the company-mapping row even
        // when the package didn't include the Companies or Dashboards section.
        [JsonPropertyName("sourceCompanyId")]
        public Guid SourceCompanyId { get; set; }

        [JsonPropertyName("sourceCompanyName")]
        public string? SourceCompanyName { get; set; }

        // Data-source connection — resolved by name within the target
        // company. Null = report inherited the company's default connection.
        [JsonPropertyName("sourceConnectionId")]
        public Guid? SourceConnectionId { get; set; }

        [JsonPropertyName("sourceConnectionName")]
        public string? SourceConnectionName { get; set; }

        // Optional linked grid template (source GUID, resolved via the
        // grid-template map). Null when the report doesn't link a template.
        [JsonPropertyName("sourceGridTemplateId")]
        public Guid? SourceGridTemplateId { get; set; }

        // Optional library section (source GUID, resolved via section map).
        [JsonPropertyName("sourceLibrarySectionId")]
        public Guid? SourceLibrarySectionId { get; set; }

        [JsonPropertyName("fieldIds")]
        public string FieldIds { get; set; } = string.Empty;

        [JsonPropertyName("filters")]
        public string? Filters { get; set; }

        [JsonPropertyName("aggregations")]
        public string? Aggregations { get; set; }

        [JsonPropertyName("columnState")]
        public string? ColumnState { get; set; }

        [JsonPropertyName("primaryTable")]
        public string? PrimaryTable { get; set; }
    }

    // A company's Master Dashboard layout — the full tab / section / tile
    // tree. Tiles reference reports by source GUID (resolved via the report
    // map), falling back to InternalName for reports imported in an earlier
    // run. Sections are linked to their tab positionally within the entry.
    public sealed class DashboardEntry
    {
        [JsonPropertyName("sourceCompanyId")]
        public Guid SourceCompanyId { get; set; }

        [JsonPropertyName("sourceCompanyName")]
        public string SourceCompanyName { get; set; } = string.Empty;

        [JsonPropertyName("tabs")]
        public List<TabEntry> Tabs { get; set; } = new();
    }

    public sealed class TabEntry
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "Dashboard";

        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        [JsonPropertyName("titleAlign")]
        public string TitleAlign { get; set; } = "left";

        [JsonPropertyName("sections")]
        public List<SectionEntry> Sections { get; set; } = new();

        [JsonPropertyName("tiles")]
        public List<TileEntry> Tiles { get; set; } = new();
    }

    public sealed class SectionEntry
    {
        // Source GUID isn't carried — sections are matched by (tab, Name)
        // and tiles reference them by SectionKey (the section's name) so
        // the link survives the id remap.
        [JsonPropertyName("label")]
        public string Label { get; set; } = "Section";

        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        [JsonPropertyName("titleAlign")]
        public string TitleAlign { get; set; } = "left";

        [JsonPropertyName("collapsed")]
        public bool Collapsed { get; set; }
    }

    public sealed class TileEntry
    {
        // Report link — source GUID first, InternalName as the fallback for
        // reports that were imported in a previous run (different GUID, same
        // internal name).
        [JsonPropertyName("sourceReportId")]
        public Guid SourceReportId { get; set; }

        [JsonPropertyName("reportInternalName")]
        public string? ReportInternalName { get; set; }

        [JsonPropertyName("reportName")]
        public string? ReportName { get; set; }

        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        [JsonPropertyName("colSpan")]
        public int ColSpan { get; set; } = 12;

        [JsonPropertyName("height")]
        public int Height { get; set; } = 500;

        // Name of the section this tile sits under, or null for the
        // "(no section)" bucket. Matched against the tab's SectionEntry list.
        [JsonPropertyName("sectionLabel")]
        public string? SectionLabel { get; set; }
    }
}
