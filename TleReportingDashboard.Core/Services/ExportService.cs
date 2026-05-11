using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
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

                // Currency and date cells ALWAYS get typed values so the
                // column-level Excel number format (applied below) drives
                // the display. Without this, a per-field column.Format
                // string would pre-render the value as text and Excel
                // would refuse to apply Currency/Date formatting,
                // sorting, or summing on the column. Other types keep
                // the original behavior: column.Format wins if set, else
                // SetCellValue picks a type-appropriate cell value.
                if (IsTypedAsCurrency(column.DataType) || IsTypedAsDate(column.DataType))
                {
                    SetCellValue(cell, value, column.DataType);
                }
                else if (!string.IsNullOrWhiteSpace(column.Format))
                {
                    cell.Value = FieldFormatter.Format(value, column.Format, column.DataType);
                }
                else
                {
                    SetCellValue(cell, value, column.DataType);
                }
            }
        }

        // Apply Excel typed formatting per column. Currency uses Excel's
        // Currency category (dollar sign + red negatives); date uses the
        // Date category (translates from a column.Format string when one
        // is set, falls back to mm/dd/yyyy). Skipped when there are no
        // data rows — Range() throws on empty ranges.
        if (data.Rows.Count > 0)
        {
            for (var colIdx = 0; colIdx < data.Columns.Count; colIdx++)
            {
                var column = data.Columns[colIdx];
                var dataRange = worksheet.Range(headerRow + 1, colIdx + 1,
                                                 headerRow + data.Rows.Count, colIdx + 1);
                if (IsTypedAsCurrency(column.DataType))
                {
                    dataRange.Style.NumberFormat.Format = "$#,##0.00;[Red]-$#,##0.00";
                }
                else if (IsTypedAsDate(column.DataType))
                {
                    dataRange.Style.NumberFormat.Format =
                        ExcelDateFormatFor(column.Format) ?? "mm/dd/yyyy";
                }
            }
        }

        // Auto-size columns
        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] ExportToPdf(QueryResponse data, string reportName)
    {
        // Landscape A4 by default — most analyst reports have enough
        // columns that portrait is too cramped. QuestPDF auto-paginates
        // and the table header repeats on each page (Header() block on
        // the table). Currency / date / numeric columns reuse the same
        // FormatValue path the CSV export uses so number formatting is
        // identical across surfaces.
        var headerLabels = data.Columns.Select(c => c.Label ?? string.Empty).ToList();
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(20);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontSize(8));

                page.Header().PaddingBottom(8).Row(row =>
                {
                    row.RelativeItem().Text(reportName)
                        .FontSize(14).Bold();
                    row.ConstantItem(180).AlignRight().Text(t =>
                    {
                        t.Span($"Generated {DateTime.Now:MMM d, yyyy h:mm tt}")
                            .FontSize(8).FontColor(Colors.Grey.Medium);
                    });
                });

                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        // Equal-weight columns. Auto-fit isn't a thing in
                        // QuestPDF — we'd need character-width heuristics
                        // to size columns proportionally. For tabular
                        // exports the equal-weight default is acceptable
                        // and survives report shape changes without
                        // re-tuning.
                        for (var i = 0; i < headerLabels.Count; i++)
                            cd.RelativeColumn();
                    });

                    table.Header(header =>
                    {
                        foreach (var label in headerLabels)
                        {
                            header.Cell()
                                .Background(Colors.Grey.Lighten3)
                                .BorderBottom(1)
                                .BorderColor(Colors.Grey.Medium)
                                .Padding(4)
                                .Text(label).Bold();
                        }
                    });

                    foreach (var row in data.Rows)
                    {
                        foreach (var col in data.Columns)
                        {
                            row.TryGetValue(col.FieldId, out var value);
                            var formatted = !string.IsNullOrWhiteSpace(col.Format)
                                ? FieldFormatter.Format(value, col.Format, col.DataType)
                                : FormatValue(value, col.DataType);
                            // Right-align numeric cells so currency / int
                            // columns line up the way Excel does — the
                            // CSV path doesn't carry alignment but the
                            // PDF render is visual.
                            var cell = table.Cell()
                                .BorderBottom(1)
                                .BorderColor(Colors.Grey.Lighten2)
                                .Padding(3);
                            if (IsNumericForPdf(col.DataType))
                                cell.AlignRight().Text(formatted);
                            else
                                cell.Text(formatted);
                        }
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        });
        return doc.GeneratePdf();
    }

    // Right-alignment in PDF mirrors what Excel does for currency / int
    // / percent. Date columns stay left-aligned to match cell-by-cell
    // legibility — most date strings (MM/dd/yyyy) read more naturally
    // left-aligned.
    private static bool IsNumericForPdf(string? dataType) => dataType?.ToLowerInvariant() switch
    {
        "currency" or "integer" or "percent" or "decimal" or "number" => true,
        _ => false
    };

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
                    ? FieldFormatter.Format(value, column.Format, column.DataType)
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
            case "datetime":
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

    private static bool IsTypedAsCurrency(string? dataType) =>
        string.Equals(dataType, "currency", StringComparison.OrdinalIgnoreCase);

    // Treat 'date' and 'datetime' the same here. SchemaBuilderService
    // already normalizes most SQL date types to 'date', but the runtime
    // does flow 'datetime' through occasionally — handle both so a single
    // odd column doesn't break the typed-cell path.
    private static bool IsTypedAsDate(string? dataType) =>
        string.Equals(dataType, "date", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataType, "datetime", StringComparison.OrdinalIgnoreCase);

    // Translates a per-field column.Format string (C# date pattern) into
    // an Excel number-format pattern. Excel's date format codes are
    // lowercase (yyyy / mm / dd), C#'s use uppercase MM for month — for
    // date-only patterns the lowercase form is unambiguous, so a simple
    // ToLower is safe. Bails to null (caller falls back to the default)
    // when the format contains time components, since "mm" is ambiguous
    // (month vs minute) once an hour token shows up — an admin-customized
    // datetime pattern is better off using the Excel default than
    // ending up with a minute-formatted month.
    private static string? ExcelDateFormatFor(string? csharpFormat)
    {
        if (string.IsNullOrWhiteSpace(csharpFormat)) return null;
        if (csharpFormat.Contains('H') || csharpFormat.Contains('h')
            || csharpFormat.Contains(':') || csharpFormat.Contains('s')
            || csharpFormat.Contains('t'))
        {
            return null;
        }
        return csharpFormat.ToLowerInvariant();
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
