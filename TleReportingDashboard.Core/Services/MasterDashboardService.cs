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
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly IAuditLogger _audit;

    public MasterDashboardService(
        IConfiguration configuration,
        IAdminService admins,
        ConfigDbCache cache,
        EditorModeState editorMode,
        IAuditLogger audit)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _admins = admins;
        _cache = cache;
        _editorMode = editorMode;
        _audit = audit;
    }

    // Resource-id format for dashboard audit rows. Tabs / sections / tiles
    // are int identities; we prefix with the company guid so a reviewer
    // filtering by resource_id sees a complete per-company history without
    // needing to join.
    private static string ResId(Guid companyId, int childId) => $"{companyId}#{childId}";

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

    // Resolve the company that owns a section — via its parent tab, since
    // sections inherit company scope through tab_id rather than carrying it
    // directly. Used by every section write path so authorization can be
    // verified without trusting a caller-supplied companyId.
    private async Task<Guid?> ResolveCompanyForSectionAsync(int sectionId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            SELECT t.company_id
            FROM EMPOWER.RPT_master_dashboard_sections s
            INNER JOIN EMPOWER.RPT_master_dashboard_tabs t ON t.id = s.tab_id
            WHERE s.id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", sectionId));
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid g ? g : null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Tabs
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<MasterDashboardTab>> GetTabsAsync(Guid companyId)
    {
        // Defensive copy — same aliasing concern as GetTilesAsync. The
        // page mutates `_tabs` for drag-drop reorder and add/remove flows.
        var cached = await _cache.GetOrAddAsync(
            ConfigDbCache.Key("MasterDashboardService", "Tabs", companyId),
            async () =>
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
            },
            bypass: _editorMode.IsActive);
        return new List<MasterDashboardTab>(cached);
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
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Create,
            resourceType: AuditResources.DashboardTab,
            resourceId: ResId(companyId, newId),
            resourceLabel: $"Tab '{label}' (company {companyId})",
            before: null,
            after: new { CompanyId = companyId, Id = newId, Label = label });
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
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Update,
            resourceType: AuditResources.DashboardTab,
            resourceId: ResId(tab.CompanyId, tab.Id),
            resourceLabel: $"Tab '{tab.Label}'",
            before: null,
            after: new { tab.Id, tab.Label, tab.TitleAlign });
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
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Delete,
            resourceType: AuditResources.DashboardTab,
            resourceId: ResId(companyId.Value, tabId),
            resourceLabel: null,
            before: new { CompanyId = companyId.Value, TabId = tabId },
            after: null,
            notes: "tab + child tiles deleted");
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
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Reorder,
            resourceType: AuditResources.DashboardTab,
            resourceId: tabs[0].CompanyId.ToString(),
            resourceLabel: $"{tabs.Count} tabs (company {tabs[0].CompanyId})",
            before: null,
            after: new { Order = tabs.Select(t => new { t.Id, t.Label, t.SortOrder }).ToArray() });
    }

    // Single helper for write paths — drops every cache entry under this
    // service's prefix in one call, so a write doesn't have to enumerate
    // which specific keys it might have invalidated. Kept as a private
    // method so the prefix string isn't repeated all over the file.
    private void InvalidateLayoutCache() => _cache.Invalidate("MasterDashboardService:");

    // ════════════════════════════════════════════════════════════════════════
    // Tiles
    // ════════════════════════════════════════════════════════════════════════

    public Task<Dictionary<Guid, List<string>>> GetPlacedReportTabsAsync(Guid companyId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("MasterDashboardService", "PlacedReportTabs", companyId),
            async () =>
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
            },
            bypass: _editorMode.IsActive);

    public async Task<List<MasterDashboardTile>> GetTilesAsync(Guid companyId, int tabId)
    {
        // Defensive copy on return so callers can mutate freely without
        // poisoning the cache. The page (`MasterDashboard.razor`) does
        // `_tiles.Add/Remove/Insert/RemoveAt/RemoveAll` for drag-drop,
        // section delete, etc. Without this copy those mutations rewrite
        // the cached list in place via reference aliasing — fine until
        // the next user / session reads the cache and gets the mutated
        // result. Closing the browser doesn't help because the cache is
        // a process-singleton.
        var cached = await _cache.GetOrAddAsync(
            ConfigDbCache.Key("MasterDashboardService", "Tiles", companyId, tabId),
            async () =>
            {
                var tiles = new List<MasterDashboardTile>();
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(@"
                    SELECT t.id, t.company_id, t.tab_id, t.report_id, r.name,
                           t.sort_order, t.col_span, t.height, t.title_align, t.section_id
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
                        TitleAlign = reader.IsDBNull(8) ? "left" : reader.GetString(8),
                        SectionId = reader.IsDBNull(9) ? null : reader.GetInt32(9)
                    });
                }
                return tiles;
            },
            bypass: _editorMode.IsActive);
        return new List<MasterDashboardTile>(cached);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Sections
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<MasterDashboardSection>> GetSectionsAsync(int tabId)
    {
        // Defensive copy — same aliasing concern as GetTilesAsync. The
        // page mutates `_sections` for drag-drop reorder and section
        // add/remove flows; without the copy those land in the cache.
        var cached = await _cache.GetOrAddAsync(
            ConfigDbCache.Key("MasterDashboardService", "Sections", tabId),
            async () =>
            {
                var sections = new List<MasterDashboardSection>();
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(@"
                    SELECT id, tab_id, label, sort_order, title_align, collapsed
                    FROM EMPOWER.RPT_master_dashboard_sections
                    WHERE tab_id = @TabId
                    ORDER BY sort_order", conn);
                cmd.Parameters.Add(new SqlParameter("@TabId", tabId));

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sections.Add(new MasterDashboardSection
                    {
                        Id = reader.GetInt32(0),
                        TabId = reader.GetInt32(1),
                        Label = reader.GetString(2),
                        SortOrder = reader.GetInt32(3),
                        TitleAlign = reader.IsDBNull(4) ? "left" : reader.GetString(4),
                        Collapsed = !reader.IsDBNull(5) && reader.GetBoolean(5)
                    });
                }
                return sections;
            },
            bypass: _editorMode.IsActive);
        return new List<MasterDashboardSection>(cached);
    }

    public async Task<MasterDashboardSection> AddSectionAsync(int tabId, string label, string? userEmail)
    {
        var companyId = await ResolveCompanyForTabAsync(tabId)
            ?? throw new InvalidOperationException($"Tab {tabId} not found.");
        EnsureCompanyAdmin(companyId, userEmail);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // 10/tab cap enforced server-side so a bypassed UI can't blow past it.
        await using (var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM EMPOWER.RPT_master_dashboard_sections WHERE tab_id = @TabId", conn))
        {
            countCmd.Parameters.Add(new SqlParameter("@TabId", tabId));
            var count = (int)(await countCmd.ExecuteScalarAsync())!;
            if (count >= IMasterDashboardService.MaxSectionsPerTab)
                throw new InvalidOperationException(
                    $"This tab already has the maximum of {IMasterDashboardService.MaxSectionsPerTab} sections.");
        }

        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_master_dashboard_sections (tab_id, label, sort_order)
            OUTPUT INSERTED.id
            VALUES (@TabId, @Label,
                ISNULL((SELECT MAX(sort_order) + 1
                          FROM EMPOWER.RPT_master_dashboard_sections
                         WHERE tab_id = @TabId), 0))", conn);
        cmd.Parameters.Add(new SqlParameter("@TabId", tabId));
        cmd.Parameters.Add(new SqlParameter("@Label", label));
        var newId = (int)(await cmd.ExecuteScalarAsync())!;
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Create,
            resourceType: AuditResources.DashboardSection,
            resourceId: ResId(companyId, newId),
            resourceLabel: $"Section '{label}' (tab {tabId})",
            before: null,
            after: new { TabId = tabId, Id = newId, Label = label });
        return new MasterDashboardSection { Id = newId, TabId = tabId, Label = label };
    }

    public async Task RenameSectionAsync(int sectionId, string label, string? userEmail)
    {
        var companyId = await ResolveCompanyForSectionAsync(sectionId);
        if (companyId is null) return;
        EnsureCompanyAdmin(companyId.Value, userEmail);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_master_dashboard_sections SET label = @Label WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Label", label));
        cmd.Parameters.Add(new SqlParameter("@Id", sectionId));
        await cmd.ExecuteNonQueryAsync();
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Update,
            resourceType: AuditResources.DashboardSection,
            resourceId: ResId(companyId.Value, sectionId),
            resourceLabel: $"Section renamed to '{label}'",
            before: null,
            after: new { Id = sectionId, Label = label });
    }

    public async Task UpdateSectionOrderAsync(List<MasterDashboardSection> sections, string? userEmail)
    {
        if (sections is null || sections.Count == 0) return;
        var companyId = await ResolveCompanyForSectionAsync(sections[0].Id);
        if (companyId is null) return;
        EnsureCompanyAdmin(companyId.Value, userEmail);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        foreach (var s in sections)
        {
            await using var cmd = new SqlCommand(@"
                UPDATE EMPOWER.RPT_master_dashboard_sections
                   SET sort_order = @Order, label = @Label, title_align = @Align
                 WHERE id = @Id", conn);
            cmd.Parameters.Add(new SqlParameter("@Order", s.SortOrder));
            cmd.Parameters.Add(new SqlParameter("@Label", s.Label));
            cmd.Parameters.Add(new SqlParameter("@Align", string.IsNullOrEmpty(s.TitleAlign) ? "left" : s.TitleAlign));
            cmd.Parameters.Add(new SqlParameter("@Id", s.Id));
            await cmd.ExecuteNonQueryAsync();
        }
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Reorder,
            resourceType: AuditResources.DashboardSection,
            resourceId: companyId.Value.ToString(),
            resourceLabel: $"{sections.Count} sections",
            before: null,
            after: new { Order = sections.Select(s => new { s.Id, s.Label, s.SortOrder }).ToArray() });
    }

    public async Task RemoveSectionAsync(int sectionId, string? userEmail)
    {
        var companyId = await ResolveCompanyForSectionAsync(sectionId);
        if (companyId is null) return;
        EnsureCompanyAdmin(companyId.Value, userEmail);

        // Two-step under one transaction. The migration uses NO ACTION on
        // the tiles.section_id FK (avoids the SQL Server multi-cascade-path
        // warning), so the app has to clear references before deleting the
        // section row. Tiles fall back to section_id = NULL → render under
        // "(no section)".
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var cmd1 = new SqlCommand(
            "UPDATE EMPOWER.RPT_master_dashboard_tiles SET section_id = NULL WHERE section_id = @Id",
            conn, (SqlTransaction)tx))
        {
            cmd1.Parameters.Add(new SqlParameter("@Id", sectionId));
            await cmd1.ExecuteNonQueryAsync();
        }

        await using (var cmd2 = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_master_dashboard_sections WHERE id = @Id",
            conn, (SqlTransaction)tx))
        {
            cmd2.Parameters.Add(new SqlParameter("@Id", sectionId));
            await cmd2.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Delete,
            resourceType: AuditResources.DashboardSection,
            resourceId: ResId(companyId.Value, sectionId),
            resourceLabel: null,
            before: new { Id = sectionId },
            after: null,
            notes: "tiles fell back to (no section)");
    }

    public async Task SetSectionCollapsedAsync(int sectionId, bool collapsed, string? userEmail)
    {
        var companyId = await ResolveCompanyForSectionAsync(sectionId);
        if (companyId is null) return;
        EnsureCompanyAdmin(companyId.Value, userEmail);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_master_dashboard_sections SET collapsed = @Collapsed WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Collapsed", collapsed));
        cmd.Parameters.Add(new SqlParameter("@Id", sectionId));
        await cmd.ExecuteNonQueryAsync();
        InvalidateLayoutCache();
    }

    public async Task SetSectionAlignAsync(int sectionId, string align, string? userEmail)
    {
        var companyId = await ResolveCompanyForSectionAsync(sectionId);
        if (companyId is null) return;
        EnsureCompanyAdmin(companyId.Value, userEmail);

        // Whitelist the value rather than passing through whatever the
        // caller supplied — title_align is a varchar(10) column, but the
        // semantics only support left/center/right and we don't want stray
        // values silently breaking the CSS text-align mapping later.
        var normalized = align?.ToLowerInvariant() switch
        {
            "center" => "center",
            "right" => "right",
            _ => "left"
        };

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_master_dashboard_sections SET title_align = @Align WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Align", normalized));
        cmd.Parameters.Add(new SqlParameter("@Id", sectionId));
        await cmd.ExecuteNonQueryAsync();
        InvalidateLayoutCache();
    }

    public async Task MoveSectionToTabAsync(int sectionId, int targetTabId, string? userEmail)
    {
        var sourceCompanyId = await ResolveCompanyForSectionAsync(sectionId);
        if (sourceCompanyId is null) return;
        EnsureCompanyAdmin(sourceCompanyId.Value, userEmail);

        var targetCompanyId = await ResolveCompanyForTabAsync(targetTabId);
        if (targetCompanyId is null || targetCompanyId.Value != sourceCompanyId.Value)
            throw new InvalidOperationException(
                "Cannot move a section to a tab that belongs to a different company.");

        // Two updates under one transaction:
        //  (1) Reseat the section onto the target tab and append it at
        //      the end of that tab's section list.
        //  (2) Reseat every tile that referenced this section. Tiles get
        //      fresh sort_order values at the end of the target tab,
        //      preserving their relative order via ROW_NUMBER().
        // Doing both in one transaction guarantees a half-moved section
        // (rows referencing tab A while the section sits on tab B) can
        // never persist if either statement fails.
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var cmd1 = new SqlCommand(@"
            UPDATE EMPOWER.RPT_master_dashboard_sections
            SET tab_id = @TargetTabId,
                sort_order = ISNULL((SELECT MAX(sort_order) + 1
                                       FROM EMPOWER.RPT_master_dashboard_sections
                                       WHERE tab_id = @TargetTabId), 0)
            WHERE id = @SectionId", conn, (SqlTransaction)tx))
        {
            cmd1.Parameters.Add(new SqlParameter("@TargetTabId", targetTabId));
            cmd1.Parameters.Add(new SqlParameter("@SectionId", sectionId));
            await cmd1.ExecuteNonQueryAsync();
        }

        await using (var cmd2 = new SqlCommand(@"
            WITH tiles_to_move AS (
                SELECT id, ROW_NUMBER() OVER (ORDER BY sort_order) AS rn
                FROM EMPOWER.RPT_master_dashboard_tiles
                WHERE section_id = @SectionId
            ),
            target_max AS (
                SELECT ISNULL(MAX(sort_order), -1) AS m
                FROM EMPOWER.RPT_master_dashboard_tiles
                WHERE tab_id = @TargetTabId
            )
            UPDATE t
            SET t.tab_id = @TargetTabId,
                t.sort_order = (SELECT m FROM target_max) + ttm.rn,
                -- Self-heal company_id alongside tab_id. The
                -- same-company guard above already blocks cross-
                -- company section moves, but a tile carrying a stale
                -- company_id from earlier corruption gets cleaned up
                -- here as a side effect of the section move.
                t.company_id = (SELECT company_id FROM EMPOWER.RPT_master_dashboard_tabs WHERE id = @TargetTabId)
            FROM EMPOWER.RPT_master_dashboard_tiles t
            INNER JOIN tiles_to_move ttm ON ttm.id = t.id", conn, (SqlTransaction)tx))
        {
            cmd2.Parameters.Add(new SqlParameter("@SectionId", sectionId));
            cmd2.Parameters.Add(new SqlParameter("@TargetTabId", targetTabId));
            await cmd2.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Update,
            resourceType: AuditResources.DashboardSection,
            resourceId: ResId(sourceCompanyId.Value, sectionId),
            resourceLabel: $"Section moved to tab {targetTabId}",
            before: null,
            after: new { SectionId = sectionId, TargetTabId = targetTabId },
            notes: "section + child tiles reseated");
    }

    public async Task MoveTileToSectionAsync(int tileId, int? sectionId, string? userEmail)
    {
        var tileCompanyId = await ResolveCompanyForTileAsync(tileId);
        if (tileCompanyId is null) return;
        EnsureCompanyAdmin(tileCompanyId.Value, userEmail);

        // Cross-company guard: when assigning to a section, verify it lives
        // under a tab in the same company as the tile. Without this, an admin
        // of company A could pin a tile of A onto a section of company B by
        // guessing ids — broken auth even if the visual UI never offers it.
        if (sectionId is int sid)
        {
            var sectionCompanyId = await ResolveCompanyForSectionAsync(sid);
            if (sectionCompanyId is null || sectionCompanyId.Value != tileCompanyId.Value)
                throw new InvalidOperationException(
                    "Cannot move a tile into a section that belongs to a different company.");
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_master_dashboard_tiles SET section_id = @SectionId WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@SectionId", (object?)sectionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Id", tileId));
        await cmd.ExecuteNonQueryAsync();
        InvalidateLayoutCache();
    }

    public Task<List<SavedReport>> GetAvailableReportsAsync(Guid companyId, string? userId = null) =>
        _cache.GetOrAddAsync(
            // Cache key is per-(company, user) because the shared-with-me
            // arm of the UNION is user-specific. Same user revisiting the
            // picker hits the cache; share grants/revokes invalidate via
            // the "MasterDashboardService:" prefix from the share service.
            ConfigDbCache.Key("MasterDashboardService", "AvailableReports", companyId, userId ?? string.Empty),
            async () =>
            {
                var reports = new List<SavedReport>();
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                // Two-arm UNION, both scoped to the user's current company:
                //  1) Company-scoped: any report whose company_id matches
                //     the user's current company and has ShowOnMaster on.
                //     Mirrors the original behavior — every user in the
                //     company can pin any Power User's ShowOnMaster report.
                //  2) Shared-with-me: a report explicitly shared to the
                //     user via RPT_report_shares — STILL constrained to
                //     the user's current company. Shares from reports in
                //     other companies never surface here; pinning a
                //     foreign-company report would let the user reach data
                //     their company-access grants don't include.
                // UNION dedupes so a report that's both owner-in-company
                // AND shared with the user surfaces once. The ShowOnMaster
                // filter applies to both arms — owner controls eligibility.
                //
                // Pull filters + updated_at so the AddTileDialog can show a
                // per-row subtitle distinguishing reports that share a name.
                // Sort by name then most-recently-edited so duplicates
                // cluster and the freshest copy is on top.
                //
                // When userId is null/empty the shared arm is skipped — keeps
                // the legacy plan for callers that pass company only.
                // owner_email is projected so the personal-pin picker can
                // filter out reports authored by an admin (caller checks
                // the email against the admin roster). The admin
                // "Add Report" flow ignores the column.
                // Wrap the UNION arms in a derived table so the ORDER BY can
                // reference the ISNULL() expression. SQL Server otherwise
                // rejects ORDER BY expressions against a UNION ("must appear
                // in the select list") even though both source columns ARE
                // in the SELECT — the restriction is about the expression
                // shape, not the underlying columns.
                var sql = string.IsNullOrEmpty(userId)
                    ? @"SELECT id, name, internal_name, owner_id, owner_email, filters, column_state, updated_at
                        FROM EMPOWER.RPT_saved_reports
                        WHERE company_id = @CompanyId
                          AND (column_state LIKE '%""ShowOnMaster"":true%'
                               OR column_state LIKE '%""ShowOnMaster"": true%')
                        ORDER BY ISNULL(internal_name, name), updated_at DESC"
                    : @"SELECT id, name, internal_name, owner_id, owner_email, filters, column_state, updated_at
                        FROM (
                            SELECT id, name, internal_name, owner_id, owner_email, filters, column_state, updated_at
                            FROM EMPOWER.RPT_saved_reports
                            WHERE company_id = @CompanyId
                              AND (column_state LIKE '%""ShowOnMaster"":true%'
                                   OR column_state LIKE '%""ShowOnMaster"": true%')
                            UNION
                            SELECT r.id, r.name, r.internal_name, r.owner_id, r.owner_email, r.filters, r.column_state, r.updated_at
                            FROM EMPOWER.RPT_saved_reports r
                            INNER JOIN EMPOWER.RPT_report_shares s ON s.report_id = r.id
                            WHERE (s.shared_with_id = @UserId OR s.shared_with_type = 'everyone')
                              AND r.company_id = @CompanyId
                              AND (r.column_state LIKE '%""ShowOnMaster"":true%'
                                   OR r.column_state LIKE '%""ShowOnMaster"": true%')
                        ) AS combined
                        ORDER BY ISNULL(internal_name, name), updated_at DESC";
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
                if (!string.IsNullOrEmpty(userId))
                    cmd.Parameters.Add(new SqlParameter("@UserId", userId));

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    reports.Add(new SavedReport
                    {
                        Id = reader.GetGuid(0),
                        Name = reader.GetString(1),
                        InternalName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        OwnerId = reader.GetString(3),
                        OwnerEmail = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        Filters = reader.IsDBNull(5) ? null : reader.GetString(5),
                        ColumnState = reader.IsDBNull(6) ? null : reader.GetString(6),
                        UpdatedAt = reader.GetDateTime(7)
                    });
                }
                return reports;
            },
            bypass: _editorMode.IsActive);

    public async Task AddTileAsync(Guid companyId, int tabId, Guid reportId, string? userEmail, int colSpan = 12, int? sectionId = null)
    {
        EnsureCompanyAdmin(companyId, userEmail);

        // Defensive: when a section was specified, verify it lives on the
        // same tab the tile is being inserted into. Without this check, an
        // admin could pin a tile under a section belonging to a different
        // tab (or different company entirely) by guessing ids — broken
        // auth even when the visual UI never offers the choice.
        if (sectionId is int sid)
        {
            await using var verifyConn = new SqlConnection(_connectionString);
            await verifyConn.OpenAsync();
            await using var verifyCmd = new SqlCommand(
                "SELECT tab_id FROM EMPOWER.RPT_master_dashboard_sections WHERE id = @Id", verifyConn);
            verifyCmd.Parameters.Add(new SqlParameter("@Id", sid));
            var sectionTabIdObj = await verifyCmd.ExecuteScalarAsync();
            var sectionTabId = sectionTabIdObj is int v ? (int?)v : null;
            if (sectionTabId is null || sectionTabId.Value != tabId)
                throw new InvalidOperationException(
                    "Cannot add a tile under a section that doesn't belong to the chosen tab.");
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_master_dashboard_tiles (company_id, source_company_id, tab_id, report_id, sort_order, col_span, height, section_id)
            SELECT @CompanyId, @CompanyId, @TabId, @ReportId,
                   ISNULL((SELECT MAX(sort_order) + 1
                             FROM EMPOWER.RPT_master_dashboard_tiles
                            WHERE company_id = @CompanyId AND tab_id = @TabId), 0),
                   @ColSpan, 500, @SectionId
            WHERE NOT EXISTS (
                SELECT 1 FROM EMPOWER.RPT_master_dashboard_tiles
                WHERE company_id = @CompanyId AND tab_id = @TabId AND report_id = @ReportId
            )", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
        cmd.Parameters.Add(new SqlParameter("@TabId", tabId));
        cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
        cmd.Parameters.Add(new SqlParameter("@ColSpan", colSpan));
        cmd.Parameters.Add(new SqlParameter("@SectionId", (object?)sectionId ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync();
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Create,
            resourceType: AuditResources.DashboardTile,
            resourceId: $"{companyId}#tab{tabId}#report{reportId}",
            resourceLabel: $"Tile for report {reportId} (tab {tabId})",
            before: null,
            after: new { CompanyId = companyId, TabId = tabId, ReportId = reportId, ColSpan = colSpan, SectionId = sectionId });
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
        // Re-sync company_id alongside tab_id. Without this, moving a tile
        // between tabs of different companies leaves the tile carrying its
        // OLD company_id; the cross-company guard on MoveTileToSectionAsync
        // then rejects perfectly valid same-company moves later because the
        // stale company_id no longer matches the section's parent tab.
        // Also clear any section_id — the old section lives under the old
        // tab and would dangle.
        await using var cmd = new SqlCommand(@"
            UPDATE t
               SET t.tab_id = @TargetTabId,
                   t.company_id = tb.company_id,
                   t.section_id = NULL,
                   t.sort_order = ISNULL(
                       (SELECT MAX(sort_order) + 1
                          FROM EMPOWER.RPT_master_dashboard_tiles
                         WHERE tab_id = @TargetTabId),
                       0)
              FROM EMPOWER.RPT_master_dashboard_tiles t
              JOIN EMPOWER.RPT_master_dashboard_tabs   tb ON tb.id = @TargetTabId
             WHERE t.id = @Id
               AND t.tab_id <> @TargetTabId;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", tileId));
        cmd.Parameters.Add(new SqlParameter("@TargetTabId", targetTabId));
        await cmd.ExecuteNonQueryAsync();
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Update,
            resourceType: AuditResources.DashboardTile,
            resourceId: ResId(companyId.Value, tileId),
            resourceLabel: $"Tile moved to tab {targetTabId}",
            before: null,
            after: new { TileId = tileId, TargetTabId = targetTabId },
            notes: "tile re-tabbed");
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
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Delete,
            resourceType: AuditResources.DashboardTile,
            resourceId: ResId(companyId.Value, tileId),
            resourceLabel: null,
            before: new { TileId = tileId },
            after: null);
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
        InvalidateLayoutCache();
        await _audit.LogAsync(
            actorEmail: userEmail,
            action: AuditActions.Reorder,
            resourceType: AuditResources.DashboardTile,
            resourceId: tiles[0].CompanyId.ToString(),
            resourceLabel: $"{tiles.Count} tiles (layout)",
            before: null,
            after: new { Tiles = tiles.Select(t => new { t.Id, t.SortOrder, t.ColSpan, t.Height, t.TitleAlign }).ToArray() });
    }

    // ── Per-user personal tile pins ──
    // Layered on top of the shared tiles; only the owning user sees them.
    // No admin gate — every user can manage their own pins. The
    // MasterDashboard loads shared + personal for the active tab and
    // merges by sort_order at render time.
    //
    // Cache: not cached. Personal pin sets are small (single-digit rows
    // typical), reads happen on tab activation, and adding a cache layer
    // would need per-user key shapes that fragment the cache pool.

    public async Task<List<MasterDashboardTile>> GetPersonalTilesAsync(string userId, Guid companyId, int tabId)
    {
        var tiles = new List<MasterDashboardTile>();
        if (string.IsNullOrWhiteSpace(userId)) return tiles;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // Join RPT_saved_reports for ReportName (matches the shared-tile
        // SELECT shape exactly so the in-memory model is identical apart
        // from IsPersonal). Guard with TRY/CATCH on object-missing so an
        // env that hasn't applied the 2026-06-02 migration yet just sees
        // an empty personal layer instead of an exception.
        try
        {
            await using var cmd = new SqlCommand(@"
                SELECT t.id, t.company_id, t.tab_id, t.report_id, r.name,
                       t.sort_order, t.col_span, t.height, t.title_align, t.section_id
                FROM EMPOWER.RPT_master_dashboard_personal_tiles t
                INNER JOIN EMPOWER.RPT_saved_reports r ON r.id = t.report_id
                WHERE t.user_id = @UserId AND t.company_id = @CompanyId AND t.tab_id = @TabId
                ORDER BY t.sort_order", conn);
            cmd.Parameters.Add(new SqlParameter("@UserId", userId));
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
                    TitleAlign = reader.IsDBNull(8) ? "left" : reader.GetString(8),
                    SectionId = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    IsPersonal = true
                });
            }
        }
        catch (SqlException ex) when (ex.IsObjectMissing())
        {
            // Migration hasn't run on this DB yet — return empty so the
            // dashboard renders the shared layer only. The exception text
            // surfaces in the bound ILogger output if anyone tails it;
            // MasterDashboardService doesn't carry its own ILogger field
            // (consistent with the rest of the service's exception
            // handling, e.g. RemoveTabAsync), so this is intentionally
            // a silent swallow.
            _ = ex;
        }
        return tiles;
    }

    public async Task<MasterDashboardTile> AddPersonalTileAsync(
        string userId, Guid companyId, int tabId, Guid reportId,
        int colSpan = 12, int? sectionId = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));

        // Section sanity-check: when supplied, the section must belong to
        // the same tab. Mirrors the guard on AddTileAsync — users picking
        // a section from the active tab's section list shouldn't be able
        // to slip in another tab's section id by API misuse.
        if (sectionId is int sid)
        {
            await using var verifyConn = new SqlConnection(_connectionString);
            await verifyConn.OpenAsync();
            await using var verifyCmd = new SqlCommand(
                "SELECT tab_id FROM EMPOWER.RPT_master_dashboard_sections WHERE id = @Id", verifyConn);
            verifyCmd.Parameters.Add(new SqlParameter("@Id", sid));
            var sectionTabIdObj = await verifyCmd.ExecuteScalarAsync();
            var sectionTabId = sectionTabIdObj is int v ? (int?)v : null;
            if (sectionTabId is null || sectionTabId.Value != tabId)
                throw new InvalidOperationException(
                    "Cannot pin under a section that doesn't belong to the chosen tab.");
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // Cross-company guard: the report being pinned MUST belong to the
        // same company the user is pinning under. Personal pins never
        // cross company boundaries — even a report shared with the user
        // has to live in their current company to be pinnable (the picker
        // already enforces this on its SQL side; this is the defense-in-
        // depth check so a bypassed UI can't slip a foreign-company
        // report id through here).
        await using (var ccCheck = new SqlCommand(
            "SELECT company_id FROM EMPOWER.RPT_saved_reports WHERE id = @Id", conn))
        {
            ccCheck.Parameters.Add(new SqlParameter("@Id", reportId));
            var reportCompanyObj = await ccCheck.ExecuteScalarAsync();
            var reportCompanyId = reportCompanyObj is Guid rc ? (Guid?)rc : null;
            if (reportCompanyId is null)
                throw new InvalidOperationException("Report not found.");
            if (reportCompanyId.Value != companyId)
                throw new InvalidOperationException(
                    "Personal pins must belong to the same company. " +
                    "Cross-company pinning is not supported.");
        }
        // MERGE so re-adding the same report on the same tab is idempotent
        // (the unique index would otherwise throw). On match, leave the
        // existing row untouched — the user already had it pinned, no
        // re-position. OUTPUT returns the id of the inserted (or matched)
        // row so the caller can build the in-memory tile.
        await using var cmd = new SqlCommand(@"
            MERGE EMPOWER.RPT_master_dashboard_personal_tiles AS t
            USING (SELECT @UserId AS user_id, @TabId AS tab_id, @ReportId AS report_id) AS s
               ON t.user_id = s.user_id AND t.tab_id = s.tab_id AND t.report_id = s.report_id
            WHEN NOT MATCHED THEN
                INSERT (user_id, company_id, tab_id, report_id, sort_order, col_span, section_id)
                VALUES (@UserId, @CompanyId, @TabId, @ReportId,
                        ISNULL((SELECT MAX(sort_order) + 1
                                  FROM EMPOWER.RPT_master_dashboard_personal_tiles
                                 WHERE user_id = @UserId AND tab_id = @TabId), 0),
                        @ColSpan, @SectionId)
            OUTPUT INSERTED.id, INSERTED.sort_order, INSERTED.col_span,
                   INSERTED.height, INSERTED.title_align, INSERTED.section_id;",
            conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
        cmd.Parameters.Add(new SqlParameter("@TabId", tabId));
        cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
        cmd.Parameters.Add(new SqlParameter("@ColSpan", colSpan));
        cmd.Parameters.Add(new SqlParameter("@SectionId", (object?)sectionId ?? DBNull.Value));

        int newId = 0, sortOrder = 0, persistedColSpan = colSpan, height = 500;
        string? titleAlign = null;
        int? persistedSectionId = sectionId;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                newId = reader.GetInt32(0);
                sortOrder = reader.GetInt32(1);
                persistedColSpan = reader.GetInt32(2);
                height = reader.GetInt32(3);
                titleAlign = reader.IsDBNull(4) ? null : reader.GetString(4);
                persistedSectionId = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            }
        }

        // OUTPUT returns nothing for the WHEN MATCHED branch (we left the
        // row alone). Re-query to surface the existing tile so the caller
        // always gets a populated row.
        if (newId == 0)
        {
            await using var rq = new SqlCommand(@"
                SELECT id, sort_order, col_span, height, title_align, section_id
                  FROM EMPOWER.RPT_master_dashboard_personal_tiles
                 WHERE user_id = @UserId AND tab_id = @TabId AND report_id = @ReportId;",
                conn);
            rq.Parameters.Add(new SqlParameter("@UserId", userId));
            rq.Parameters.Add(new SqlParameter("@TabId", tabId));
            rq.Parameters.Add(new SqlParameter("@ReportId", reportId));
            await using var rqReader = await rq.ExecuteReaderAsync();
            if (await rqReader.ReadAsync())
            {
                newId = rqReader.GetInt32(0);
                sortOrder = rqReader.GetInt32(1);
                persistedColSpan = rqReader.GetInt32(2);
                height = rqReader.GetInt32(3);
                titleAlign = rqReader.IsDBNull(4) ? null : rqReader.GetString(4);
                persistedSectionId = rqReader.IsDBNull(5) ? null : rqReader.GetInt32(5);
            }
        }

        // Fetch report name in one extra round-trip so the caller doesn't
        // have to re-resolve. Small cost on what's already an admin action.
        string reportName = string.Empty;
        await using (var nameCmd = new SqlCommand(
            "SELECT name FROM EMPOWER.RPT_saved_reports WHERE id = @Id;", conn))
        {
            nameCmd.Parameters.Add(new SqlParameter("@Id", reportId));
            var nm = await nameCmd.ExecuteScalarAsync();
            reportName = nm as string ?? string.Empty;
        }

        return new MasterDashboardTile
        {
            Id = newId,
            CompanyId = companyId,
            TabId = tabId,
            ReportId = reportId,
            ReportName = reportName,
            SortOrder = sortOrder,
            ColSpan = persistedColSpan,
            Height = height,
            TitleAlign = string.IsNullOrEmpty(titleAlign) ? "left" : titleAlign!,
            SectionId = persistedSectionId,
            IsPersonal = true
        };
    }

    public async Task<HashSet<Guid>> GetPersonalPlacedReportIdsAsync(string userId)
    {
        var result = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(userId)) return result;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        try
        {
            // SELECT DISTINCT is cheap on the IX_personal_tiles_user_company_tab
            // index — user_id is the leading column, so it's a focused range
            // scan even at scale.
            await using var cmd = new SqlCommand(@"
                SELECT DISTINCT report_id
                  FROM EMPOWER.RPT_master_dashboard_personal_tiles
                 WHERE user_id = @UserId;", conn);
            cmd.Parameters.Add(new SqlParameter("@UserId", userId));
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(reader.GetGuid(0));
        }
        catch (SqlException ex) when (ex.IsObjectMissing())
        {
            // Migration not applied yet — no personal pins exist, so the
            // empty set is the correct fallback. Consistent with the
            // GetPersonalTilesAsync handler above.
            _ = ex;
        }
        return result;
    }

    public async Task RemovePersonalTileAsync(string userId, int personalTileId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // user_id in the WHERE clause IS the authorization — a user can
        // only remove their own pins. Calling with someone else's tile id
        // matches zero rows and silently no-ops.
        await using var cmd = new SqlCommand(@"
            DELETE FROM EMPOWER.RPT_master_dashboard_personal_tiles
             WHERE id = @Id AND user_id = @UserId;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", personalTileId));
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        await cmd.ExecuteNonQueryAsync();
    }

    // Persist personal-pin layout changes (size + title alignment). Unlike
    // the shared/canonical path (UpdateLayoutAsync) which is admin-only and
    // batches a whole tab's worth at once, this fires immediately on each
    // resize/align mutation because personal pins have no "Save Layout"
    // button — users aren't in edit mode when they tweak their own tiles.
    // user_id in the WHERE is the authorization (same posture as the
    // remove path); foreign tile ids silently no-op.
    //
    // titleAlign: pass "left", "center", "right" (or null to clear back to
    // default). Any other string passes through verbatim — the rendering
    // side falls back to "left" for unrecognized values.
    public async Task UpdatePersonalTileLayoutAsync(string userId, int personalTileId,
        int colSpan, int height, string? titleAlign)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_master_dashboard_personal_tiles
               SET col_span    = @ColSpan,
                   height      = @Height,
                   title_align = @TitleAlign
             WHERE id = @Id AND user_id = @UserId;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", personalTileId));
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        cmd.Parameters.Add(new SqlParameter("@ColSpan", colSpan));
        cmd.Parameters.Add(new SqlParameter("@Height", height));
        cmd.Parameters.Add(new SqlParameter("@TitleAlign",
            string.IsNullOrWhiteSpace(titleAlign) ? (object)DBNull.Value : titleAlign));
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Per-user tab visibility ──
    // Hidden state lives in RPT_user_hidden_dashboard_tabs (PK on (user_id,
    // tab_id), FK CASCADE on tab_id). Existence = hidden; absence = visible.
    // No admin gate — users manage their own view of the shared layout.
    // Not cached: the read happens once per dashboard mount and the row
    // count per user is bounded by the number of tabs they have access to.

    public async Task<HashSet<int>> GetHiddenTabIdsAsync(string userId, Guid companyId)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrEmpty(userId)) return result;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // Join through master_dashboard_tabs so the result is implicitly
        // scoped to the requested company. Stale rows for tabs that moved
        // companies (shouldn't happen) would silently drop out instead of
        // bleeding across tenants.
        await using var cmd = new SqlCommand(@"
            SELECT h.tab_id
              FROM EMPOWER.RPT_user_hidden_dashboard_tabs h
              JOIN EMPOWER.RPT_master_dashboard_tabs    t ON t.id = h.tab_id
             WHERE h.user_id = @UserId
               AND t.company_id = @CompanyId", conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetInt32(0));
        }
        return result;
    }

    public async Task SetTabHiddenAsync(string userId, int tabId, bool hidden)
    {
        if (string.IsNullOrEmpty(userId)) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        if (hidden)
        {
            // Idempotent insert — re-hiding an already-hidden tab is a
            // silent no-op rather than a PK violation. INSERT...WHERE NOT
            // EXISTS keeps this single-round-trip without MERGE overhead.
            await using var cmd = new SqlCommand(@"
                INSERT INTO EMPOWER.RPT_user_hidden_dashboard_tabs (user_id, tab_id)
                SELECT @UserId, @TabId
                 WHERE NOT EXISTS (
                    SELECT 1 FROM EMPOWER.RPT_user_hidden_dashboard_tabs
                     WHERE user_id = @UserId AND tab_id = @TabId
                 )", conn);
            cmd.Parameters.Add(new SqlParameter("@UserId", userId));
            cmd.Parameters.Add(new SqlParameter("@TabId", tabId));
            await cmd.ExecuteNonQueryAsync();
        }
        else
        {
            await using var cmd = new SqlCommand(
                "DELETE FROM EMPOWER.RPT_user_hidden_dashboard_tabs WHERE user_id = @UserId AND tab_id = @TabId", conn);
            cmd.Parameters.Add(new SqlParameter("@UserId", userId));
            cmd.Parameters.Add(new SqlParameter("@TabId", tabId));
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
