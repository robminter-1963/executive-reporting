using System.Text.Json;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;
using TleReportingDashboard.Web.Services;
using TleReportingDashboard.Web.Services.QueryPipeline;

namespace TleReportingDashboard.Worker.Jobs;

public sealed class ScheduledReportJob
{
    private const int MaxConsecutiveFailuresBeforeDeactivation = 3;

    private readonly string _connectionString;
    private readonly IQueryPipeline _queryPipeline;
    private readonly IExportService _exportService;
    private readonly IEmailService _emailService;
    private readonly ILogger<ScheduledReportJob> _logger;

    public ScheduledReportJob(
        IConfiguration configuration,
        IQueryPipeline queryPipeline,
        IExportService exportService,
        IEmailService emailService,
        ILogger<ScheduledReportJob> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _queryPipeline = queryPipeline ?? throw new ArgumentNullException(nameof(queryPipeline));
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(Guid scheduleId)
    {
        _logger.LogInformation("Starting scheduled report execution for ScheduleId={ScheduleId}", scheduleId);

        var schedule = await GetScheduleAsync(scheduleId);
        if (schedule is null)
        {
            _logger.LogWarning("Schedule {ScheduleId} not found — skipping execution", scheduleId);
            return;
        }

        if (!schedule.IsActive)
        {
            _logger.LogInformation("Schedule {ScheduleId} is inactive — skipping execution", scheduleId);
            return;
        }

        try
        {
            var savedReport = await GetSavedReportAsync(schedule.ReportId);
            if (savedReport is null)
            {
                throw new InvalidOperationException(
                    $"SavedReport {schedule.ReportId} referenced by schedule {scheduleId} was not found.");
            }

            var queryRequest = BuildQueryRequest(savedReport);
            var queryResponse = await _queryPipeline.ExecuteAsync(queryRequest, Array.Empty<string>());

            _logger.LogInformation(
                "Query executed for ScheduleId={ScheduleId}: {RowCount} rows in {Duration}ms",
                scheduleId, queryResponse.TotalCount, queryResponse.ExecutionMs);

            var (attachmentBytes, attachmentFileName) = FormatAttachment(
                BuildExportData(queryResponse, savedReport.ColumnState),
                savedReport.Name, schedule.AttachmentFormat);

            var htmlBody = BuildEmailBody(savedReport.Name, queryResponse.TotalCount, schedule.IncludePreview);

            await _emailService.SendReportEmailAsync(
                schedule.OwnerEmail,
                schedule.Subject,
                htmlBody,
                attachmentBytes,
                attachmentFileName);

            await UpdateScheduleStatusAsync(scheduleId, "Success", 0, schedule.IsActive);

            _logger.LogInformation(
                "Scheduled report completed successfully: ScheduleId={ScheduleId}, Report={ReportName}",
                scheduleId, savedReport.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Scheduled report failed: ScheduleId={ScheduleId}, Attempt={Attempt}",
                scheduleId, schedule.ConsecutiveFailures + 1);

            var failures = schedule.ConsecutiveFailures + 1;
            var errorMessage = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            var isActive = schedule.IsActive;

            if (failures >= MaxConsecutiveFailuresBeforeDeactivation)
            {
                isActive = false;
                _logger.LogWarning(
                    "Schedule {ScheduleId} auto-deactivated after {Failures} consecutive failures",
                    scheduleId, failures);

                await SendFailureNotificationAsync(schedule, failures);
            }

            await UpdateScheduleStatusAsync(scheduleId, $"Failed: {errorMessage}", failures, isActive);
        }
    }

    // ── ADO.NET data access ──

    private async Task<ReportSchedule?> GetScheduleAsync(Guid scheduleId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT id, report_id, owner_id, owner_email, cron_expression, subject, attachment_format, include_preview, is_active, last_run_at, consecutive_failures FROM EMPOWER.RPT_report_schedules WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new ReportSchedule
        {
            Id = r.GetGuid(0),
            ReportId = r.GetGuid(1),
            OwnerId = r.GetString(2),
            OwnerEmail = r.GetString(3),
            CronExpression = r.GetString(4),
            Subject = r.GetString(5),
            AttachmentFormat = r.GetString(6),
            IncludePreview = r.GetBoolean(7),
            IsActive = r.GetBoolean(8),
            LastRunAt = r.IsDBNull(9) ? null : r.GetDateTime(9),
            ConsecutiveFailures = r.IsDBNull(10) ? 0 : r.GetInt32(10)
        };
    }

    private async Task<SavedReport?> GetSavedReportAsync(Guid reportId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT id, name, owner_id, owner_email, field_ids, filters, aggregations, column_state, connection_id FROM EMPOWER.RPT_saved_reports WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", reportId));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new SavedReport
        {
            Id = r.GetGuid(0),
            Name = r.GetString(1),
            OwnerId = r.GetString(2),
            OwnerEmail = r.GetString(3),
            FieldIds = r.GetString(4),
            Filters = r.IsDBNull(5) ? null : r.GetString(5),
            Aggregations = r.IsDBNull(6) ? null : r.GetString(6),
            ColumnState = r.IsDBNull(7) ? null : r.GetString(7),
            ConnectionId = r.IsDBNull(8) ? null : r.GetGuid(8)
        };
    }

    private async Task UpdateScheduleStatusAsync(Guid scheduleId, string status, int consecutiveFailures, bool isActive)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_report_schedules
            SET last_run_at = SYSUTCDATETIME(),
                last_run_status = @Status,
                consecutive_failures = @Failures,
                is_active = @IsActive
            WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Status", status));
        cmd.Parameters.Add(new SqlParameter("@Failures", consecutiveFailures));
        cmd.Parameters.Add(new SqlParameter("@IsActive", isActive));
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ──

    private static QueryRequest BuildQueryRequest(SavedReport savedReport)
    {
        if (string.IsNullOrWhiteSpace(savedReport.FieldIds))
        {
            throw new InvalidOperationException(
                $"SavedReport {savedReport.Id} has no field configuration (FieldIds is empty).");
        }

        var fieldIds = JsonSerializer.Deserialize<List<string>>(savedReport.FieldIds) ?? [];
        if (fieldIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"SavedReport {savedReport.Id} has an empty field list — cannot generate report.");
        }

        var filters = !string.IsNullOrWhiteSpace(savedReport.Filters)
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(savedReport.Filters) ?? new()
            : new();

        var aggregations = !string.IsNullOrWhiteSpace(savedReport.Aggregations)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(savedReport.Aggregations)
            : null;

        return new QueryRequest
        {
            FieldIds = fieldIds,
            Filters = filters,
            Aggregations = aggregations,
            Page = 1,
            PageSize = QueryRequest.MaxPageSize,
            ConnectionId = savedReport.ConnectionId
        };
    }

