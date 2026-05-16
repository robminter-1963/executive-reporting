using System.Text.Json;
using System.Text.Json.Serialization;

namespace TleReportingDashboard.Web.Services;

// Shared JsonSerializerOptions for every System.Text.Json call across
// services + pages + models. Four near-identical private clones used to
// live across ThemeService, SchemaConfigStore, PromotionPackageService,
// and QueryRequestFactory; a dozen razor sites also passed inline
// `new JsonSerializerOptions { ... }` with subtly different shapes.
//
// Why a single source matters: PropertyNameCaseInsensitive must be true
// on EVERY read path that round-trips through any service-managed JSON,
// or values written by Theme/Schema can fail to deserialize through
// GridTemplate / Library readers. Centralizing also lets us add (e.g.)
// a custom converter once and have it apply everywhere.
public static class AppJson
{
    // Day-to-day JSON: compact (no indentation), case-insensitive read.
    // Use for persisted configs, schedule patterns, column-state blobs,
    // anything that isn't shown to a human as text.
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    // Pretty-printed JSON for admin diagnostic surfaces (Show SQL
    // dialog, Schema history preview, JSON viewer). Same read semantics
    // as Compact; only the write path differs.
    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // Pretty-printed JSON that omits null properties. Used by config
    // previews where unset fields would just be noise (Schema Builder's
    // preview tab: MaxRowLimit / CommandTimeout / etc. that are often
    // null). Preserves the original SchemaBuilder behavior pre-AppJson
    // consolidation — the inline `DefaultIgnoreCondition = WhenWritingNull`
    // lived only at that one site.
    public static readonly JsonSerializerOptions IndentedSkipNulls = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
