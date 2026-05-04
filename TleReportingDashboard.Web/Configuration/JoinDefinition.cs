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

    /// <summary>
    /// Optional. Schema-qualified primary table this join is scoped to.
    /// When set, the resolver only considers this join for reports whose
    /// primary table matches (via name OR alias). When null/empty, the
    /// join is GENERIC and eligible for any report — preserves the
    /// pre-feature behavior for joins authored before primary scoping
    /// existed.
    /// </summary>
    public string? PrimaryTable { get; init; }

    /// <summary>
    /// Optional. Alias of <see cref="PrimaryTable"/>. Used by the resolver
    /// alongside <see cref="PrimaryTable"/> so a report whose primary is
    /// authored as "salesforce.lead AS l" matches a join scoped to either
    /// "salesforce.lead" or "l".
    /// </summary>
    public string? PrimaryAlias { get; init; }
}
