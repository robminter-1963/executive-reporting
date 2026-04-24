namespace TleReportingDashboard.Web.Configuration;

public sealed class RelativeDateOperator
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? SqlTemplate { get; init; }
}
