using System.Data.Common;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

// Shared advanced-filter SQL emission used by BOTH the Web path
// (QueryBuilder, backing the in-app Run button + DetailViewer +
// MasterDashboard tiles) and the Worker path (SqlEmitter, backing
// scheduled email exports). Kept as a single canonical implementation so
// adding an operator or tweaking emission happens in one place.
//
// Callers supply:
//   * the filter tree (FilterGroup with clauses + nested groups)
//   * a resolver: field-id → FilterableField? (their type's adapter)
//   * the dialect and the parameters list to append to
//
// Each leaf clause is translated into one parameterized WHERE fragment.
// Groups render as parenthesized expressions combined by their Op
// (AND / OR). Parameters use "@af_p{N}" (N = current parameters.Count)
// to avoid collision with other parameter prefixes in either pipeline.
public static class AdvancedFilterSqlEmitter
{
    // The minimum field info the emitter needs. Both FieldConfig (Web)
    // and FieldDefinition (Worker) can project into this shape — they
    // share all these property names and types already.
    public sealed class FilterableField
    {
        public string? SqlExpression { get; init; }
        public string SourceTable { get; init; } = string.Empty;
        public string SourceColumn { get; init; } = string.Empty;
        public string DataType { get; init; } = string.Empty;
        public string? DisplayTimezone { get; init; }

        // Mirrors FieldConfig/FieldDefinition.ApplyTimezoneConversion. The
        // pipeline paints DisplayTimezone onto every referenced field at
        // emission time, so DisplayTimezone alone is not a sufficient signal
        // for "should this field's date filter wrap with AT TIME ZONE" —
        // only fields explicitly flagged for conversion should wrap.
        public bool ApplyTimezoneConversion { get; init; }

        // LookupType binding for the chip-picker → admin-authored
        // FilterPredicateSql path. When both are set, InList/NotInList
        // (and Equals/NotEquals) emit the admin's predicate against the
        // joined lookup table instead of comparing the field's own column
        // to the user-picked codes. Matches the SqlEmitter.BuildFilterWhereClauses
        // behavior so Advanced Filters and Header Filters generate the same
        // WHERE for the same field + picked values.
        public string? LookupTypeId { get; init; }
        public string? FilterPredicateSql { get; init; }

        // Alternate column for the WHERE comparison when this field is
        // filtered via a LookupType. See Configuration.FieldDefinition.FilterColumn.
        // Honored as a fallback when FilterPredicateSql isn't set, before
        // the legacy "field's own column" path.
        public string? FilterColumn { get; init; }

        // Non-date LHS expression — inline SqlExpression if present,
        // otherwise the qualified column reference. Date-typed fields
        // bypass this and go through BuildDateLhs (which always casts
        // to DATE).
        internal string GetRawExpression() =>
            !string.IsNullOrWhiteSpace(SqlExpression)
                ? SqlExpression!
                : $"{SourceTable}.{SourceColumn}";
    }

