using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IExportService
{
    byte[] ExportToExcel(QueryResponse data, string reportName);
    byte[] ExportToCsv(QueryResponse data);
    // Renders the same data as a paginated PDF document — landscape A4
    // by default, table headers repeat on each page, currency / date /
    // numeric columns get the same per-column formatting as the CSV
    // path. Used by the live Export menu (Detail Viewer, Report Viewer,
    // Master Dashboard tile) and as a scheduled-report attachment
    // option (RPT_report_schedules.attachment_format = 'pdf').
    byte[] ExportToPdf(QueryResponse data, string reportName);
}
