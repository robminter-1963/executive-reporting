using System.Data;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Phase 3: Tabs + tiles are company-scoped only — every write replaces the
// canonical layout for every viewer of that company. Mutation methods
// enforce admin role server-side via EnsureCompanyAdmin so a UI bypass
// (dev tools, accidentally missing role gate on a future button) can't
// silently overwrite the admin's layout. Editor / Scheduler / Viewer
// roles are intentionally blocked from layout edits — pending product
// decision on personal layouts vs an approval queue.
public class MasterDashboardService : IMasterDashboardService
{
    private readonly string _connectionString;
    private readonly IAdminService _admins;

    public MasterDashboardService(IConfiguration configuration, IAdminService admins)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _admins = admins;
    }

    // Throws on anything other than a global or company admin. Methods that
    // know their target companyId pass it; methods keyed only by tab/tile id
    // look the company up first (one round-trip; layout edits are rare).
    private void EnsureCompanyAdmin(Guid companyId, string? userEmail)
    {
        if (!string.IsNullOrEmpty(userEmail) && _admins.IsCompanyAdmin(userEmail, companyId)) return;
        throw new UnauthorizedAccessException(
            "Only admins can change the master dashboard layout. Editor / Scheduler / Viewer roles are intentionally blocked pending product decision.");
    }

    private async Task<Guid?> ResolveCompanyForTabAsync(int tabId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT company_id FROM EMPOWER.RPT_master_dashboard_tabs WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", tabId));
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid g ? g : null;
    }

    private async Task<Guid?> ResolveCompanyForTileAsync(int tileId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT company_id FROM EMPOWER.RPT_master_dashboard_tiles WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", tileId));
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid g ? g : null;
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

    public async Task<MasterDashboardTab> AddTabAsync(Guid companyId, string label, string? userEmail)
    {
        EnsureCompanyAdmin(companyId, userEmail);

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

    public async Task UpdateTabAsync(MasterDashboardTab tab, string? userEmail)
    {
        EnsureCompanyAdmin(tab.CompanyId, userEmail);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_master_dashboard_tabs SET label = @Label WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Label", tab.Label));
        cmd.Parameters.Add(new SqlParameter("@Id", tab.Id));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveTabAsync(int tabId, string? userEmail)
    {
        var companyId = await ResolveCompanyForTabAsync(tabId);
        if (companyId is null) return; // already gone — nothing to authorize against
        EnsureCompanyAdmin(companyId.Value, userEmail);

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

    public async Task UpdateTabOrderAsync(List<MasterDashboardTab> tabs, string? userEmail)
    {
        if (tabs is null || tabs.Count == 0) return;
        // All tabs in a single call must belong to the same company — anything
        // else is a misuse. Authorize against the first tab's company.
        EnsureCompanyAdmin(tabs[0].CompanyId, userEmail);

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
        //
        // Pull filters + updated_at so the AddTileDialog can show a per-row
        // subtitle distinguishing reports that share a name (e.g. one
        // "Active Pipeline" per loan type). Sort by name then most-recently-
        // edited so duplicates cluster and the freshest copy is on top.
        await using var cmd = new SqlCommand(@"
            SELECT id, name, internal_name, owner_id, filters, column_state, updated_at
            FROM EMPOWER.RPT_saved_reports
            WHERE company_id = @CompanyId
              AND (column_state LIKE '%""ShowOnMaster"":true%'
                   OR column_state LIKE '%""ShowOnMaster"": true%')
            ORDER BY ISNULL(internal_name, name), updated_at DESC", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            reports.Add(new SavedReport
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                InternalName = reader.IsDBNull(2) ? null : reader.GetString(2),
                OwnerId = reader.GetString(3),
                Filters = reader.IsDBNull(4) ? null : reader.GetString(4),
                ColumnState = reader.IsDBNull(5) ? null : reader.GetString(5),
                UpdatedAt = reader.GetDateTime(6)
            });
        }
        return reports;
    }

    public async Task AddTileAsync(Guid companyId, int tabId, Guid reportId, string? userEmail, int colSpan = 12)
    {
        EnsureCompanyAdmin(companyId, userEmail);

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

    public async Task MoveTileToTabAsync(int tileId, int targetTabId, string? userEmail)
    {
        var companyId = await ResolveCompanyForTileAsync(tileId);
        if (companyId is null) return; // tile already gone — nothing to authorize against
        EnsureCompanyAdmin(companyId.Value, userEmail);

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

    public async Task RemoveTileAsync(int tileId, string? userEmail)
    {
        var companyId = await ResolveCompanyForTileAsync(tileId);
        if (companyId is null) return;
        EnsureCompanyAdmin(companyId.Value, userEmail);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_master_dashboard_tiles WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", tileId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateLayoutAsync(List<MasterDashboardTile> tiles, string? userEmail)
    {
        if (tiles is null || tiles.Count == 0) return;
        // All tiles in a single call must belong to the same company.
        EnsureCompanyAdmin(tiles[0].CompanyId, userEmail);

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
