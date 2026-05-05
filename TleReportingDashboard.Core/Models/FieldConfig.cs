namespace TleReportingDashboard.Web.Models;

public class FieldConfig
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty; // "Dimension" or "Measure"
    public string SourceTable { get; set; } = string.Empty;
    public string SourceColumn { get; set; } = string.Empty;
    /// <summary>
    /// Optional inline SQL expression used in place of SourceTable.SourceColumn
    /// (scalar subquery, CASE expression, computed value, etc.). Use
    /// <see cref="GetSqlExpression"/> when emitting SQL.
    /// </summary>
    public string? SqlExpression { get; set; }
    public string? SqlPreamble { get; set; }
    // Runtime-only: SchemaService normalizes legacy JoinId (string) + new JoinIds
    // (array) into this single list so QueryBuilder only needs one code path.
    public List<string> JoinIds { get; set; } = new();
    public string? SqlJoin { get; set; }
    public List<string>? LookupIds { get; set; }
    public int SortOrder { get; set; }
    public int? MaxLength { get; set; }
    public int? CodeSetId { get; set; }
    public Dictionary<string, int>? ValueSortOrder { get; set; }
    public string? RolesRequired { get; set; }
    public List<string>? AllowedAggregations { get; set; }
    public string? DefaultRedactionValue { get; set; }
    // Display format applied by FieldFormatter at render / export time.
    // Auto-detects mask ("(999) 999-9999") vs .NET format string ("C2", "yyyy-MM-dd").
    public string? Format { get; set; }
    /// <summary>
    /// Optional SQL expression to use in ORDER BY instead of the field's default expression.
    /// Set at runtime from a referenced Lookup's ORDERBY column.
    /// </summary>
    public string? SortExpression { get; set; }
    /// <summary>
    /// When true, the field's expression is wrapped at emission time with
    /// AT TIME ZONE '&lt;connection timezone&gt;'. Only honored on Postgres
    /// connections that have a non-empty display timezone configured.
    /// </summary>
    public bool ApplyTimezoneConversion { get; set; }

    /// <summary>
    /// Admin-asserted "this column is single-column unique" flag. Used when
    /// the connection's account can't read information_schema constraints —
    /// the schema introspection can't see the PK / UNIQUE so the admin
    /// states it manually. Surfaces wherever the system would otherwise
    /// rely on constraint introspection: the (-) marker on Sort By picks
    /// and the OFFSET-pagination tiebreaker fallback.
    /// </summary>
    public bool IsUnique { get; set; }
    /// <summary>
    /// Runtime-only: the display timezone copied off the connection so the
    /// field's expression getters can consult it without re-reading the
    /// connection record on every call. Populated by SchemaService /
    /// QueryService when the FieldConfig list is materialized.
    /// </summary>
    public string? DisplayTimezone { get; set; }

    /// <summary>
    /// Returns the SQL expression for this field, layered:
    ///   1. Raw — inline SqlExpression or SourceTable.SourceColumn.
    ///   2. Timezone wrap when ApplyTimezoneConversion + DisplayTimezone.
    ///   3. CAST to DATE when DataType = "date" — strips the time portion so
    ///      a datetime column surfaces as a pure date across SELECT, WHERE,
    ///      GROUP BY, and ORDER BY.
    /// Order matters: timezone conversion happens first (otherwise we'd cast
    /// to UTC date and then notionally shift, which is nonsense).
    /// </summary>
    public string GetSqlExpression()
    {
        var expr = !string.IsNullOrWhiteSpace(SqlExpression)
            ? SqlExpression
            : $"{SourceTable}.{SourceColumn}";
        expr = WrapForTimezone(expr);
        expr = WrapForDateCast(expr);
        return expr;
    }

    /// <summary>
    /// Returns the SQL expression for ORDER BY — uses SortExpression (from a Lookup ORDERBY column)
    /// if available, otherwise falls back to the field's default expression. A lookup-driven
    /// SortExpression intentionally bypasses the wraps because it's already designed to
    /// sort by the lookup's ORDERBY column, which is typically an integer rank, not a timestamp.
    /// </summary>
    public string GetSortExpression() =>
        !string.IsNullOrWhiteSpace(SortExpression)
            ? SortExpression
            : GetSqlExpression();

    private string WrapForTimezone(string expr)
    {
        if (!ApplyTimezoneConversion || string.IsNullOrWhiteSpace(DisplayTimezone))
            return expr;
        // Two-step conversion for Postgres `timestamp without time zone`
        // columns that store UTC. Step 1 reinterprets the naive timestamp
        // as UTC (returning timestamptz); step 2 converts that tz-aware
        // value into the connection's display zone. Emitting both is
        // harmless when the source is already timestamptz.
        //   col AT TIME ZONE 'GMT' AT TIME ZONE 'US/Pacific'
        // IANA names never contain apostrophes but escape defensively to
        // match SQL literal rules in case an admin types a weird value.
        var tz = DisplayTimezone.Replace("'", "''");
        return $"({expr} AT TIME ZONE 'GMT' AT TIME ZONE '{tz}')";
    }

    // CAST AS DATE works on both SQL Server and Postgres, so no dialect
    // branching needed. Idempotent — casting a DATE to DATE is a no-op on
    // both, so the wrap is harmless when the underlying column is already
    // a pure date.
    private string WrapForDateCast(string expr)
    {
        return string.Equals(DataType, "date", StringComparison.OrdinalIgnoreCase)
            ? $"CAST({expr} AS DATE)"
            : expr;
    }
}
