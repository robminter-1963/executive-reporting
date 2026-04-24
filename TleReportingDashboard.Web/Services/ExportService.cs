using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class ExportService : IExportService
{
    public byte[] ExportToExcel(QueryResponse data, string reportName)
    {
        using var workbook = new XLWorkbook();
        var sheetName = reportName.Length > 31 ? reportName[..31] : reportName;
        var worksheet = workbook.Worksheets.Add(sheetName);

        // Column headers (row 1)
        var headerRow = 1;
        for (var col = 0; col < data.Columns.Count; col++)
        {
            var cell = worksheet.Cell(headerRow, col + 1);
            cell.Value = data.Columns[col].Label;
            cell.Style.Font.Bold = true;
        }

        // Data rows (starting at row 3)
        for (var rowIdx = 0; rowIdx < data.Rows.Count; rowIdx++)
        {
            var row = data.Rows[rowIdx];
            for (var colIdx = 0; colIdx < data.Columns.Count; colIdx++)
            {
                var column = data.Columns[colIdx];
                var cell = worksheet.Cell(headerRow + 1 + rowIdx, colIdx + 1);

                if (!row.TryGetValue(column.FieldId, out var value) || value is null)
                    continue;

                // Per-field Format, when set, writes the formatted string so the
                // cell reads exactly what the user sees in the grid. Skips the
                // type-based cell-value conversion in that branch.
                if (!string.IsNullOrWhiteSpace(column.Format))
                    cell.Value = FieldFormatter.Format(value, column.Format);
                else
                    SetCellValue(cell, value, column.DataType);
            }
        }

        // Apply currency format to currency columns
        for (var colIdx = 0; colIdx < data.Columns.Count; colIdx++)
        {
            if (string.Equals(data.Columns[colIdx].DataType, "currency", StringComparison.OrdinalIgnoreCase))
            {
                var dataRange = worksheet.Range(headerRow + 1, colIdx + 1, headerRow + data.Rows.Count, colIdx + 1);
                dataRange.Style.NumberFormat.Format = "#,##0.00";
            }
        }

        // Auto-size columns
        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] ExportToCsv(QueryResponse data)
    {
        var sb = new StringBuilder();

        // Header row
        sb.AppendLine(string.Join(",", data.Columns.Select(c => EscapeCsvField(c.Label))));

        // Data rows
        foreach (var row in data.Rows)
        {
            var fields = new List<string>();
            foreach (var column in data.Columns)
            {
                row.TryGetValue(column.FieldId, out var value);
                var formatted = !string.IsNullOrWhiteSpace(column.Format)
                    ? FieldFormatter.Format(value, column.Format)
                    : FormatValue(value, column.DataType);
                fields.Add(EscapeCsvField(formatted));
            }
            sb.AppendLine(string.Join(",", fields));
        }

        // UTF-8 with BOM
        var bom = Encoding.UTF8.GetPreamble();
        var content = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[bom.Length + content.Length];
        bom.CopyTo(result, 0);
        content.CopyTo(result, bom.Length);
        return result;
    }

    private static void SetCellValue(IXLCell cell, object value, string dataType)
    {
        switch (dataType.ToLowerInvariant())
        {
            case "currency":
            case "percent":
            case "integer":
                if (TryConvertToDouble(value, out var numericValue))
                    cell.Value = numericValue;
                else
                    cell.Value = value.ToString();
                break;

            case "date":
                if (value is DateTime dt)
                    cell.Value = dt;
                else if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    cell.Value = parsed;
                else
                    cell.Value = value.ToString();
                break;

            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static string FormatValue(object? value, string dataType)
    {
        if (value is null)
            return string.Empty;

        switch (dataType.ToLowerInvariant())
        {
            case "currency":
                if (TryConvertToDouble(value, out var currencyVal))
                    return currencyVal.ToString("C2", CultureInfo.CurrentCulture);
                break;

            case "percent":
                if (TryConvertToDouble(value, out var pctVal))
                    return pctVal.ToString("N3") + "%";
                break;

            case "date":
                if (value is DateTime dt)
                    return dt.ToString("MM/dd/yyyy");
                if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return parsed.ToString("MM/dd/yyyy");
                break;

            case "integer":
                if (TryConvertToDouble(value, out var intVal))
                    return intVal.ToString("N0");
                break;
        }

        return value.ToString() ?? string.Empty;
    }

    private static bool TryConvertToDouble(object value, out double result)
    {
        if (value is double d) { result = d; return true; }
        if (value is decimal dec) { result = (double)dec; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is float f) { result = f; return true; }
        return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains('"') || field.Contains(',') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }
}
