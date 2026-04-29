namespace TleReportingDashboard.Web.Configuration;

public sealed class JoinDefinition
{
    public required string Id { get; init; }
    public required string Sql { get; init; }

    /// <summary>
    /// Optional. Schema-qualified name of the source (parent) table this
    /// join depends on — i.e. the table that must already exist in the
    /// FROM/JOIN chain when this join runs. e.g. "salesforce.lead".
    /// </summary>
    public string? SourceTable { get; init; }

    /// <summary>
    /// Optional. Alias the source is referenced by in this join's ON
    /// clause. e.g. "l" for "ON c.SFID = l.CAMPAIGN__C". When the report's
    /// primary table matches this alias (or SourceTable when alias is
    /// blank), this join takes precedence over any other join with the
    /// same target but a different source.
    /// </summary>
    public string? SourceAlias { get; init; }

    /// <summary>
    /// Optional. Schema-qualified name of the table this join introduces.
    /// e.g. "salesforce.campaign". When unset, the resolver falls back to
    /// regex-extracting the target from <see cref="Sql"/> (legacy behavior).
    /// </summary>
    public string? TargetTable { get; init; }

    /// <summary>
    /// Optional. Alias the target is given in this join. e.g. "c" for
    /// "JOIN salesforce.campaign c ...". Field definitions reference this
    /// value via their own SourceTable property when they're columns on
    /// the joined table.
    /// </summary>
    public string? TargetAlias { get; init; }
}
