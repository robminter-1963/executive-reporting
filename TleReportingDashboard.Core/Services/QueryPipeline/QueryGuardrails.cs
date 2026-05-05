using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

public sealed record GuardrailConfig(int MaxRows, int CommandTimeoutSeconds, int PageSize);

public static class QueryGuardrails
{
    private const int AbsoluteMaxRows = 500_000;
    private const int AbsoluteMaxTimeout = 300;
    private const int DefaultMaxRows = 100_000;
    private const int DefaultTimeout = 30;
    private const int DefaultPageSize = 50;

    public static GuardrailConfig Apply(SchemaSettings settings)
    {
        var maxRows = settings.MaxRowLimit > 0
            ? Math.Min(settings.MaxRowLimit, AbsoluteMaxRows)
            : DefaultMaxRows;

        var timeout = settings.CommandTimeoutSeconds > 0
            ? Math.Min(settings.CommandTimeoutSeconds, AbsoluteMaxTimeout)
            : DefaultTimeout;

        var pageSize = settings.DefaultPageSize > 0
            ? settings.DefaultPageSize
            : DefaultPageSize;

        return new GuardrailConfig(maxRows, timeout, pageSize);
    }
}