    // Mirrors what a user sees in the viewer: drops columns hidden in the saved
    // report's ColumnState so scheduled exports don't leak fields the owner has
    // hidden. Rows are left untouched — the export loop iterates Columns, so
    // dropped fields never get emitted.
    private static QueryResponse BuildExportData(QueryResponse source, string? columnStateJson)
    {
        if (string.IsNullOrWhiteSpace(columnStateJson)) return source;
        List<string>? hiddenList = null;
        try
        {
            using var doc = JsonDocument.Parse(columnStateJson);
            if (doc.RootElement.TryGetProperty("HiddenColumns", out var hc)
                && hc.ValueKind == JsonValueKind.Array)
            {
                hiddenList = JsonSerializer.Deserialize<List<string>>(hc.GetRawText());
            }
        }
        catch { return source; }

        if (hiddenList is null || hiddenList.Count == 0) return source;
        var hidden = new HashSet<string>(hiddenList, StringComparer.OrdinalIgnoreCase);
        var visibleColumns = source.Columns.Where(c => !hidden.Contains(c.FieldId)).ToList();
        if (visibleColumns.Count == source.Columns.Count) return source;
        return new QueryResponse
        {
            Columns = visibleColumns,
            Rows = source.Rows,
            TotalCount = source.TotalCount
        };
    }

    private (byte[] Bytes, string FileName) FormatAttachment(
        QueryResponse data, string reportName, string format)
    {
        var safeReportName = SanitizeFileName(reportName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = _exportService.ExportToCsv(data);
            return (bytes, $"{safeReportName}_{timestamp}.csv");
        }

        var excelBytes = _exportService.ExportToExcel(data, reportName);
        return (excelBytes, $"{safeReportName}_{timestamp}.xlsx");
    }

    private static string BuildEmailBody(string reportName, int rowCount, bool includePreview)
    {
        var html = $"""
            <h2>Scheduled Report: {System.Net.WebUtility.HtmlEncode(reportName)}</h2>
            <p>Your scheduled report has been generated successfully.</p>
            <ul>
                <li><strong>Records:</strong> {rowCount:N0}</li>
                <li><strong>Generated:</strong> {DateTime.UtcNow:MMMM dd, yyyy h:mm tt} UTC</li>
            </ul>
            <p>The report is attached to this email.</p>
            """;

        if (!includePreview)
        {
            html += "<p><em>Preview was not included per your schedule settings.</em></p>";
        }

        return html;
    }

    private async Task SendFailureNotificationAsync(ReportSchedule schedule, int failures)
    {
        try
        {
            var html = $"""
                <h2>Scheduled Report Deactivated</h2>
                <p>Your scheduled report <strong>{System.Net.WebUtility.HtmlEncode(schedule.Subject)}</strong> has been
                automatically deactivated after {failures} consecutive failures.</p>
                <p><strong>Last error:</strong> {System.Net.WebUtility.HtmlEncode(schedule.LastRunStatus ?? "Unknown")}</p>
                <p>Please review and fix the issue in the TLE Reporting Dashboard, then re-enable the schedule.</p>
                """;

            await _emailService.SendReportEmailAsync(
                schedule.OwnerEmail,
                $"Schedule Deactivated: {schedule.Subject}",
                html,
                attachment: null,
                attachmentFileName: string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send deactivation notification for ScheduleId={ScheduleId}",
                schedule.Id);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Length > 100 ? sanitized[..100] : sanitized;
    }
}