    // Top-level entry. Returns the WHERE fragment (with outer parens)
    // that the caller ANDs onto its WHERE list, or empty string when
    // the tree contains no usable clauses.
    //
    // lookupTypes (optional): the connection's LookupTypes catalog. When
    // a clause's field carries a LookupTypeId + FilterPredicateSql, the
    // emitter substitutes the admin's predicate (with {values} or implicit
    // IN-append) instead of comparing the field's own column to the
    // user-picked codes. Pass null/empty when LookupType binding isn't
    // applicable — falls back to the legacy plain-column comparison.
    public static string BuildGroupExpression(
        FilterGroup group,
        Func<string, FilterableField?> resolve,
        ISqlDialect dialect,
        List<DbParameter> parameters,
        IReadOnlyList<LookupTypeDefinition>? lookupTypes = null)
    {
        var isPostgres = string.Equals(dialect.Name, "postgres", StringComparison.OrdinalIgnoreCase);
        var joinOp = string.Equals(group.Op, FilterGroupOps.Or, StringComparison.OrdinalIgnoreCase)
            ? " OR "
            : " AND ";

        var lookupTypeMap = lookupTypes is { Count: > 0 }
            ? lookupTypes
                .GroupBy(lt => lt.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : null;

        var fragments = new List<string>();
        if (group.Items is not null)
        {
            foreach (var item in group.Items)
            {
                if (item is null) continue;
                string? fragment = null;
                if (item.Clause is not null)
                {
                    fragment = BuildLeafClause(item.Clause, resolve, isPostgres, dialect, parameters, lookupTypeMap);
                }
                else if (item.Group is not null)
                {
                    fragment = BuildGroupExpression(item.Group, resolve, dialect, parameters, lookupTypes);
                }

                if (!string.IsNullOrWhiteSpace(fragment))
                    fragments.Add(fragment);
            }
        }

        if (fragments.Count == 0) return string.Empty;
        if (fragments.Count == 1) return $"({fragments[0]})";
        return "(" + string.Join(joinOp, fragments) + ")";
    }

    // Visits every leaf clause in the tree (for callers that just need
    // the list of field ids — e.g., to feed the join-requirements set).
    public static void Walk(FilterGroup? group, Action<FilterClause> visit)
    {
        if (group?.Items is null) return;
        foreach (var item in group.Items)
        {
            if (item?.Clause is not null) visit(item.Clause);
            else if (item?.Group is not null) Walk(item.Group, visit);
        }
    }

    // ── Clause emission ────────────────────────────────────────────────

    private static string BuildLeafClause(
        FilterClause filter,
        Func<string, FilterableField?> resolve,
        bool isPostgres,
        ISqlDialect dialect,
        List<DbParameter> parameters,
        IReadOnlyDictionary<string, LookupTypeDefinition>? lookupTypeMap)
    {
        if (string.IsNullOrEmpty(filter.FieldId) || string.IsNullOrEmpty(filter.Op))
            return string.Empty;
        var field = resolve(filter.FieldId);
        if (field is null) return string.Empty;
        // Skip fields with neither an SqlExpression nor a usable
        // (table, column) pair. Prevents malformed LHS like "pinfo. = @p0".
        if (string.IsNullOrWhiteSpace(field.SqlExpression)
            && string.IsNullOrWhiteSpace(field.SourceColumn))
            return string.Empty;

        return BuildClause(field, filter, isPostgres, dialect, parameters, lookupTypeMap) ?? string.Empty;
    }

    private static string BuildClause(
        FilterableField field,
        FilterClause filter,
        bool isPostgres,
        ISqlDialect dialect,
        List<DbParameter> parameters,
        IReadOnlyDictionary<string, LookupTypeDefinition>? lookupTypeMap)
    {
        var op = filter.Op;

        // Null checks — type-agnostic.
        if (op == FilterOperators.IsNull)    return $"{field.GetRawExpression()} IS NULL";
        if (op == FilterOperators.IsNotNull) return $"{field.GetRawExpression()} IS NOT NULL";

        // LookupType binding short-circuits. Precedence (highest first):
        //   1. FilterColumn — explicit single-column shortcut. Admin
        //      pointed at the column to compare against; always honor it
        //      first. Picker is responsible for sending Codes.
        //   2. FilterPredicateSql — joined-table escape hatch. Only fires
        //      when FilterColumn is empty AND the predicate has a valid
        //      substitution path ({values} OR SourceTableRef+ValueColumn).
        //   3. Fall through to the plain "<expr> IN (descriptions)"
        //      legacy path.
        if ((op == FilterOperators.InList || op == FilterOperators.NotInList)
            && filter.Values.Count > 0
            && !string.IsNullOrWhiteSpace(field.FilterColumn))
        {
            return BuildFilterColumnClause(field, filter, dialect, parameters);
        }
        if ((op == FilterOperators.InList || op == FilterOperators.NotInList)
            && filter.Values.Count > 0
            && !string.IsNullOrWhiteSpace(field.LookupTypeId)
            && !string.IsNullOrWhiteSpace(field.FilterPredicateSql)
            && lookupTypeMap is not null
            && lookupTypeMap.TryGetValue(field.LookupTypeId!, out var lookupType))
        {
            return BuildLookupTypeClause(field, lookupType, filter, dialect, parameters);
        }

        // Booleans — dialect-aware storage (SQL Server uses CHAR(1) Y/N).
        if (op == FilterOperators.IsTrue)
            return isPostgres
                ? $"{field.GetRawExpression()} = TRUE"
                : $"{field.GetRawExpression()} = 'Y'";
        if (op == FilterOperators.IsFalse)
            return isPostgres
                ? $"{field.GetRawExpression()} = FALSE"
                : $"{field.GetRawExpression()} = 'N'";

        var dtype = field.DataType ?? "";
        var isDateLike = dtype.Equals("date", StringComparison.OrdinalIgnoreCase)
                      || dtype.Equals("datetime", StringComparison.OrdinalIgnoreCase);

        if (isDateLike)
        {
            var lhs = BuildDateLhs(field, isPostgres);
            return BuildDateOpClause(lhs, field, filter, isPostgres, dialect, parameters);
        }

        if (dtype.Equals("text", StringComparison.OrdinalIgnoreCase))
            return BuildTextOpClause(field.GetRawExpression(), filter, isPostgres, dialect, parameters);

        if (dtype is "integer" or "decimal" or "currency" or "percent")
            return BuildNumericOpClause(field.GetRawExpression(), filter, dialect, parameters);

        return string.Empty;
    }

    // Date filter LHS: always CAST AS DATE so day-grained comparison
    // works regardless of whether the underlying column is date or
    // datetime. Postgres adds the TZ wrap only when the field is
    // explicitly flagged ApplyTimezoneConversion AND DisplayTimezone is
    // set on the connection. Without the ApplyTimezoneConversion gate the
    // wrap fires on every datetime field whose connection has a display
    // timezone — i.e. fields stored in local time get an unwanted UTC→tz
    // shift in the WHERE. SQL Server never wraps (stored in local time).
    private static string BuildDateLhs(FilterableField field, bool isPostgres)
    {
        var raw = field.GetRawExpression();
        if (isPostgres)
        {
            if (field.ApplyTimezoneConversion
                && !string.IsNullOrWhiteSpace(field.DisplayTimezone))
            {
                var tz = field.DisplayTimezone!.Replace("'", "''");
                return $"({raw} AT TIME ZONE 'UTC' AT TIME ZONE '{tz}')::date";
            }
            return $"{raw}::date";
        }
        return $"CAST({raw} AS DATE)";
    }

    private static string BuildDateOpClause(
        string lhs,
        FilterableField field,
        FilterClause filter,
        bool isPostgres,
        ISqlDialect dialect,
        List<DbParameter> parameters)
    {
        DateTime? parseDate(int idx)
        {
            if (filter.Values.Count <= idx) return null;
            return DateTime.TryParse(filter.Values[idx],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var d) ? d.Date : null;
        }

        string paramOf(DateTime d)
        {
            var name = "@af_p" + parameters.Count;
            parameters.Add(dialect.CreateParameter(name, d));
            return name;
        }

        switch (filter.Op)
        {
            case FilterOperators.Equals:
                { var d = parseDate(0); return d is null ? string.Empty : $"{lhs} = {paramOf(d.Value)}"; }
            case FilterOperators.NotEquals:
                { var d = parseDate(0); return d is null ? string.Empty : $"{lhs} <> {paramOf(d.Value)}"; }
            case FilterOperators.Before:
                { var d = parseDate(0); return d is null ? string.Empty : $"{lhs} < {paramOf(d.Value)}"; }
            case FilterOperators.After:
                { var d = parseDate(0); return d is null ? string.Empty : $"{lhs} > {paramOf(d.Value)}"; }
            case FilterOperators.OnOrBefore:
                { var d = parseDate(0); return d is null ? string.Empty : $"{lhs} <= {paramOf(d.Value)}"; }
            case FilterOperators.OnOrAfter:
                { var d = parseDate(0); return d is null ? string.Empty : $"{lhs} >= {paramOf(d.Value)}"; }
            case FilterOperators.Between:
                {
                    var d1 = parseDate(0);
                    var d2 = parseDate(1);
                    if (d1 is null || d2 is null) return string.Empty;
                    return $"{lhs} BETWEEN {paramOf(d1.Value)} AND {paramOf(d2.Value)}";
                }
            case FilterOperators.Relative:
                {
                    var preset = filter.Values.Count > 0 ? filter.Values[0] : RelativeDatePresets.Yesterday;
                    return BuildRelativeDateClause(lhs, preset, field, isPostgres);
                }
        }
        return string.Empty;
    }

    private static string BuildRelativeDateClause(string lhs, string preset, FilterableField field, bool isPostgres)
    {
        // Cached date anchors. Each variable holds an expression — not a
        // value — so the SQL is evaluated at query time on the database
        // and the user's "today" line never drifts mid-session.
        //   * firstOfWeek      — Monday of the current week (ISO weekstart)
        //   * firstOfLastWeek  — Monday of the prior week
        //   * firstOfMonth     — 1st of the current calendar month
        //   * firstOfNextMonth — 1st of the following calendar month
        //   * firstOfLastMonth — 1st of the prior calendar month
        //   * firstOfQuarter      — 1st day of the current calendar quarter (Jan/Apr/Jul/Oct)
        //   * firstOfNextQuarter  — 1st day of the next quarter
        //   * firstOfLastQuarter  — 1st day of the prior quarter
        //   * firstOfYear / firstOfLastYear — Jan 1 anchors
        string todayExpr;
        string firstOfWeek, firstOfLastWeek;
        string firstOfMonth, firstOfNextMonth, firstOfLastMonth;
        string firstOfQuarter, firstOfNextQuarter, firstOfLastQuarter;
        string firstOfYear, firstOfLastYear;

        if (isPostgres)
        {
            // "Today" always follows the connection's DisplayTimezone, NOT
            // the field-level ApplyTimezoneConversion flag. The flag controls
            // whether the field's stored value gets shifted (it's already in
            // local time when false). "Today" is the user's wall-clock date,
            // which is the connection's display zone. CURRENT_DATE returns
            // the session zone (typically UTC on managed Postgres), so a
            // late-evening Pacific query computes "today" as tomorrow —
            // wrong reference for any relative comparison.
            todayExpr = !string.IsNullOrWhiteSpace(field.DisplayTimezone)
                ? $"(NOW() AT TIME ZONE '{field.DisplayTimezone!.Replace("'", "''")}')::date"
                : "CURRENT_DATE";
            // date_trunc('week', ...) returns Monday on Postgres — matches
            // the ISO weekstart we want for ThisWeek / LastWeek.
            firstOfWeek        = $"date_trunc('week', {todayExpr})::date";
            firstOfLastWeek    = $"(date_trunc('week', {todayExpr}) - INTERVAL '1 week')::date";
            firstOfMonth       = $"date_trunc('month', {todayExpr})::date";
            firstOfNextMonth   = $"(date_trunc('month', {todayExpr}) + INTERVAL '1 month')::date";
            firstOfLastMonth   = $"(date_trunc('month', {todayExpr}) - INTERVAL '1 month')::date";
            firstOfQuarter     = $"date_trunc('quarter', {todayExpr})::date";
            firstOfNextQuarter = $"(date_trunc('quarter', {todayExpr}) + INTERVAL '3 month')::date";
            firstOfLastQuarter = $"(date_trunc('quarter', {todayExpr}) - INTERVAL '3 month')::date";
            firstOfYear        = $"date_trunc('year',  {todayExpr})::date";
            firstOfLastYear    = $"(date_trunc('year',  {todayExpr}) - INTERVAL '1 year')::date";
        }
        else
        {
            // SQL Server: datetimes are stored in local server time, not UTC,
            // so GETDATE() (server-local) is the right "today" reference and
            // no AT TIME ZONE wrap is applied. Per-connection DisplayTimezone
            // is intentionally ignored on this path — applying a UTC→local
            // shift would corrupt comparisons against locally-stored data.
            todayExpr        = "CAST(GETDATE() AS DATE)";
            // Monday-anchored week start on SQL Server. SQL Server's
            // DATEPART(weekday, ...) is sensitive to DATEFIRST; we
            // compute the Monday offset arithmetically so the result
            // is independent of session DATEFIRST. ((WEEKDAY+6)%7) maps
            // Sunday=0..Saturday=6 → Mon-distance 6,0,1,2,3,4,5.
            firstOfWeek      = $"DATEADD(day, -((DATEDIFF(day, '19000101', {todayExpr}) + 0) % 7), {todayExpr})";
            firstOfLastWeek  = $"DATEADD(day, -7, {firstOfWeek})";
            firstOfMonth     = $"DATEFROMPARTS(YEAR({todayExpr}), MONTH({todayExpr}), 1)";
            firstOfNextMonth = $"DATEADD(month, 1, {firstOfMonth})";
            firstOfLastMonth = $"DATEADD(month, -1, {firstOfMonth})";
            // Quarter start = month 1, 4, 7, or 10 of the current year.
            // ((month - 1) / 3) * 3 + 1 → 1,1,1,4,4,4,7,7,7,10,10,10
            firstOfQuarter     = $"DATEFROMPARTS(YEAR({todayExpr}), (((MONTH({todayExpr}) - 1) / 3) * 3) + 1, 1)";
            firstOfNextQuarter = $"DATEADD(month, 3, {firstOfQuarter})";
            firstOfLastQuarter = $"DATEADD(month, -3, {firstOfQuarter})";
            firstOfYear      = $"DATEFROMPARTS(YEAR({todayExpr}), 1, 1)";
            firstOfLastYear  = $"DATEFROMPARTS(YEAR({todayExpr}) - 1, 1, 1)";
        }

        string daysAgo(int n) => isPostgres ? $"{todayExpr} - {n}" : $"DATEADD(day, -{n}, {todayExpr})";

        return preset switch
        {
            RelativeDatePresets.Today       => $"{lhs} = {todayExpr}",
            RelativeDatePresets.Yesterday   => $"{lhs} = {daysAgo(1)}",
            RelativeDatePresets.Last7Days   => $"{lhs} >= {daysAgo(7)} AND {lhs} <= {todayExpr}",
            RelativeDatePresets.Last30Days  => $"{lhs} >= {daysAgo(30)} AND {lhs} <= {todayExpr}",
            RelativeDatePresets.Last90Days  => $"{lhs} >= {daysAgo(90)} AND {lhs} <= {todayExpr}",
            RelativeDatePresets.ThisWeek    => $"{lhs} >= {firstOfWeek} AND {lhs} <= {todayExpr}",
            RelativeDatePresets.LastWeek    => $"{lhs} >= {firstOfLastWeek} AND {lhs} < {firstOfWeek}",
            RelativeDatePresets.Mtd         => $"{lhs} >= {firstOfMonth} AND {lhs} <= {todayExpr}",
            RelativeDatePresets.ThisMonth   => $"{lhs} >= {firstOfMonth} AND {lhs} < {firstOfNextMonth}",
            RelativeDatePresets.LastMonth   => $"{lhs} >= {firstOfLastMonth} AND {lhs} < {firstOfMonth}",
            RelativeDatePresets.ThisQuarter => $"{lhs} >= {firstOfQuarter} AND {lhs} < {firstOfNextQuarter}",
            RelativeDatePresets.LastQuarter => $"{lhs} >= {firstOfLastQuarter} AND {lhs} < {firstOfQuarter}",
            RelativeDatePresets.Ytd         => $"{lhs} >= {firstOfYear} AND {lhs} <= {todayExpr}",
            RelativeDatePresets.LastYear    => $"{lhs} >= {firstOfLastYear} AND {lhs} < {firstOfYear}",
            _                               => $"{lhs} = {daysAgo(1)}"
        };
    }

