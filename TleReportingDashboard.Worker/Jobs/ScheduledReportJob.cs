using System.ComponentModel;
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
    private readonly INotificationService _notifications;
    private readonly ICompanyRegistry _companies;
    private readonly ILogger<ScheduledReportJob> _logger;

    public ScheduledReportJob(
        IConfiguration configuration,
        IQueryPipeline queryPipeline,
        IExportService exportService,
        IEmailService emailService,
        INotificationService notifications,
        ICompanyRegistry companies,
        ILogger<ScheduledReportJob> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _queryPipeline = queryPipeline ?? throw new ArgumentNullException(nameof(queryPipeline));
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _companies = companies ?? throw new ArgumentNullException(nameof(companies));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Backward-compat overload — kept so any recurring job already
    // registered with the 1-arg signature (i.e., serialized in
    // HangFire.Hash before this change) keeps deserializing on its
    // next fire. SchedulerSyncService overwrites every registration
    // to the 2-arg version on the next poll, so this overload only
    // matters for the brief window after deploy.
    public Task ExecuteAsync(Guid scheduleId) =>
        ExecuteAsync(scheduleId, string.Empty);

    // [DisplayName] makes the Hangfire dashboard render the friendly
    // displayName instead of "ScheduledReportJob.ExecuteAsync(...)".
    // {1} is the second argument; SchedulerSyncService passes the
    // saved report's Name when it registers the recurring job, so the
    // dashboard reads "Scheduled run: Pipeline Report" instead of the
    // raw method signature.
    [DisplayName("Scheduled run: {1}")]
    public async Task ExecuteAsync(Guid scheduleId, string displayName)
    {
        _logger.LogInformation(
            "Starting scheduled report execution for ScheduleId={ScheduleId} ({DisplayName})",
            scheduleId, string.IsNullOrWhiteSpace(displayName) ? "no label" : displayName);

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

            // Prefix the subject with the report's company name so a single
            // inbox handling schedules across multiple companies can be
            // sorted/scanned at a glance. Best-effort lookup — if the
            // registry can't resolve the id (deleted / inactive company),
            // ship the bare schedule subject instead of failing the run.
            var subject = await PrefixSubjectWithCompanyAsync(savedReport.CompanyId, schedule.Subject);

            await _emailService.SendReportEmailAsync(
                schedule.OwnerEmail,
                subject,
                htmlBody,
                attachmentBytes,
                attachmentFileName);

            await UpdateScheduleStatusAsync(scheduleId, "Success", 0, schedule.IsActive);

            // Drop a notification into the recipient's bell-icon inbox so
            // they have an in-app surface confirming the run completed.
            // Fire-and-forget — the email already went out, a failed
            // notification shouldn't poison the success path.
            try
            {
                await _notifications.CreateAsync(
                    userEmail:         schedule.OwnerEmail,
                    kind:              NotificationKinds.ScheduleRan,
                    title:             $"\"{savedReport.Name}\" scheduled report ran",
                    body:              $"Sent to {schedule.OwnerEmail} with {queryResponse.TotalCount:N0} rows.",
                    linkUrl:           $"/viewer/{savedReport.Id}",
                    relatedEntityType: "schedule",
                    relatedEntityId:   scheduleId.ToString());
            }
            catch (Exception nex)
            {
                _logger.LogWarning(nex,
                    "Schedule ran but notification dispatch failed for ScheduleId={ScheduleId}",
                    scheduleId);
            }

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
            // Bare ex.Message — UpdateScheduleStatusAsync prepends "Failed: "
            // and clamps the whole string to the column's actual width.
            var errorMessage = ex.Message;
            var isActive = schedule.IsActive;

            if (failures >= MaxConsecutiveFailuresBeforeDeactivation)
            {
                isActive = false;
                _logger.LogWarning(
                    "Schedule {ScheduleId} auto-deactivated after {Failures} consecutive failures",
                    scheduleId, failures);

                await SendFailureNotificationAsync(schedule, failures);

                // Mirror the email with an in-app notification so the
                // owner sees the deactivation when they next sign in,
                // even if their inbox filters out the SMTP message.
                try
                {
                    await _notifications.CreateAsync(
                        userEmail:         schedule.OwnerEmail,
                        kind:              NotificationKinds.ScheduleFailed,
                        title:             "Scheduled report deactivated",
                        body:              $"\"{schedule.Subject}\" was deactivated after {failures} consecutive failures. Last error: {errorMessage}",
                        linkUrl:           $"/viewer/{schedule.ReportId}",
                        relatedEntityType: "schedule",
                        relatedEntityId:   scheduleId.ToString());
                }
                catch (Exception nex)
                {
                    _logger.LogWarning(nex,
                        "Schedule deactivation notification failed for ScheduleId={ScheduleId}",
                        scheduleId);
                }
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
        // primary_table MUST be in the SELECT — SqlEmitter throws
        // "Primary Table is required" if QueryRequest.PrimaryTable is
        // null. Without this column the saved report's value is lost
        // even when set correctly via the Builder UI.
        await using var cmd = new SqlCommand(
            "SELECT id, name, owner_id, owner_email, field_ids, filters, aggregations, column_state, connection_id, primary_table, company_id FROM EMPOWER.RPT_saved_reports WHERE id = @Id", conn);
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
            ConnectionId = r.IsDBNull(8) ? null : r.GetGuid(8),
            PrimaryTable = r.IsDBNull(9) ? null : r.GetString(9),
            CompanyId = r.IsDBNull(10) ? Guid.Empty : r.GetGuid(10)
        };
    }

    // Hard cap matches the post-migration column width (NVARCHAR(500)).
    // Acts as a safety net for environments that haven't run the
    // 2026-05-04 widen migration yet — without this, a long error
    // message blows up the UPDATE with "String or binary data would
    // be truncated" (MSSQL refuses to silently truncate by default).
    // Picking the smaller of the post-migration width and any caller-
    // supplied long status keeps writes safe regardless of the row
    // schema actually deployed.
    private const int LastRunStatusMaxLength = 500;

    private async Task UpdateScheduleStatusAsync(Guid scheduleId, string status, int consecutiveFailures, bool isActive)
    {
        // Truncate defensively; the column is NVARCHAR(500) post-
        // migration. If the env still has the old NVARCHAR(50) and a
        // long string lands here, we'd still fail — but that case is
        // caught by the migration that ships in this same change.
        var safeStatus = status?.Length > LastRunStatusMaxLength
            ? status[..LastRunStatusMaxLength]
            : status;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_report_schedules
            SET last_run_at = SYSUTCDATETIME(),
                last_run_status = @Status,
                consecutive_failures = @Failures,
                is_active = @IsActive
            WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Status", (object?)safeStatus ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Failures", consecutiveFailures));
        cmd.Parameters.Add(new SqlParameter("@IsActive", isActive));
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ──

    private async Task<string> PrefixSubjectWithCompanyAsync(Guid companyId, string subject)
    {
        if (companyId == Guid.Empty) return subject;
        try
        {
            var company = await _companies.GetByIdAsync(companyId);
            return string.IsNullOrWhiteSpace(company?.Name)
                ? subject
                : $"{company.Name} - {subject}";
        }
        catch (Exception ex)
        {
            // Company resolution shouldn't break the email — log + fall
            // through to the unprefixed subject so the run still delivers.
            _logger.LogWarning(ex,
                "Couldn't resolve company {CompanyId} for subject prefix; sending bare subject",
                companyId);
            return subject;
        }
    }

    private static QueryRequest BuildQueryRequest(SavedReport savedReport)
    {
        // Empty-fields validation is Worker-specific — interactive
        // surfaces show an empty grid and let the user fix it. The
        // Worker fails the run instead so the schedule's last_run_status
        // surfaces the misconfiguration.
        if (string.IsNullOrWhiteSpace(savedReport.FieldIds))
        {
            throw new InvalidOperationException(
                $"SavedReport {savedReport.Id} has no field configuration (FieldIds is empty).");
        }

        // QueryRequestFactory captures every saved-report knob in one
        // place — field_ids, filters (JsonElement-safe), aggregations,
        // primary_table, connection_id, plus all column_state knobs
        // (Distinct, Sort, CustomFilterIds, AdvancedFilters, TableCalcs).
        // Adding a new saved knob is a one-line change in the factory;
        // every consumer (Viewer / Master Dashboard tile / Detail Viewer /
        // this Worker) picks it up automatically.
        var request = QueryRequestFactory.FromSavedReport(savedReport, QueryRequest.MaxPageSize);

        if (request.FieldIds is null || request.FieldIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"SavedReport {savedReport.Id} has an empty field list — cannot generate report.");
        }

        return request;
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
