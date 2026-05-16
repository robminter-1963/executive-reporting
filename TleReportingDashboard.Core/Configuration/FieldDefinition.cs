namespace TleReportingDashboard.Web.Configuration;

public sealed class FieldDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string Domain { get; init; }
    public required string DataType { get; init; }
    public required string FieldType { get; init; }
    public List<string>? AllowedAggregations { get; init; }
    public required string SourceTable { get; init; }
    public required string SourceColumn { get; init; }
    /// <summary>
    /// Optional inline SQL expression used in place of SourceTable.SourceColumn
    /// (e.g. a scalar subquery, CASE expression, or computed column). When set,
    /// projection / aggregation / filter / order-by all use this expression instead
    /// of the qualified column reference.
    /// </summary>
    public string? SqlExpression { get; init; }
    /// <summary>
    /// Optional CTE preamble required by this field's SqlExpression.
    /// Combined with other active preambles in a single WITH clause.
    /// </summary>
    public string? SqlPreamble { get; init; }
    /// <summary>
    /// Legacy single reference to a join id. Kept for JSON backward compatibility;
    /// new saves use <see cref="JoinIds"/>. Readers should treat a populated JoinId
    /// as a single-element JoinIds list.
    /// </summary>
    public string? JoinId { get; init; }
    /// <summary>
    /// References to existing join ids from the Joins configuration. The joins' SQL
    /// is resolved at query time — no duplication needed in SqlJoin. Useful when an
    /// inline SqlExpression spans multiple joined tables.
    /// </summary>
    public List<string>? JoinIds { get; init; }
    public string? SqlJoin { get; init; }
    /// <summary>
    /// IDs of named Lookups (from schema_config.json Lookups section) whose
    /// CTE and JOIN are required when this field is in the query.
    /// </summary>
    public List<string>? LookupIds { get; init; }
    public int? MaxLength { get; init; }
    /// <summary>
    /// Optional column min-width hint (px) applied in the report grid. When
    /// set, the grid renders this column at least this wide regardless of
    /// the MaxLength-derived auto-sizing. Useful for free-form text fields
    /// like Notes that hold prose much longer than their declared MaxLength
    /// would suggest — those reads end up cramped under the auto-size rule.
    /// Null = no override; the grid's data-type + MaxLength heuristics
    /// pick a default. Honored in ReportGrid.GetColumnWidths.
    /// </summary>
    public int? MinWidth { get; init; }
    public int? CodeSetId { get; init; }
    public Dictionary<string, int>? ValueSortOrder { get; init; }
    public string? RolesRequired { get; init; }
    public string? DefaultRedactionValue { get; init; }
    /// <summary>
    /// Optional display format applied to the raw value at render / export time.
    /// Two syntaxes are supported (auto-detected):
    ///   • Mask — when the string contains 9 / A / * it's treated as a character
    ///     mask (9 = digit, A = letter, * = any char, everything else literal).
    ///     Example: "(999) 999-9999" on "8005551234" → "(800) 555-1234".
    ///   • .NET format string — otherwise passed to value.ToString(format),
    ///     e.g. "C2", "N0", "yyyy-MM-dd", "MMM d, yyyy h:mm tt".
    /// Null or blank means "no formatting, render raw".
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// When true and the connection is Postgres with a display timezone set,
    /// the emitter wraps this field's SQL expression as
    /// `(<expr> AT TIME ZONE '<tz>')` at query time. Intended for date /
    /// datetime fields stored in UTC that should render in the connection's
    /// local zone. No-op on non-Postgres connections or when the connection's
    /// PgDisplayTimezone is blank.
    /// </summary>
    public bool ApplyTimezoneConversion { get; init; }

    /// <summary>
    /// Admin-asserted single-column uniqueness flag. Set when the
    /// connection's account can't introspect information_schema
    /// constraints — schema browsing can't auto-detect the PK / UNIQUE,
    /// so the admin marks the column manually. Used for the (-) marker on
    /// Sort By picks and the OFFSET-pagination fallback tiebreaker, both
    /// of which would otherwise depend on a DB constraint lookup.
    /// </summary>
    public bool IsUnique { get; init; }

    /// <summary>
    /// Runtime-only: set by the query pipeline (SqlEmitter / QueryService)
    /// before handing the field off to expression builders. When combined
    /// with ApplyTimezoneConversion, GetSqlExpression() returns the wrapped
    /// form. Not serialized — comes from the connection record at query time.
    /// Mutable by design so the pipeline can paint it onto cached field
    /// instances without re-cloning the whole schema.
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

        if (ApplyTimezoneConversion && !string.IsNullOrWhiteSpace(DisplayTimezone))
        {
            // Two-step conversion for Postgres `timestamp without time zone`
            // columns storing UTC. Step 1 reinterprets the naive timestamp
            // as UTC (→ timestamptz); step 2 converts that tz-aware value
            // into the connection's display zone. Harmless if the source is
            // already timestamptz.
            //   col AT TIME ZONE 'GMT' AT TIME ZONE 'US/Pacific'
            var tz = DisplayTimezone.Replace("'", "''");
            expr = $"({expr} AT TIME ZONE 'GMT' AT TIME ZONE '{tz}')";
        }

        // CAST AS DATE works on both SQL Server and Postgres — no dialect
        // branching needed. Idempotent when the underlying column is already
        // a pure date.
        if (string.Equals(DataType, "date", StringComparison.OrdinalIgnoreCase))
        {
            expr = $"CAST({expr} AS DATE)";
        }

        return expr;
    }
}