    // Emits the admin's FilterPredicateSql with the user-picked value list
    // bound through parameters. Two binding modes (mirror SqlEmitter.BuildFilterWhereClauses):
    //   • Placeholder: predicate contains "{values}" → substitute "(@p0, @p1, ...)"
    //   • Implicit: no placeholder → AND-append
    //     "AND <LookupType.SourceTableRef>.<ValueColumn> IN (@p0, ...)"
    // For NotInList we wrap the whole thing in NOT (...) so an admin
    // predicate that joins to the lookup table still negates correctly —
    // negating just the IN list would leave the join open and match nothing.
    // Falls back to the field's own column when the lookup type didn't
    // declare a SourceTableRef/ValueColumn (keeps the filter biting).
    private static string BuildLookupTypeClause(
        FilterableField field,
        LookupTypeDefinition lookupType,
        FilterClause filter,
        ISqlDialect dialect,
        List<DbParameter> parameters)
    {
        var inParams = new List<string>(filter.Values.Count);
        foreach (var v in filter.Values)
        {
            var name = "@af_p" + parameters.Count;
            parameters.Add(dialect.CreateParameter(name, v));
            inParams.Add(name);
        }
        var inList = $"({string.Join(", ", inParams)})";

        var predicate = field.FilterPredicateSql!;
        string body;
        if (predicate.Contains("{values}", StringComparison.Ordinal))
        {
            body = predicate.Replace("{values}", inList, StringComparison.Ordinal);
        }
        else if (!string.IsNullOrWhiteSpace(lookupType.SourceTableRef)
                 && !string.IsNullOrWhiteSpace(lookupType.ValueColumn))
        {
            body = $"{predicate} AND {lookupType.SourceTableRef}.{lookupType.ValueColumn} IN {inList}";
        }
        else
        {
            // Last-resort fallback: compare the field's own column. Won't
            // route through the lookup table but at least produces a
            // non-empty WHERE so the filter has some effect.
            body = $"{field.GetRawExpression()} IN {inList}";
        }

        return filter.Op == FilterOperators.NotInList
            ? $"NOT ({body})"
            : $"({body})";
    }

