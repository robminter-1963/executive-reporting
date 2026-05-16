using System.Text.Json;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Single source of truth for "build a QueryRequest from a SavedReport".
// Every consumer that runs a saved report (Report Viewer, Master
// Dashboard tile, Detail Viewer, scheduled report Worker) calls this
// instead of reconstructing the request inline. Adding a new saved
// knob (something serialized into RPT_saved_reports.column_state or
// RPT_saved_reports.filters / aggregations / primary_table / etc.)
// becomes a one-line change here — every consumer picks it up
// automatically.
//
// Why this exists: prior duplication left consumers drifting out of
// sync (the Worker missed PrimaryTable, then JsonElement-typed filters,
// then AdvancedFilters and CustomFilterIds — three rounds of debugging
// for the same class of problem). One factory eliminates the drift
// surface area structurally.
//
// Per-surface concerns (page size, runtime row-level scoping, fallback
// PK columns looked up from the live connection schema, dashboard
// extra-group-by fields, hidden columns for exports, etc.) are NOT in
// here — callers add them after the factory call. The factory captures
// only knobs that are a pure function of the SavedReport row.
public static class QueryRequestFactory
{
    // Build a QueryRequest with every saved-report knob applied.
    // Callers can mutate the returned instance to layer per-surface
    // bits on top — page size, scoping, fallback sort columns, etc.
    public static QueryRequest FromSavedReport(
        SavedReport report,
        int pageSize = 50000,
        List<string>? fallbackSortColumns = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        // ── field_ids ───────────────────────────────────────────────
        var fieldIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(report.FieldIds))
        {
            try
            {
                fieldIds = JsonSerializer.Deserialize<List<string>>(report.FieldIds) ?? new();
            }
            catch
            {
                // Malformed FieldIds → empty list. Caller decides
                // whether that's a fatal config issue (Worker treats
                // it as such; the Builder can repair).
            }
        }

        // ── filters (with JsonElement → CLR unwrap) ────────────────
        // System.Text.Json deserializes `object?` to JsonElement; SqlClient
        // can't bind those as parameters. Unwrap to native primitives so
        // the same shape works regardless of whether the dict is built
        // from typed UI state (Web Builder) or rehydrated from JSON
        // (Viewer / tiles / Worker).
        var filters = ParseFiltersWithUnwrap(report.Filters);

        // ── aggregations ────────────────────────────────────────────
        var aggregations = !string.IsNullOrWhiteSpace(report.Aggregations)
            ? TryDeserialize<Dictionary<string, string>>(report.Aggregations)
            : null;

        // ── column_state knobs ──────────────────────────────────────
        // Distinct, multi-column sort, CustomFilterIds, AdvancedFilters,
        // TableCalculatedColumns. Parsed once from the same JSON document.
        var cs = ParseColumnState(report.ColumnState);

        // Resolve the effective primary + secondary sort. Priority
        // matches what the Builder writes: multi-sort (TableSort) wins
        // when populated; falls back to legacy single-field columns.
        var (primarySort, primaryDir, secondarySort, secondaryDir) = cs.ResolveSort();

