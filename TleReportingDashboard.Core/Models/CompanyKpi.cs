namespace TleReportingDashboard.Web.Models;

// One KPI card on a company's Master Dashboard. Lives above the tab
// strip and is visible across every tab on that company (per-company
// scope, not per-tab). Visibility for the whole band is gated by
// RPT_companies.show_kpi_band — when that's off, the band is hidden
// regardless of how many KPIs are defined.
//
// The render format isn't stored — it's auto-derived at render time
// from the field's DataType in the schema config. That way changing
// a field's type (currency → decimal, for example) flows through to
// every KPI using it without a separate migration.
public class CompanyKpi
{
    public Guid Id { get; set; }

    // Tenant + data source. ConnectionId scopes the query to the right
    // database; PrimaryTable picks the root the aggregation runs against.
    public Guid CompanyId { get; set; }
    public Guid ConnectionId { get; set; }
    public string PrimaryTable { get; set; } = string.Empty;

    // Optional admin override for the card's display label. When null
    // the card shows the field's Label from the schema config.
    public string? Label { get; set; }

    // The numeric field to aggregate (FieldConfig.Id).
    public string FieldId { get; set; } = string.Empty;

    // sum | count | avg | min | max
    public string Aggregation { get; set; } = "sum";

    // Optional period filter. When DateFieldId is set, the aggregation
    // runs only over rows where that date field falls inside Period.
    //   mtd        — month-to-date
    //   qtd        — quarter-to-date
    //   ytd        — year-to-date
    //   last_30d   — rolling 30 days back from now
    //   last_90d   — rolling 90 days back from now
    // When DateFieldId is null, Period is ignored.
    public string? DateFieldId { get; set; }
    public string? Period { get; set; }

    // When true, the card shows a delta chip comparing the current value
    // to the same-length window immediately before. Requires a non-null
    // Period; ignored otherwise.
    public bool ComparePrevious { get; set; }

    // Explicit from/to bounds. Honored only when Period == "custom". Other
    // period values resolve their window from KpiPeriods.Resolve and ignore
    // these. UTC; the dialog widget binds via local time but converts.
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    // Pre-defined custom filters from the connection's schema config. Each
    // id matches a CustomFilterDefinition.Id; QueryRequest.CustomFilterIds
    // turns them on for the KPI's single query. Null/empty = none active.
    public List<string>? CustomFilterIds { get; set; }

    // Ad-hoc per-field filter values. Same shape as ReportConfig.Filters
    // (and QueryRequest.Filters): the key is the field id, the value is
    // the literal to match (string / number / bool / list). Null/empty
    // means no extra value filters. Layered AND with the date window and
    // custom filters at SQL emission.
    public Dictionary<string, object?>? Filters { get; set; }

    // Position in the band (12-column grid, same as tile col_span).
    public int ColSpan { get; set; } = 3;

    // Drag/drop ordering — small to large = left to right in the band.
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedByEmail { get; set; }
}

// One supported aggregation function. Centralized so the dialog
// dropdown, the SQL emitter, and the cache key all agree on the
// canonical strings.
public static class KpiAggregations
{
    public const string Sum   = "sum";
    public const string Count = "count";
    public const string Avg   = "avg";
    public const string Min   = "min";
    public const string Max   = "max";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
        new[]
        {
            (Sum,   "Sum"),
            (Count, "Count"),
            (Avg,   "Average"),
            (Min,   "Minimum"),
            (Max,   "Maximum"),
        };

    public static string Display(string? key) =>
        All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)).Label
            ?? Sum;

    // Maps to the QueryRequest.Aggregations dict's expected uppercase tokens.
    public static string ToSqlOp(string? key) => (key ?? Sum).ToLowerInvariant() switch
    {
        Count => "COUNT",
        Avg   => "AVG",
        Min   => "MIN",
        Max   => "MAX",
        _     => "SUM"
    };
}

// One supported period filter. Same purpose as KpiAggregations —
// canonical strings + display labels + a date-range resolver.
public static class KpiPeriods
{
    public const string Mtd      = "mtd";
    public const string Qtd      = "qtd";
    public const string Ytd      = "ytd";
    public const string Last30d  = "last_30d";
    public const string Last90d  = "last_90d";
    // Custom range — the KPI carries explicit DateFrom + DateTo bounds
    // instead of computing them from "now". Picked by admins who want a
    // fixed window (e.g. a fiscal quarter that doesn't align to QTD).
    public const string Custom   = "custom";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
        new[]
        {
            (Mtd,     "Month to date"),
            (Qtd,     "Quarter to date"),
            (Ytd,     "Year to date"),
            (Last30d, "Last 30 days"),
            (Last90d, "Last 90 days"),
            (Custom,  "Custom range"),
        };

    public static string Display(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Label
            ?? string.Empty;

    // Returns the (from, to) UTC window for the period, anchored on
    // the supplied "now" so callers can swap in a test clock.
    public static (DateTime From, DateTime To) Resolve(string period, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        return period switch
        {
            Mtd     => (new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc), nowUtc),
            Qtd     => (new DateTime(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc), nowUtc),
            Ytd     => (new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc), nowUtc),
            Last30d => (today.AddDays(-30), nowUtc),
            Last90d => (today.AddDays(-90), nowUtc),
            _       => (DateTime.MinValue, DateTime.MaxValue)
        };
    }

    // Returns the immediately-preceding window of the same length,
    // for "compare to previous period" deltas.
    public static (DateTime From, DateTime To) ResolvePrevious(string period, DateTime nowUtc)
    {
        var (from, to) = Resolve(period, nowUtc);
        var len = to - from;
        return (from - len, from);
    }
}

// One KPI's computed value(s) for a single render. Held in the
// MasterDashboard's _kpiData dict alongside _tileData.
//   Current        — the aggregated number from this period
//   Previous       — null unless the KPI has ComparePrevious set and the
//                    second query succeeded; the same aggregation over
//                    the immediately-preceding window of the same length
//   DeltaPercent   — null unless both Current and Previous are populated
//                    AND Previous is non-zero. Computed as
//                    (Current - Previous) / Previous, signed.
//   Error          — non-null when the query couldn't run; the card
//                    renders a muted "—" instead of a value.
public sealed record KpiResult(
    decimal? Current,
    decimal? Previous,
    decimal? DeltaPercent,
    string? Error)
{
    public static KpiResult Empty => new(null, null, null, null);
    public static KpiResult FromError(string error) => new(null, null, null, error);
}

// Captured query state for the right-click "Show SQL" dialog. Populated
// during KPI compute so admins can see exactly what SQL ran (or what
// would have run, when the execute path threw) without tailing logs.
//   Sql        — emitted SQL string (always present, even on failures —
//                we fall back to the build-only path when execute fails)
//   Parameters — bound parameter dict (name → value)
//   Error      — null on success; the error message when execute threw
public sealed record KpiQueryInfo(
    string Sql,
    Dictionary<string, object?>? Parameters,
    string? Error);