    // Emits "{FilterColumn} IN (@af_p0, ...)" — or NOT IN for NotInList.
    // The picker is responsible for sending Codes (CODENUMs) when the
    // field has a FilterColumn set; this just binds them and emits the
    // comparison against the admin's chosen column on the field's row.
    private static string BuildFilterColumnClause(
        FilterableField field,
        FilterClause filter,
        ISqlDialect dialect,
        List<DbParameter> parameters)
    {
        var inParams = new List<string>(filter.Values.Count);
        foreach (var v in filter.Values)
        {
            var name = "@af_p" + parameters.Count;
            parameters.Add(dialect.CreateParameter(name, v));
            inParams.Add(name);
        }
        var inList = $"({string.Join(", ", inParams)})";
        var op = filter.Op == FilterOperators.NotInList ? "NOT IN" : "IN";
        return $"{field.FilterColumn} {op} {inList}";
    }

    private static string BuildTextOpClause(
        string lhs,
        FilterClause filter,
        bool isPostgres,
        ISqlDialect dialect,
        List<DbParameter> parameters)
    {
        string paramOf(string v)
        {
            var name = "@af_p" + parameters.Count;
            parameters.Add(dialect.CreateParameter(name, v));
            return name;
        }

        var likeOp = isPostgres ? "ILIKE" : "LIKE";

        switch (filter.Op)
        {
            case FilterOperators.Equals:
                if (filter.Values.Count == 0) return string.Empty;
                return $"{lhs} = {paramOf(filter.Values[0])}";
            case FilterOperators.NotEquals:
                if (filter.Values.Count == 0) return string.Empty;
                return $"{lhs} <> {paramOf(filter.Values[0])}";
            case FilterOperators.Contains:
                if (filter.Values.Count == 0) return string.Empty;
                return $"{lhs} {likeOp} {paramOf("%" + filter.Values[0] + "%")}";
            case FilterOperators.StartsWith:
                if (filter.Values.Count == 0) return string.Empty;
                return $"{lhs} {likeOp} {paramOf(filter.Values[0] + "%")}";
            case FilterOperators.EndsWith:
                if (filter.Values.Count == 0) return string.Empty;
                return $"{lhs} {likeOp} {paramOf("%" + filter.Values[0])}";
            case FilterOperators.InList:
            case FilterOperators.NotInList:
                if (filter.Values.Count == 0) return string.Empty;
                var placeholders = filter.Values.Select(paramOf).ToList();
                var negate = filter.Op == FilterOperators.NotInList ? "NOT " : string.Empty;
                return $"{lhs} {negate}IN ({string.Join(", ", placeholders)})";
        }
        return string.Empty;
    }

