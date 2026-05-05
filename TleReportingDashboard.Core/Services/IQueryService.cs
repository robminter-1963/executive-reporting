using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IQueryService
{
    Task<QueryResponse> ExecuteQueryAsync(QueryRequest request);

    // Build-only path. Generates the SQL + parameters without hitting the
    // source database — used by debug paths (right-click "Show query") to
    // recover the SQL when execution failed and the regular response path
    // never produced one.
    Task<(string Sql, Dictionary<string, object?> Parameters)> BuildSqlAsync(QueryRequest request);
}
