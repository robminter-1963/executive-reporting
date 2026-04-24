using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IQueryService
{
    Task<QueryResponse> ExecuteQueryAsync(QueryRequest request);
}
