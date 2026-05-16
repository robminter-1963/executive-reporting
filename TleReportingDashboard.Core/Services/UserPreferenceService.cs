using System.Data;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class UserPreferenceService : IUserPreferenceService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;

    public UserPreferenceService(
        IConfiguration configuration,
        ConfigDbCache cache,
        EditorModeState editorMode)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _editorMode = editorMode;
    }

    public Task<UserPreference> GetPreferencesAsync(string userId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserPreferenceService", "ByUser", userId),
            () => GetPreferencesImplAsync(userId),
            bypass: _editorMode.IsActive);

    private async Task<UserPreference> GetPreferencesImplAsync(string userId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT user_id, onboarding_completed, default_page_size, is_dark_mode,
                     master_dashboard_title, master_dashboard_title_align,
                     master_dashboard_logo, master_dashboard_logo_type,
                     created_at, updated_at, report_library_page_size, report_page_sizes,
                     schema_builder_connection_id, schema_builder_company_id, report_library_company_id,
                     -- Defensive read of last_master_dashboard_seen — pre-migration
                     -- DBs return NULL via the CASE so the column reference
                     -- doesn't error. Reader still maps the alias the same.
                     CASE WHEN COL_LENGTH('EMPOWER.RPT_user_preferences','last_master_dashboard_seen') IS NULL
                          THEN CAST(NULL AS DATETIME) ELSE last_master_dashboard_seen END AS last_master_dashboard_seen
                FROM EMPOWER.RPT_user_preferences
               WHERE user_id = @UserId", conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new UserPreference
            {
                UserId = reader.GetString(0),
                OnboardingCompleted = reader.GetBoolean(1),
                DefaultPageSize = reader.GetInt32(2),
                IsDarkMode = reader.GetBoolean(3),
                MasterDashboardTitle = reader.IsDBNull(4) ? "Master Dashboard" : reader.GetString(4),
                MasterDashboardTitleAlign = reader.IsDBNull(5) ? "left" : reader.GetString(5),
                MasterDashboardLogo = reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6),
                MasterDashboardLogoType = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetDateTime(8),
                UpdatedAt = reader.GetDateTime(9),
                ReportLibraryPageSize = reader.OptInt("report_library_page_size") ?? 15,
                ReportPageSizes = ParseReportPageSizes(reader.OptString("report_page_sizes")),
                SchemaBuilderConnectionId = reader.OptGuid("schema_builder_connection_id"),
                SchemaBuilderCompanyId = reader.OptGuid("schema_builder_company_id"),
                ReportLibraryCompanyId = reader.OptGuid("report_library_company_id"),
                LastMasterDashboardSeen = reader.OptDate("last_master_dashboard_seen")
            };
        }

        return new UserPreference
        {
            UserId = userId,
            DefaultPageSize = 100,
            ReportLibraryPageSize = 15,
            IsDarkMode = false
        };
    }

    private static Dictionary<Guid, int> ParseReportPageSizes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (raw is null) return new();
            var result = new Dictionary<Guid, int>();
            foreach (var kvp in raw)
            {
                if (Guid.TryParse(kvp.Key, out var id) && kvp.Value > 0)
                    result[id] = kvp.Value;
            }
            return result;
        }
        catch { return new(); }
    }

    public async Task TouchLastMasterDashboardSeenAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // Dedicated UPDATE so concurrent writes from other tabs (e.g. the
        // theme picker) don't clobber unrelated columns. Pre-migration
        // databases (no last_master_dashboard_seen column) get caught by
        // the catch — silently no-op rather than blowing up the page load.
        try
        {
            await using var cmd = new SqlCommand(
                "UPDATE EMPOWER.RPT_user_preferences SET last_master_dashboard_seen = SYSUTCDATETIME() WHERE user_id = @UserId",
                conn);
            cmd.Parameters.Add(new SqlParameter("@UserId", userId));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException) { /* migration not applied yet — greeting falls back to "Welcome". */ }
        _cache.Invalidate(ConfigDbCache.Key("UserPreferenceService", "ByUser", userId));
    }

    public async Task SavePreferencesAsync(UserPreference preference)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // Serialize the per-report page-size map to JSON (string-keyed for
        // portability). Null when empty so we don't write useless "{}" rows.
        string? pageSizesJson = null;
        if (preference.ReportPageSizes is { Count: > 0 })
        {
            var stringKeyed = preference.ReportPageSizes.ToDictionary(
                kvp => kvp.Key.ToString(), kvp => kvp.Value);
            pageSizesJson = System.Text.Json.JsonSerializer.Serialize(stringKeyed);
        }

        // MERGE upsert
        await using var cmd = new SqlCommand(@"
            MERGE EMPOWER.RPT_user_preferences AS target
            USING (SELECT @UserId AS user_id) AS source
            ON target.user_id = source.user_id
            WHEN MATCHED THEN
                UPDATE SET default_page_size = @PageSize,
                           report_library_page_size = @LibPageSize,
                           report_page_sizes = @ReportPageSizes,
                           is_dark_mode = @IsDarkMode,
                           onboarding_completed = @Onboarding,
                           master_dashboard_title = @Title,
                           master_dashboard_title_align = @TitleAlign,
                           master_dashboard_logo = @Logo,
                           master_dashboard_logo_type = @LogoType,
                           schema_builder_connection_id = @SchemaConnId,
                           schema_builder_company_id = @SchemaCompanyId,
                           report_library_company_id = @LibCompanyId,
                           updated_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (user_id, default_page_size, report_library_page_size, report_page_sizes, is_dark_mode, onboarding_completed, master_dashboard_title, master_dashboard_title_align, master_dashboard_logo, master_dashboard_logo_type, schema_builder_connection_id, schema_builder_company_id, report_library_company_id, created_at, updated_at)
                VALUES (@UserId, @PageSize, @LibPageSize, @ReportPageSizes, @IsDarkMode, @Onboarding, @Title, @TitleAlign, @Logo, @LogoType, @SchemaConnId, @SchemaCompanyId, @LibCompanyId, SYSUTCDATETIME(), SYSUTCDATETIME());",
            conn);

        cmd.Parameters.Add(new SqlParameter("@UserId", preference.UserId));
        cmd.Parameters.Add(new SqlParameter("@PageSize", preference.DefaultPageSize));
        cmd.Parameters.Add(new SqlParameter("@LibPageSize", preference.ReportLibraryPageSize));
        cmd.Parameters.Add(new SqlParameter("@ReportPageSizes", (object?)pageSizesJson ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@IsDarkMode", preference.IsDarkMode));
        cmd.Parameters.Add(new SqlParameter("@Onboarding", preference.OnboardingCompleted));
        cmd.Parameters.Add(new SqlParameter("@Title", (object?)preference.MasterDashboardTitle ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@TitleAlign", preference.MasterDashboardTitleAlign));
        cmd.Parameters.Add(new SqlParameter("@Logo", SqlDbType.VarBinary) { Value = (object?)preference.MasterDashboardLogo ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@LogoType", (object?)preference.MasterDashboardLogoType ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@SchemaConnId", (object?)preference.SchemaBuilderConnectionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@SchemaCompanyId", (object?)preference.SchemaBuilderCompanyId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@LibCompanyId", (object?)preference.ReportLibraryCompanyId ?? DBNull.Value));

        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate(ConfigDbCache.Key("UserPreferenceService", "ByUser", preference.UserId));
    }
}