        return new QueryRequest
        {
            FieldIds = fieldIds,
            Filters = filters,
            Aggregations = aggregations,
            ConnectionId = report.ConnectionId,
            PrimaryTable = report.PrimaryTable,

            // Knobs sourced from column_state.
            Distinct = cs.Distinct,
            CustomFilterIds = cs.CustomFilterIds,
            AdvancedFilters = cs.AdvancedFilters,
            TableCalcSqlColumns = cs.TableCalcs?
                .Where(c => !string.IsNullOrWhiteSpace(c.SqlExpression) && !string.IsNullOrEmpty(c.Key))
                .Select(c => new TableCalcSqlSelector
                {
                    Key = c.Key,
                    SqlExpression = c.SqlExpression!,
                    JoinIds = c.JoinIds
                })
                .ToList(),

            SortField = primarySort,
            SortDirection = primaryDir,
            SecondarySortField = secondarySort,
            SecondarySortDirection = secondaryDir,
            FallbackSortColumns = fallbackSortColumns,

            Page = 1,
            PageSize = pageSize
        };
    }

    // Exposed so callers (export builders, hidden-column UI logic)
    // can reuse the same "drop columns the user hid" semantics
    // without duplicating the JSON parse.
    public static List<string>? ExtractHiddenColumns(string? columnStateJson)
    {
        if (string.IsNullOrWhiteSpace(columnStateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(columnStateJson);
            if (doc.RootElement.TryGetProperty("HiddenColumns", out var el))
                return JsonSerializer.Deserialize<List<string>>(el.GetRawText());
        }
        catch { /* malformed — treat as no hidden columns */ }
        return null;
    }

    // ── private helpers ────────────────────────────────────────────

    private static Dictionary<string, object?> ParseFiltersWithUnwrap(string? filtersJson)
    {
        if (string.IsNullOrWhiteSpace(filtersJson))
            return new Dictionary<string, object?>();

        Dictionary<string, JsonElement>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(filtersJson);
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
        if (raw is null) return new Dictionary<string, object?>();

        var result = new Dictionary<string, object?>(raw.Count);
        foreach (var (k, v) in raw)
        {
            result[k] = UnwrapJsonElement(v);
        }
        return result;
    }

    // Maps JsonElement → the CLR types HeaderFilters produces from
    // typed UI state, so SQL parameter binding works regardless of
    // whether the dict came from a JSON round-trip or from live UI.
    private static object? UnwrapJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        // Prefer Int64 for whole numbers — keeps SqlDbType inference
        // on integer parameters.
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        // Multi-select code-set filters serialize as arrays of strings.
        // QueryBuilder's IN-clause path expects List<string>; anything
        // else is a config bug surfaced as a SQL error downstream.
        JsonValueKind.Array  => el.EnumerateArray()
                                   .Select(e => e.ValueKind == JsonValueKind.String
                                       ? e.GetString() ?? string.Empty
                                       : e.ToString())
                                   .ToList(),
        _ => el.ToString()
    };

    private static T? TryDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, AppJson.Compact); }
        catch { return default; }
    }

    private static ColumnStateView ParseColumnState(string? json)
    {
        var view = new ColumnStateView();
        if (string.IsNullOrWhiteSpace(json)) return view;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Distinct", out var dEl)
                && dEl.ValueKind == JsonValueKind.True)
                view.Distinct = true;

            if (root.TryGetProperty("CustomFilterIds", out var cfEl))
                view.CustomFilterIds = TryDeserialize<List<string>>(cfEl.GetRawText());

            // AdvancedFilters back-compat: new saves emit a single
            // {Op, Items[]} group object; very early experimental saves
            // emitted a flat clause array, which we wrap into a root
            // AND-group so the WHERE tree is uniform downstream.
            if (root.TryGetProperty("AdvancedFilters", out var afEl))
            {
                if (afEl.ValueKind == JsonValueKind.Object)
                {
                    view.AdvancedFilters = TryDeserialize<FilterGroup>(afEl.GetRawText());
                }
                else if (afEl.ValueKind == JsonValueKind.Array)
                {
                    var flat = TryDeserialize<List<FilterClause>>(afEl.GetRawText()) ?? new();
                    if (flat.Count > 0)
                    {
                        view.AdvancedFilters = new FilterGroup
                        {
                            Op = FilterGroupOps.And,
                            Items = flat.Select(c => new FilterItem { Clause = c }).ToList()
                        };
                    }
                }
            }

            if (root.TryGetProperty("TableCalculatedColumns", out var tcEl))
                view.TableCalcs = TryDeserialize<List<TableCalcColumnDef>>(tcEl.GetRawText());

            // Multi-sort tuples: [{ Field, Direction }, ...]. First
            // entry = primary, second = secondary. Falls back through
            // the same priority chain the editor's
            // ResolveEffectiveSortField uses:
            //   1. TableSort (header-click multi-sort)
            //   2. DetailGroupBy primary + DetailSort secondary
            //      (Group By dropdown drives primary, Sort By drops to secondary)
            //   3. DetailSort primary + DetailSortThenBy secondary
            //   4. Legacy TableSortField/Direction
            // Worker, Viewer, Tile, and Detail Viewer all need the
            // same order-by, so the chain belongs here rather than
            // duplicated in each consumer.
            if (root.TryGetProperty("TableSort", out var tsEl)
                && tsEl.ValueKind == JsonValueKind.Array)
            {
                var arr = tsEl.EnumerateArray().ToList();
                if (arr.Count > 0)
                {
                    view.PrimarySortField = arr[0].TryGetProperty("Field", out var f) ? f.GetString() : null;
                    view.PrimarySortDirection = arr[0].TryGetProperty("Direction", out var d) ? d.GetString() : null;
                }
                if (arr.Count > 1)
                {
                    view.SecondarySortField = arr[1].TryGetProperty("Field", out var f) ? f.GetString() : null;
                    view.SecondarySortDirection = arr[1].TryGetProperty("Direction", out var d) ? d.GetString() : null;
                }
            }

            if (string.IsNullOrEmpty(view.PrimarySortField))
            {
                // Pull all the dropdown-sourced sort fields once so the
                // priority chain below stays compact.
                string? gbf = root.TryGetProperty("DetailGroupByFieldId", out var dgfEl) ? dgfEl.GetString() : null;
                string? gbd = root.TryGetProperty("DetailGroupByDirection", out var dgdEl) ? dgdEl.GetString() : null;
                string? sbf = root.TryGetProperty("DetailSortFieldId", out var dsfEl) ? dsfEl.GetString() : null;
                string? sbd = root.TryGetProperty("DetailSortDirection", out var dsdEl) ? dsdEl.GetString() : null;
                string? tbf = root.TryGetProperty("DetailSortThenByFieldId", out var dtfEl) ? dtfEl.GetString() : null;
                string? tbd = root.TryGetProperty("DetailSortThenByDirection", out var dtdEl) ? dtdEl.GetString() : null;

                if (!string.IsNullOrWhiteSpace(gbf)
                    && !string.Equals(gbd, "none", StringComparison.OrdinalIgnoreCase))
                {
                    view.PrimarySortField = gbf;
                    view.PrimarySortDirection = string.Equals(gbd, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
                    if (!string.IsNullOrWhiteSpace(sbf)
                        && !string.Equals(sbf, gbf, StringComparison.OrdinalIgnoreCase))
                    {
                        view.SecondarySortField = sbf;
                        view.SecondarySortDirection = string.Equals(sbd, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
                    }
                }
                else if (!string.IsNullOrWhiteSpace(sbf))
                {
                    view.PrimarySortField = sbf;
                    view.PrimarySortDirection = string.Equals(sbd, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
                    if (!string.IsNullOrWhiteSpace(tbf)
                        && !string.Equals(tbf, sbf, StringComparison.OrdinalIgnoreCase))
                    {
                        view.SecondarySortField = tbf;
                        view.SecondarySortDirection = string.Equals(tbd, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
                    }
                }
            }

            if (string.IsNullOrEmpty(view.PrimarySortField)
                && root.TryGetProperty("TableSortField", out var tsfEl))
                view.PrimarySortField = tsfEl.GetString();
            if (string.IsNullOrEmpty(view.PrimarySortDirection)
                && root.TryGetProperty("TableSortDirection", out var tsdEl))
                view.PrimarySortDirection = tsdEl.GetString();
        }
        catch
        {
            // Malformed column_state shouldn't crash the request build.
            // The query emits without those knobs — surfaces as wrong
            // row counts which the next run logs/reports.
        }

        return view;
    }

    // Internal flat view of column_state knobs. Lets ParseColumnState
    // do one pass and the factory consume named fields without
    // re-traversing the JSON document.
    private sealed class ColumnStateView
    {
        public bool Distinct { get; set; }
        public List<string>? CustomFilterIds { get; set; }
        public FilterGroup? AdvancedFilters { get; set; }
        public List<TableCalcColumnDef>? TableCalcs { get; set; }
        public string? PrimarySortField { get; set; }
        public string? PrimarySortDirection { get; set; }
        public string? SecondarySortField { get; set; }
        public string? SecondarySortDirection { get; set; }

        public (string? Primary, string? PrimaryDir, string? Secondary, string? SecondaryDir)
            ResolveSort() => (PrimarySortField, PrimarySortDirection, SecondarySortField, SecondarySortDirection);
    }
}
