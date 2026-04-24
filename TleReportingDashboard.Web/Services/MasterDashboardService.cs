using System.Data;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Phase 3: Tabs + tiles are company-scoped only. The page that calls this
// service (MasterDashboard.razor) gates the write paths on the signed-in
// user's admin flag — this service doesn't re-check it. Readers (GetTabs,
// GetTiles, GetPlacedReportTabs, GetAvailableReports) are safe for any
// user with access to the company.
public class MasterDashboardService : IMasterDashboardService
{
    private readonly string _connectionString;

    public MasterDashboardService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Tabs
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<MasterDashboardTab>> GetTabsAsync(Guid companyId)
    {
        var tabs = new List<MasterDashboardTab>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            SELECT id, company_id, label, sort_order, title_align
              FROM EMPOWER.RPT_master_dashboard_tabs
             WHERE company_id = @CompanyId
             ORDER BY sort_order", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tabs.Add(new MasterDashboardTab
            {
                Id = reader.GetInt32(0),
                CompanyId = reader.GetGuid(1),
                Label = reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                TitleAlign = reader.IsDBNull(4) ? "left" : reader.GetString(4)
            });
        }
        return tabs;
    }

    public async Task<MasterDashboardTab> AddTabAsync(Guid companyId, string label)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_master_dashboard_tabs (company_id, label, sort_order)
            OUTPUT INSERTED.id
            VALUES (@CompanyId, @Label,
                ISNULL((SELECT MAX(sort_order) + 1
                          FROM EMPOWER.RPT_master_dashboard_tabs
                         WHERE company_id = @CompanyId), 0))", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
        cmd.Parameters.Add(new SqlParameter("@Label", label));
        var newId = (int)(await cmd.ExecuteScalarAsync())!;
        return new MasterDashboardTab { Id = newId, CompanyId = companyId, Label = label };
    }

    public async Task UpdateTabAsync(MasterDashboardTab tab)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_master_dashboard_tabs SET label = @Label WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Label", tab.Label));
        cmd.Parameters.Add(new SqlParameter("@Id", tab.Id));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveTabAsync(int tabId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // Delete tiles in this tab first
        await using var cmd1 = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_master_dashboard_tiles WHERE tab_id = @TabId", conn);
        cmd1.Parameters.Add(new SqlParameter("@TabId", tabId));
        await cmd1.ExecuteNonQueryAsync();

        await using var cmd2 = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_master_dashboard_tabs WHERE id = @Id", conn);
        cmd2.Parameters.Add(new SqlParameter("@Id", tabId));
        await cmd2.ExecuteNonQueryAsync();
    }

    public async Task UpdateTabOrderAsync(List<MasterDashboardTab> tabs)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        foreach (var tab in tabs)
        {
            await using var cmd = new SqlCommand(
                "UPDATE EMPOWER.RPT_master_dashboard_tabs SET sort_order = @Order, label = @Label, title_align = @TitleAlign WHERE id = @Id", conn);
            cmd.Parameters.Add(new SqlParameter("@Order", tab.SortOrder));
            cmd.Parameters.Add(new SqlParameter("@Label", tab.Label));
            cmd.Parameters.Add(new SqlParameter("@TitleAlign", string.IsNullOrEmpty(tab.TitleAlign) ? "left" : tab.TitleAlign));
            cmd.Parameters.Add(new SqlParameter("@Id", tab.Id));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Tiles
    // ════════════════════════════════════════════════════════════════════════

    public async Task<Dictionary<Guid, List<string>>> GetPlacedReportTabsAsync(Guid companyId)
    {
        var result = new Dictionary<Guid, List<string>>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            SELECT DISTINCT tl.report_id, tb.label
            FROM EMPOWER.RPT_master_dashboard_tiles tl
            INNER JOIN EMPOWER.RPT_master_dashboard_tabs tb ON tb.id = tl.tab_id
            WHERE tl.company_id = @CompanyId
            ORDER BY tb.label", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var reportId = reader.GetGuid(0);
            var tabLabel = reader.GetString(1);
            if (!result.TryGetValue(reportId, out var labels))
                result[reportId] = labels = new List<string>();
            labels.Add(tabLabel);
        }
        return result;
    }

    public async Task<List<MasterDashboardTile>> GetTilesAsync(Guid companyId, int tabId)
    {
        var tiles = new List<MasterDashboardTile>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            SELECT t.id, t.company_id, t.tab_id, t.report_id, r.name, t.sort_order, t.col_span, t.height, t.title_align
            FROM EMPOWER.RPT_master_dashboard_tiles t
            INNER JOIN EMPOWER.RPT_saved_reports r ON r.id = t.report_id
            WHERE t.company_id = @CompanyId AND t.tab_id = @TabId
            ORDER BY t.sort_order", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
        cmd.Parameters.Add(new SqlParameter("@TabId", tabId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tiles.Add(new MasterDashboardTile
            {
                Id = reader.GetInt32(0),
                CompanyId = reader.GetGuid(1),
                TabId = reader.GetInt32(2),
                ReportId = reader.GetGuid(3),
                ReportName = reader.GetString(4),
                SortOrder = reader.GetInt32(5),
                ColSpan = reader.GetInt32(6),
                Height = reader.GetInt32(7),
                TitleAlign = reader.IsDBNull(8) ? "left" : reader.GetString(8)
            });
        }
        return tiles;
    }

    public async Task<List<SavedReport>> GetAvailableReportsAsync(Guid companyId)
    {
        var reports = new List<SavedReport>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // Reports are company-scoped too (saved_reports.company_id). Only
        // surface candidates that belong to the current company so tiles
        // can't accidentally pull another company's data.
        await using var cmd = new SqlCommand(@"
            SELECT id, name, owner_id, column_state
            FROM EMPOWER.RPT_saved_reports
            WHERE company_id = @CompanyId
              AND (column_state LIKE '%""ShowOnMaster"":true%'
                   OR column_state LIKE '%""ShowOnMaster"": true%')
            ORDER BY name", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            reports.Add(new SavedReport
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                OwnerId = reader.GetString(2),
                ColumnState = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }
        return reports;
    }

    public async Task AddTileAsync(Guid companyId, int tabId, Guid reportId, int colSpan = 12)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_master_dashboard_tiles (company_id, source_company_id, tab_id, report_id, sort_order, col_span, height)
            SELECT @CompanyId, @CompanyId, @TabId, @ReportId,
                   ISNULL((SELECT MAX(sort_order) + 1
                             FROM EMPOWER.RPT_master_dashboard_tiles
                            WHERE company_id = @CompanyId AND tab_id = @TabId), 0),
                   @ColSpan, 500
            WHERE NOT EXISTS (
                SELECT 1 FROM EMPOWER.RPT_master_dashboard_tiles
                WHERE company_id = @CompanyId AND tab_id = @TabId AND report_id = @ReportId
            )", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
        cmd.Parameters.Add(new SqlParameter("@TabId", tabId));
        cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
        cmd.Parameters.Add(new SqlParameter("@ColSpan", colSpan));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MoveTileToTabAsync(int tileId, int targetTabId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // Append to the target tab's sort order so the moved tile lands at
        // the end. No-op when the tile already lives on the target tab
        // (WHERE tab_id <> @TargetTabId).
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_master_dashboard_tiles
               SET tab_id = @TargetTabId,
                   sort_order = ISNULL(
                       (SELECT MAX(sort_order) + 1
                          FROM EMPOWER.RPT_master_dashboard_tiles
                         WHERE tab_id = @TargetTabId),
                       0)
             WHERE id = @Id
               AND tab_id <> @TargetTabId;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", tileId));
        cmd.Parameters.Add(new SqlParameter("@TargetTabId", targetTabId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveTileAsync(int tileId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_master_dashboard_tiles WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", tileId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateLayoutAsync(List<MasterDashboardTile> tiles)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        foreach (var tile in tiles)
        {
            await using var cmd = new SqlCommand(@"
                UPDATE EMPOWER.RPT_master_dashboard_tiles
                SET sort_order = @Order, col_span = @ColSpan, height = @Height, title_align = @TitleAlign
                WHERE id = @Id", conn);
            cmd.Parameters.Add(new SqlParameter("@Order", tile.SortOrder));
            cmd.Parameters.Add(new SqlParameter("@ColSpan", tile.ColSpan));
            cmd.Parameters.Add(new SqlParameter("@Height", tile.Height));
            cmd.Parameters.Add(new SqlParameter("@TitleAlign", string.IsNullOrEmpty(tile.TitleAlign) ? "left" : tile.TitleAlign));
            cmd.Parameters.Add(new SqlParameter("@Id", tile.Id));
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
