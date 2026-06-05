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

    // Single workbook with one worksheet per input. Used by the Batches
    // feature to package multiple reports (potentially across companies)
    // into one file. Sheet names are sanitised + de-duplicated against
    // Excel's 31-char limit. Per-sheet formatting (currency, dates,
    // auto-fit) matches ExportToExcel — same private fill logic backs
    // both. An empty input yields a workbook with a single blank
    // "(empty)" sheet so the result is still openable.
    byte[] ExportToMultiSheetExcel(IReadOnlyList<MultiSheetExcelInput> sheets);
}

// One worksheet's worth of input for the multi-sheet exporter. Data
// is the QueryResponse (same shape every other export consumes); SheetName
// is the desired tab label (gets sanitised + length-clamped + de-duped).
public sealed record MultiSheetExcelInput(string SheetName, QueryResponse Data);
