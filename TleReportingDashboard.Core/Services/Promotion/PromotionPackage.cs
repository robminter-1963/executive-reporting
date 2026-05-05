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
public sealed class PromotionPackage
{
    // Stays at 1 until a non-additive wire change forces a bump. Import
    // refuses anything that doesn't match what it knows how to read.
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
    // chose to export gets populated; the rest stay null. Adding a new
    // section here is additive and doesn't bump PackageVersion.
    [JsonPropertyName("schemaConfigs")]
    public List<SchemaConfigEntry> SchemaConfigs { get; set; } = new();

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
}
