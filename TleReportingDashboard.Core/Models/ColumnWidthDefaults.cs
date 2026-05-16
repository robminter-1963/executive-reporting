namespace TleReportingDashboard.Web.Models;

// Admin-managed per-data-type default column widths for report grids.
// Stored as a single JSON blob in RPT_app_settings under the
// AppSettingKeys.ColumnWidthDefaults key. Two width tiers — Compact
// (Report Viewer, dashboard tile drill-down) and Wide (Report Builder
// table view, Detail Viewer) — let admins keep dense views dense while
// expanding editor / drill-down views for readability.
//
// Lookup falls back through:
//   1. exact data-type key (case-insensitive)
//   2. "text" key (the generic catch-all)
//   3. hard-coded entry on this class (defensive — only fires if "text"
//      was somehow deleted from the dictionary)
public sealed class ColumnWidthDefaults
{
    public Dictionary<string, ColumnWidthEntry> Types { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Resolves the width entry for a given data type. Never returns null —
    // unknown / unset types collapse to the "text" entry, then to a
    // hard-coded fallback. Callers can use either tier of the returned
    // entry depending on which renderer they're in.
    public ColumnWidthEntry Resolve(string? dataType)
    {
        if (!string.IsNullOrEmpty(dataType) && Types.TryGetValue(dataType, out var hit))
            return hit;
        if (Types.TryGetValue("text", out var fallback))
            return fallback;
        return new ColumnWidthEntry
        {
            CompactMin = 70, CompactMax = 150,
            WideMin    = 70, WideMax    = 150
        };
    }

    // Seed values — these are the defaults the renderers had hard-coded
    // before the admin tab existed. Used by:
    //   * ColumnWidthDefaultsService on first read when nothing's saved
    //   * AdminColumnWidthsTab's "Reset to defaults" button
    public static ColumnWidthDefaults Seed() => new()
    {
        Types =
        {
            ["currency"] = new() { CompactMin = 70,  CompactMax = 100, WideMin = 110, WideMax = 140 },
            ["money"]    = new() { CompactMin = 70,  CompactMax = 100, WideMin = 110, WideMax = 140 },
            ["percent"]  = new() { CompactMin = 80,  CompactMax = 100, WideMin = 90,  WideMax = 110 },
            ["date"]     = new() { CompactMin = 90,  CompactMax = 100, WideMin = 100, WideMax = 120 },
            ["datetime"] = new() { CompactMin = 100, CompactMax = 120, WideMin = 160, WideMax = 180 },
            ["integer"]  = new() { CompactMin = 35,  CompactMax = 50,  WideMin = 80,  WideMax = 100 },
            ["int"]      = new() { CompactMin = 35,  CompactMax = 50,  WideMin = 80,  WideMax = 100 },
            ["decimal"]  = new() { CompactMin = 70,  CompactMax = 150, WideMin = 90,  WideMax = 120 },
            ["phone"]    = new() { CompactMin = 70,  CompactMax = 150, WideMin = 130, WideMax = 150 },
            ["boolean"]  = new() { CompactMin = 70,  CompactMax = 150, WideMin = 60,  WideMax = 80 },
            ["text"]     = new() { CompactMin = 70,  CompactMax = 150, WideMin = 70,  WideMax = 150 }
        }
    };

    // Canonical data-type keys the admin UI shows as editable rows. Other
    // (admin-defined) types fall back to "text" at resolve time. Aliases
    // — money / int — are present in Seed() but hidden from the UI; the
    // admin edits the canonical name (currency, integer) and the alias
    // gets cloned on save so both keys stay in sync.
    public static readonly IReadOnlyList<string> EditableTypes = new[]
    {
        "text", "currency", "percent", "date", "datetime",
        "integer", "decimal", "phone", "boolean"
    };

    // Alias pairs kept in sync on save: when the admin edits "currency",
    // the same entry is written under "money" too (and likewise integer
    // ↔ int). Keeps the dictionary's resolve path single-key without
    // forcing admins to remember which alias the schema happens to use.
    public static readonly IReadOnlyList<(string Canonical, string Alias)> Aliases = new[]
    {
        ("currency", "money"),
        ("integer",  "int")
    };
}

public sealed class ColumnWidthEntry
{
    public int CompactMin { get; set; }
    public int CompactMax { get; set; }
    public int WideMin    { get; set; }
    public int WideMax    { get; set; }
}
