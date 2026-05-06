using System.Data;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class ReportDbService : IReportService, ISharingService, IScheduleService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly ILogger<ReportDbService> _logger;

    public ReportDbService(
        IConfiguration configuration,
        ConfigDbCache cache,
        EditorModeState editorMode,
        ILogger<ReportDbService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _editorMode = editorMode;
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

    public Task<List<SavedReport>> GetReportsAsync(string userId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Reports", "ByOwner", userId),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    "SELECT * FROM EMPOWER.RPT_saved_reports WHERE owner_id = @UserId ORDER BY name", conn);
                cmd.Parameters.Add(new SqlParameter("@UserId", userId));
                return await ReadReportsAsync(cmd);
            },
            bypass: _editorMode.IsActive);

    public Task<List<SavedReport>> GetAllReportsAsync() =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Reports", "All"),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    "SELECT * FROM EMPOWER.RPT_saved_reports ORDER BY name", conn);
                return await ReadReportsAsync(cmd);
            },
            bypass: _editorMode.IsActive);

    public Task<SavedReport?> GetReportByIdAsync(Guid id) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Reports", "ById", id),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    "SELECT * FROM EMPOWER.RPT_saved_reports WHERE id = @Id", conn);
                cmd.Parameters.Add(new SqlParameter("@Id", id));
                var rows = await ReadReportsAsync(cmd);
                return rows.FirstOrDefault();
            },
            bypass: _editorMode.IsActive);

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
              (id, name, internal_name, category, owner_id, owner_email, field_ids, filters, aggregations, column_state, grid_template_id, connection_id, company_id, primary_table, last_run_at, created_at, updated_at)
              VALUES (@Id, @Name, @InternalName, @Category, @OwnerId, @OwnerEmail, @FieldIds, @Filters, @Aggregations, @ColumnState, @GridTemplateId, @ConnectionId,
                      (SELECT company_id FROM EMPOWER.RPT_company_connections WHERE id = @ConnectionId),
                      @PrimaryTable, @LastRunAt, @CreatedAt, @UpdatedAt)", conn);
        AddReportParams(cmd, report);
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("ReportDbService:Reports:");
        _cache.Invalidate("ReportDbService:Shares:");
        _cache.Invalidate("MasterDashboardService:");
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
              name = @Name, internal_name = @InternalName, category = @Category, field_ids = @FieldIds, filters = @Filters, aggregations = @Aggregations,
              column_state = @ColumnState, grid_template_id = @GridTemplateId, connection_id = @ConnectionId,
              company_id = (SELECT company_id FROM EMPOWER.RPT_company_connections WHERE id = @ConnectionId),
              primary_table = @PrimaryTable,
              last_run_at = @LastRunAt, updated_at = @UpdatedAt
              WHERE id = @Id AND owner_id = @OwnerId", conn);
        AddReportParams(cmd, report);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) throw new UnauthorizedAccessException("Report not found or not owned by user.");
        _cache.Invalidate("ReportDbService:Reports:");
        // Renaming or any other report mutation has to invalidate the
        // shared-with-me caches too — those keys (Shares:SharedWith:<userId>)
        // hold List<SavedReport> snapshots that include the joined name.
        // Without this, a viewer's "Shared With Me" tab keeps the old
        // name until the next cache eviction, which is exactly the bug
        // the user reported when renaming a shared report.
        _cache.Invalidate("ReportDbService:Shares:");
        _cache.Invalidate("MasterDashboardService:");
        return report;
    }

    public async Task<List<string>> GetDistinctCategoriesAsync()
    {
        await using var conn = await OpenConnectionAsync();
        // The IX_saved_reports_category filtered index makes this a cheap
        // index-only scan even at 10k+ reports. ORDER BY in SQL so the
        // caller doesn't re-sort.
        await using var cmd = new SqlCommand(
            @"SELECT DISTINCT category
                FROM EMPOWER.RPT_saved_reports
               WHERE category IS NOT NULL AND LTRIM(RTRIM(category)) <> ''
            ORDER BY category;", conn);
        var result = new List<string>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                    result.Add(reader.GetString(0));
            }
        }
        catch (SqlException ex) when (ex.Number == 207) // Invalid column name
        {
            // Migration hasn't run on this DB yet; return empty so the UI
            // falls through to a free-text input. Logged at debug — admin
            // applies the migration to enable the feature.
            _logger.LogDebug(ex, "category column not present yet — returning empty list.");
        }
        return result;
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
        _cache.Invalidate("ReportDbService:Reports:");
        // ON DELETE CASCADE on RPT_report_shares.report_id removes the
        // share rows, but the SharedWith caches still hold the deleted
        // report. Drop those so users it was shared to don't see a
        // ghost entry until eviction.
        _cache.Invalidate("ReportDbService:Shares:");
        _cache.Invalidate("MasterDashboardService:");
    }

    // ════════════════════════════════════════════════════════════════════════
    // ISharingService
    // ════════════════════════════════════════════════════════════════════════

    public Task<List<ReportShare>> GetSharesForReportAsync(Guid reportId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Shares", "ByReport", reportId),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    "SELECT * FROM EMPOWER.RPT_report_shares WHERE report_id = @ReportId", conn);
                cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
                return await ReadSharesAsync(cmd);
            },
            bypass: _editorMode.IsActive);

    public Task<List<SavedReport>> GetSharedWithMeAsync(string userId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Shares", "SharedWith", userId),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    @"SELECT r.* FROM EMPOWER.RPT_saved_reports r
                      INNER JOIN EMPOWER.RPT_report_shares s ON r.id = s.report_id
                      WHERE s.shared_with_id = @UserId ORDER BY r.name", conn);
                cmd.Parameters.Add(new SqlParameter("@UserId", userId));
                return await ReadReportsAsync(cmd);
            },
            bypass: _editorMode.IsActive);

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
        _cache.Invalidate("ReportDbService:Shares:");
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
        _cache.Invalidate("ReportDbService:Shares:");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IScheduleService
    // ════════════════════════════════════════════════════════════════════════

    public Task<List<ReportSchedule>> GetSchedulesForReportAsync(Guid reportId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Schedules", "ByReport", reportId),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    "SELECT * FROM EMPOWER.RPT_report_schedules WHERE report_id = @ReportId", conn);
                cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
                return await ReadSchedulesAsync(cmd);
            },
            bypass: _editorMode.IsActive);

    public Task<List<ReportSchedule>> GetSchedulesForUserAsync(string userId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Schedules", "ByUser", userId),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    "SELECT * FROM EMPOWER.RPT_report_schedules WHERE owner_id = @UserId", conn);
                cmd.Parameters.Add(new SqlParameter("@UserId", userId));
                return await ReadSchedulesAsync(cmd);
            },
            bypass: _editorMode.IsActive);

    public Task<List<ReportSchedule>> GetAllSchedulesAsync() =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("ReportDbService", "Schedules", "All"),
            async () =>
            {
                await using var conn = await OpenConnectionAsync();
                await using var cmd = new SqlCommand(
                    "SELECT * FROM EMPOWER.RPT_report_schedules", conn);
                return await ReadSchedulesAsync(cmd);
            },
            bypass: _editorMode.IsActive);

    public async Task<ReportSchedule> CreateScheduleAsync(ReportSchedule schedule)
    {
        schedule.Id = schedule.Id == Guid.Empty ? Guid.NewGuid() : schedule.Id;
        schedule.CreatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            @"INSERT INTO EMPOWER.RPT_report_schedules
              (id, report_id, owner_id, owner_email, cron_expression, schedule_pattern, start_date, end_date,
               subject, recipients, cc_recipients, bcc_recipients,
               kind, team_id, team_connection_id, dist_email,
               attachment_format, include_preview, is_active, created_at)
              VALUES (@Id, @ReportId, @OwnerId, @OwnerEmail, @Cron, @Pattern, @StartDate, @EndDate,
                      @Subject, @Recipients, @Cc, @Bcc,
                      @Kind, @TeamId, @TeamConnId, @DistEmail,
                      @Format, @Preview, @Active, @CreatedAt)", conn);
        AddScheduleParams(cmd, schedule);
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", schedule.CreatedAt));
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("ReportDbService:Schedules:");
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
              kind = @Kind, team_id = @TeamId, team_connection_id = @TeamConnId, dist_email = @DistEmail,
              attachment_format = @Format, include_preview = @Preview, is_active = @Active
              WHERE id = @Id AND owner_id = @OwnerId", conn);
        AddScheduleParams(cmd, schedule);
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("ReportDbService:Schedules:");
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
        // Kind is persisted as the lowercase string ('individual' /
        // 'distribution') rather than the integer enum so the column
        // stays human-readable from SSMS — matches the CHECK constraint
        // in the migration.
        cmd.Parameters.Add(new SqlParameter("@Kind",
            s.Kind == ScheduleKind.Individual ? "individual" : "distribution"));
        cmd.Parameters.Add(new SqlParameter("@TeamId", (object?)s.TeamId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@TeamConnId", (object?)s.TeamConnectionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@DistEmail", (object?)s.DistEmail ?? DBNull.Value));
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
        _cache.Invalidate("ReportDbService:Schedules:");
    }

    public async Task DeleteScheduleAsync(Guid scheduleId, string userId)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_report_schedules WHERE id = @Id AND owner_id = @UserId", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("ReportDbService:Schedules:");
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
                // TryGet so envs that haven't applied the category migration
                // yet keep working — null falls back to "uncategorized" everywhere.
                Category = TryGetOptionalString(reader, "category"),
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
                // Kind / team / dist columns are read defensively via
                // TryGetOptional* — envs that haven't applied the
                // 2026-05-05 schedule_kind migration return nulls here
                // and the row falls through to the legacy owner-email
                // distribution path in the Worker.
                Kind = ParseScheduleKind(TryGetOptionalString(reader, "kind")),
                TeamId = TryGetOptionalInt(reader, "team_id"),
                TeamConnectionId = TryGetOptionalGuid(reader, "team_connection_id"),
                DistEmail = TryGetOptionalString(reader, "dist_email"),
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
        cmd.Parameters.Add(new SqlParameter("@Category",
            string.IsNullOrWhiteSpace(r.Category) ? DBNull.Value : (object)r.Category));
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

    private static int? TryGetOptionalInt(SqlDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetInt32(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    // Maps the lowercase string persisted by AddScheduleParams back to
    // the enum. Null / unknown values default to Distribution so a
    // pre-migration env (no `kind` column) reads as the legacy single-
    // recipient mode and the Worker's owner-email fallback kicks in.
    private static ScheduleKind ParseScheduleKind(string? raw) =>
        string.Equals(raw, "individual", StringComparison.OrdinalIgnoreCase)
            ? ScheduleKind.Individual
            : ScheduleKind.Distribution;
}
