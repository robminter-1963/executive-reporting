namespace TleReportingDashboard.Web.Configuration;

public sealed class SchemaSettings
{
    public int MaxRowLimit { get; init; }
    public int CommandTimeoutSeconds { get; init; }
    public int DefaultPageSize { get; init; }
    public List<RelativeDateOperator> RelativeDateOperators { get; init; } = [];

    // Fully-qualified primary table used in the FROM clause of generated
    // queries. Every other table reaches it via joins. Previously hard-coded
    // to EMPOWER.LN_MTGTERMS; now a per-schema setting so Postgres /
    // Salesforce / any non-Empower connection can name its own root table.
    // Falls back to EMPOWER.LN_MTGTERMS when blank to preserve behavior for
    // existing TLE schemas that predate this setting.
    public string? PrimaryTable { get; init; }
}
