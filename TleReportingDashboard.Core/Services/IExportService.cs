using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IExportService
{
    byte[] ExportToExcel(QueryResponse data, string reportName);
    byte[] ExportToCsv(QueryResponse data);
}
