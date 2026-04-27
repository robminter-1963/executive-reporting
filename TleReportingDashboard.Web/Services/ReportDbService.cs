using System.Data;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class ReportDbService : IReportService, ISharingService, IScheduleService
{
    private readonly string _connectionString;
    private readonly ILogger<ReportDbService> _logger;

    public ReportDbService(IConfiguration configuration, ILogger<ReportDbService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _logger = logger;
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ════════════════════════════════════════════════════════════════════════
    // IReportService
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<SavedReport>> GetReportsAsync(string userId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "SELECT * FROM EMPOWER.RPT_saved_reports WHERE owner_id = @UserId ORDER BY name", conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        return await ReadReportsAsync(cmd);
    }

    public async Task<List<SavedReport>> GetAllReportsAsync()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "SELECT * FROM EMPOWER.RPT_saved_reports ORDER BY name", conn);
        return await ReadReportsAsync(cmd);
    }

    public async Task<SavedReport?> GetReportByIdAsync(Guid id)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "SELECT * FROM EMPOWER.RPT_saved_reports WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", id));
        var rows = await ReadReportsAsync(cmd);
        return rows.FirstOrDefault();
    }

    public async Task<SavedReport> SaveReportAsync(SavedReport report)
    {
        report.Id = report.Id == Guid.Empty ? Guid.NewGuid() : report.Id;
        report.CreatedAt = DateTime.UtcNow;
        report.UpdatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync();
        // company_id is derived server-side from the chosen connection so
        // callers don't have to plumb it in explicitly. The Master Dashboard's
        // "Add Report" picker filters on this column — without it set, a
        // ShowOnMaster-flagged report would be invisible there.
        await using var cmd = new SqlCommand(
            @"INSERT INTO EMPOWER.RPT_saved_reports
              (id, name, internal_name, owner_id, owner_email, field_ids, filters, aggregations, column_state, grid_template_id, connection_id, company_id, primary_table, last_run_at, created_at, updated_at)
              VALUES (@Id, @Name, @InternalName, @OwnerId, @OwnerEmail, @FieldIds, @Filters, @Aggregations, @ColumnState, @GridTemplateId, @ConnectionId,
                      (SELECT company_id FROM EMPOWER.RPT_company_connections WHERE id = @ConnectionId),
                      @PrimaryTable, @LastRunAt, @CreatedAt, @UpdatedAt)", conn);
        AddReportParams(cmd, report);
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("Report saved: {Id} {Name}", report.Id, report.Name);
        return report;
    }

    public async Task<SavedReport> UpdateReportAsync(SavedReport report)
    {
        report.UpdatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync();
        // Refresh company_id on every update so a connection swap (rare but
        // possible via Schema Builder admin ops) flows through to the report
        // without a separate maintenance step.
        await using var cmd = new SqlCommand(
            @"UPDATE EMPOWER.RPT_saved_reports SET
              name = @Name, internal_name = @InternalName, field_ids = @FieldIds, filters = @Filters, aggregations = @Aggregations,
              column_state = @ColumnState, grid_template_id = @GridTemplateId, connection_id = @ConnectionId,
              company_id = (SELECT company_id FROM EMPOWER.RPT_company_connections WHERE id = @ConnectionId),
              primary_table = @PrimaryTable,
              last_run_at = @LastRunAt, updated_at = @UpdatedAt
              WHERE id = @Id AND owner_id = @OwnerId", conn);
        AddReportParams(cmd, report);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) throw new UnauthorizedAccessException("Report not found or not owned by user.");
        return report;
    }

    public async Task DeleteReportAsync(Guid id, string userId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_saved_reports WHERE id = @Id AND owner_id = @UserId", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", id));
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) throw new UnauthorizedAccessException("Report not found or not owned by user.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // ISharingService
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<ReportShare>> GetSharesForReportAsync(Guid reportId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "SELECT * FROM EMPOWER.RPT_report_shares WHERE report_id = @ReportId", conn);
        cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
        return await ReadSharesAsync(cmd);
    }

    public async Task<List<SavedReport>> GetSharedWithMeAsync(string userId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            @"SELECT r.* FROM EMPOWER.RPT_saved_reports r
              INNER JOIN EMPOWER.RPT_report_shares s ON r.id = s.report_id
              WHERE s.shared_with_id = @UserId ORDER BY r.name", conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        return await ReadReportsAsync(cmd);
    }

    public async Task<ReportShare> ShareReportAsync(ReportShare share)
    {
        share.Id = share.Id == Guid.Empty ? Guid.NewGuid() : share.Id;
        share.CreatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            @"INSERT INTO EMPOWER.RPT_report_shares
              (id, report_id, shared_with_id, shared_with_type, permission, shared_by_id, created_at)
              VALUES (@Id, @ReportId, @SharedWithId, @SharedWithType, @Permission, @SharedById, @CreatedAt)", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", share.Id));
        cmd.Parameters.Add(new SqlParameter("@ReportId", share.ReportId));
        cmd.Parameters.Add(new SqlParameter("@SharedWithId", share.SharedWithId));
        cmd.Parameters.Add(new SqlParameter("@SharedWithType", share.SharedWithType));
        cmd.Parameters.Add(new SqlParameter("@Permission", share.Permission));
        cmd.Parameters.Add(new SqlParameter("@SharedById", share.SharedById));
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", share.CreatedAt));
        await cmd.ExecuteNonQueryAsync();
        return share;
    }

    public async Task RevokeShareAsync(Guid shareId, string requesterId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_report_shares WHERE id = @Id AND shared_by_id = @RequesterId", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", shareId));
        cmd.Parameters.Add(new SqlParameter("@RequesterId", requesterId));
        await cmd.ExecuteNonQueryAsync();
    }

    // ════════════════════════════════════════════════════════════════════════
    // IScheduleService
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<ReportSchedule>> GetSchedulesForReportAsync(Guid reportId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "SELECT * FROM EMPOWER.RPT_report_schedules WHERE report_id = @ReportId", conn);
        cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
        return await ReadSchedulesAsync(cmd);
    }

    public async Task<List<ReportSchedule>> GetSchedulesForUserAsync(string userId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "SELECT * FROM EMPOWER.RPT_report_schedules WHERE owner_id = @UserId", conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        return await ReadSchedulesAsync(cmd);
    }

    public async Task<List<ReportSchedule>> GetAllSchedulesAsync()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "SELECT * FROM EMPOWER.RPT_report_schedules", conn);
        return await ReadSchedulesAsync(cmd);
    }

    public async Task<ReportSchedule> CreateScheduleAsync(ReportSchedule schedule)
    {
        schedule.Id = schedule.Id == Guid.Empty ? Guid.NewGuid() : schedule.Id;
        schedule.CreatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            @"INSERT INTO EMPOWER.RPT_report_schedules
              (id, report_id, owner_id, owner_email, cron_expression, schedule_pattern, start_date, end_date,
               subject, recipients, cc_recipients, bcc_recipients,
               attachment_format, include_preview, is_active, created_at)
              VALUES (@Id, @ReportId, @OwnerId, @OwnerEmail, @Cron, @Pattern, @StartDate, @EndDate,
                      @Subject, @Recipients, @Cc, @Bcc,
                      @Format, @Preview, @Active, @CreatedAt)", conn);
        AddScheduleParams(cmd, schedule);
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", schedule.CreatedAt));
        await cmd.ExecuteNonQueryAsync();
        return schedule;
    }

    public async Task<ReportSchedule> UpdateScheduleAsync(ReportSchedule schedule)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            @"UPDATE EMPOWER.RPT_report_schedules SET
              cron_expression = @Cron, schedule_pattern = @Pattern,
              start_date = @StartDate, end_date = @EndDate,
              subject = @Subject,
              recipients = @Recipients, cc_recipients = @Cc, bcc_recipients = @Bcc,
              attachment_format = @Format, include_preview = @Preview, is_active = @Active
              WHERE id = @Id AND owner_id = @OwnerId", conn);
        AddScheduleParams(cmd, schedule);
        await cmd.ExecuteNonQueryAsync();
        return schedule;
    }

    private static void AddScheduleParams(SqlCommand cmd, ReportSchedule s)
    {
        cmd.Parameters.Add(new SqlParameter("@Id", s.Id));
        cmd.Parameters.Add(new SqlParameter("@ReportId", s.ReportId));
        cmd.Parameters.Add(new SqlParameter("@OwnerId", s.OwnerId));
        cmd.Parameters.Add(new SqlParameter("@OwnerEmail", s.OwnerEmail));
        cmd.Parameters.Add(new SqlParameter("@Cron", s.CronExpression));
        cmd.Parameters.Add(new SqlParameter("@Pattern", (object?)s.SchedulePatternJson ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@StartDate", (object?)s.StartDate ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@EndDate", (object?)s.EndDate ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Subject", s.Subject));
        cmd.Parameters.Add(new SqlParameter("@Recipients", (object?)s.Recipients ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Cc", (object?)s.CcRecipients ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Bcc", (object?)s.BccRecipients ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Format", s.AttachmentFormat));
        cmd.Parameters.Add(new SqlParameter("@Preview", s.IncludePreview));
        cmd.Parameters.Add(new SqlParameter("@Active", s.IsActive));
    }

    public async Task DeactivateScheduleAsync(Guid scheduleId, string userId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_report_schedules SET is_active = 0 WHERE id = @Id AND owner_id = @UserId", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteScheduleAsync(Guid scheduleId, string userId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_report_schedules WHERE id = @Id AND owner_id = @UserId", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        await cmd.ExecuteNonQueryAsync();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Readers
    // ════════════════════════════════════════════════════════════════════════

    private static async Task<List<SavedReport>> ReadReportsAsync(SqlCommand cmd)
    {
        var reports = new List<SavedReport>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            reports.Add(new SavedReport
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                // TryGet so envs that haven't run the internal_name migration
                // yet keep working — null falls back to Name in every consumer.
                InternalName = TryGetOptionalString(reader, "internal_name"),
                OwnerId = reader.GetString(reader.GetOrdinal("owner_id")),
                OwnerEmail = reader.GetString(reader.GetOrdinal("owner_email")),
                CompanyId = reader.GetGuid(reader.GetOrdinal("company_id")),
                FieldIds = reader.GetString(reader.GetOrdinal("field_ids")),
                Filters = reader.IsDBNull(reader.GetOrdinal("filters")) ? null : reader.GetString(reader.GetOrdinal("filters")),
                Aggregations = reader.IsDBNull(reader.GetOrdinal("aggregations")) ? null : reader.GetString(reader.GetOrdinal("aggregations")),
                ColumnState = reader.IsDBNull(reader.GetOrdinal("column_state")) ? null : reader.GetString(reader.GetOrdinal("column_state")),
                GridTemplateId = reader.IsDBNull(reader.GetOrdinal("grid_template_id")) ? null : reader.GetGuid(reader.GetOrdinal("grid_template_id")),
                ConnectionId = TryGetOptionalGuid(reader, "connection_id"),
                PrimaryTable = TryGetOptionalString(reader, "primary_table"),
                LastRunAt = reader.IsDBNull(reader.GetOrdinal("last_run_at")) ? null : reader.GetDateTime(reader.GetOrdinal("last_run_at")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("updated_at"))
            });
        }
        return reports;
    }

    private static async Task<List<ReportShare>> ReadSharesAsync(SqlCommand cmd)
    {
        var shares = new List<ReportShare>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            shares.Add(new ReportShare
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                ReportId = reader.GetGuid(reader.GetOrdinal("report_id")),
                SharedWithId = reader.GetString(reader.GetOrdinal("shared_with_id")),
                SharedWithType = reader.GetString(reader.GetOrdinal("shared_with_type")),
                Permission = reader.GetString(reader.GetOrdinal("permission")),
                SharedById = reader.GetString(reader.GetOrdinal("shared_by_id")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"))
            });
        }
        return shares;
    }

    // Returns NULL when the column name doesn't exist in the reader (migration
    // not yet applied) or when the value is DBNull. Keeps schedule reads working
    // against an older DB schema without crashing.
    private static string? GetNullableString(SqlDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetString(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    private static DateTime? GetNullableDate(SqlDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetDateTime(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    private static async Task<List<ReportSchedule>> ReadSchedulesAsync(SqlCommand cmd)
    {
        var schedules = new List<ReportSchedule>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schedules.Add(new ReportSchedule
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                ReportId = reader.GetGuid(reader.GetOrdinal("report_id")),
                OwnerId = reader.GetString(reader.GetOrdinal("owner_id")),
                OwnerEmail = reader.GetString(reader.GetOrdinal("owner_email")),
                CronExpression = reader.GetString(reader.GetOrdinal("cron_expression")),
                SchedulePatternJson = GetNullableString(reader, "schedule_pattern"),
                StartDate = GetNullableDate(reader, "start_date"),
                EndDate = GetNullableDate(reader, "end_date"),
                Subject = reader.GetString(reader.GetOrdinal("subject")),
                Recipients = GetNullableString(reader, "recipients"),
                CcRecipients = GetNullableString(reader, "cc_recipients"),
                BccRecipients = GetNullableString(reader, "bcc_recipients"),
                AttachmentFormat = reader.GetString(reader.GetOrdinal("attachment_format")),
                IncludePreview = reader.GetBoolean(reader.GetOrdinal("include_preview")),
                IsActive = reader.GetBoolean(reader.GetOrdinal("is_active")),
                LastRunAt = reader.IsDBNull(reader.GetOrdinal("last_run_at")) ? null : reader.GetDateTime(reader.GetOrdinal("last_run_at")),
                LastRunStatus = reader.IsDBNull(reader.GetOrdinal("last_run_status")) ? null : reader.GetString(reader.GetOrdinal("last_run_status")),
                ConsecutiveFailures = reader.GetInt32(reader.GetOrdinal("consecutive_failures")),
                HangfireJobId = reader.IsDBNull(reader.GetOrdinal("hangfire_job_id")) ? null : reader.GetString(reader.GetOrdinal("hangfire_job_id")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"))
            });
        }
        return schedules;
    }

    private static void AddReportParams(SqlCommand cmd, SavedReport r)
    {
        cmd.Parameters.Add(new SqlParameter("@Id", r.Id));
        cmd.Parameters.Add(new SqlParameter("@Name", r.Name));
        cmd.Parameters.Add(new SqlParameter("@InternalName",
            string.IsNullOrWhiteSpace(r.InternalName) ? DBNull.Value : (object)r.InternalName));
        cmd.Parameters.Add(new SqlParameter("@OwnerId", r.OwnerId));
        cmd.Parameters.Add(new SqlParameter("@OwnerEmail", r.OwnerEmail));
        cmd.Parameters.Add(new SqlParameter("@FieldIds", r.FieldIds));
        cmd.Parameters.Add(new SqlParameter("@Filters", (object?)r.Filters ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Aggregations", (object?)r.Aggregations ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ColumnState", (object?)r.ColumnState ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@GridTemplateId", (object?)r.GridTemplateId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ConnectionId", (object?)r.ConnectionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@PrimaryTable", (object?)r.PrimaryTable ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@LastRunAt", (object?)r.LastRunAt ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", r.CreatedAt));
        cmd.Parameters.Add(new SqlParameter("@UpdatedAt", r.UpdatedAt));
    }

    // Tolerate a missing column (e.g. migration not yet applied on some env)
    // by returning null instead of throwing. Callers fall back to null-means-default.
    private static Guid? TryGetOptionalGuid(SqlDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetGuid(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    private static string? TryGetOptionalString(SqlDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetString(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }
}