    private static string BuildNumericOpClause(
        string lhs,
        FilterClause filter,
        ISqlDialect dialect,
        List<DbParameter> parameters)
    {
        string paramOf(decimal v)
        {
            var name = "@af_p" + parameters.Count;
            parameters.Add(dialect.CreateParameter(name, v));
            return name;
        }

        decimal? parse(int idx)
        {
            if (filter.Values.Count <= idx) return null;
            return decimal.TryParse(filter.Values[idx],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d) ? d : null;
        }

        switch (filter.Op)
        {
            case FilterOperators.Equals:    { var v = parse(0); return v is null ? string.Empty : $"{lhs} = {paramOf(v.Value)}"; }
            case FilterOperators.NotEquals: { var v = parse(0); return v is null ? string.Empty : $"{lhs} <> {paramOf(v.Value)}"; }
            case FilterOperators.Lt:        { var v = parse(0); return v is null ? string.Empty : $"{lhs} < {paramOf(v.Value)}"; }
            case FilterOperators.Lte:       { var v = parse(0); return v is null ? string.Empty : $"{lhs} <= {paramOf(v.Value)}"; }
            case FilterOperators.Gt:        { var v = parse(0); return v is null ? string.Empty : $"{lhs} > {paramOf(v.Value)}"; }
            case FilterOperators.Gte:       { var v = parse(0); return v is null ? string.Empty : $"{lhs} >= {paramOf(v.Value)}"; }
            case FilterOperators.Between:
                {
                    var a = parse(0); var b = parse(1);
                    if (a is null || b is null) return string.Empty;
                    return $"{lhs} BETWEEN {paramOf(a.Value)} AND {paramOf(b.Value)}";
                }
            case FilterOperators.InList:
                {
                    var parsed = filter.Values
                        .Select(s => decimal.TryParse(s,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var d) ? (decimal?)d : null)
                        .Where(x => x.HasValue)
                        .Select(x => paramOf(x!.Value))
                        .ToList();
                    return parsed.Count > 0 ? $"{lhs} IN ({string.Join(", ", parsed)})" : string.Empty;
                }
        }
        return string.Empty;
    }
}
