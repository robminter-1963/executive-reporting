using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class CompanyAdminService : ICompanyAdminService
{
    private readonly string _connStr;
    private readonly ICompanyRegistry _registry;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly IAuditLogger _audit;
    private readonly ILogger<CompanyAdminService> _logger;

    public CompanyAdminService(
        IConfiguration configuration,
        ICompanyRegistry registry,
        ConfigDbCache cache,
        EditorModeState editorMode,
        IAuditLogger audit,
        ILogger<CompanyAdminService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for CompanyAdminService.");
        _registry = registry;
        _cache = cache;
        _editorMode = editorMode;
        _audit = audit;
        _logger = logger;
    }

    // Audit-safe projection of a CompanyRecord. Strips the logo blob (not
    // useful in a diff and bloats the audit JSON) and converts the
    // content-type to a bool so a logo-replace diffs cleanly as "had logo"
    // → "had logo" instead of two opaque base64 strings.
    private static object ForAudit(CompanyRecord c) => new
    {
        c.Id, c.Code, c.Name, c.WebsiteUrl, c.IsActive, c.IsHidden, c.DisplayOrder,
        HasLogo = c.Logo is { Length: > 0 },
        c.LogoContentType
    };

    public Task<List<CompanyRecord>> GetAllAsync(CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CompanyAdminService", "All"),
            async () =>
            {
                var rows = new List<CompanyRecord>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                // Include logo + logo_content_type so the admin tab can render a
                // preview thumbnail without a second round-trip. The logo bytes
                // are typically < 500 KB so pulling them up-front is cheap.
                // is_hidden read defensively via COL_LENGTH for envs that
                // haven't applied the 2026-05-05_18-00 migration yet.
                await using var cmd = new SqlCommand(@"
                    SELECT id, code, name, is_active,
                           created_at, updated_at, logo, logo_content_type, display_order, website_url,
                           CASE WHEN COL_LENGTH('EMPOWER.RPT_companies','is_hidden') IS NULL
                                THEN CAST(0 AS BIT) ELSE is_hidden END AS is_hidden
                      FROM EMPOWER.RPT_companies
                     ORDER BY is_active DESC, display_order, name;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new CompanyRecord(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetBoolean(3),
                        reader.GetDateTime(4),
                        reader.GetDateTime(5))
                    {
                        Logo = reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6),
                        LogoContentType = reader.IsDBNull(7) ? null : reader.GetString(7),
                        DisplayOrder = reader.GetInt32(8),
                        WebsiteUrl = reader.IsDBNull(9) ? null : reader.GetString(9),
                        IsHidden = reader.GetBoolean(10)
                    });
                }
                return rows;
            },
            bypass: _editorMode.IsActive);

    public async Task UpdateDisplayOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // One UPDATE per row — the list is tiny (one per company) so a batched
        // MERGE or TVP is overkill. Each row sets display_order to its index
        // in the caller-supplied ordering.
        for (var i = 0; i < orderedIds.Count; i++)
        {
            await using var cmd = new SqlCommand(@"
                UPDATE EMPOWER.RPT_companies
                   SET display_order = @order,
                       updated_at    = SYSUTCDATETIME()
                 WHERE id = @id;", conn, tx);
            cmd.Parameters.Add(new SqlParameter("@id", orderedIds[i]));
            cmd.Parameters.Add(new SqlParameter("@order", i));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company display order updated: {Count} companies re-ordered", orderedIds.Count);

        // One audit row for the whole reorder — the per-company display_order
        // update isn't a security event, but the curated ordering IS a shared
        // user-visible config change that auditors want recorded once.
        await _audit.LogAsync(
            actorEmail: null,
            action: AuditActions.Reorder,
            resourceType: AuditResources.Company,
            resourceId: null,
            resourceLabel: $"{orderedIds.Count} companies",
            before: null,
            after: new { OrderedIds = orderedIds.ToArray() });
    }

    public async Task<CompanyRecord> CreateAsync(string code, string name,
                                                  string? websiteUrl, string? createdBy, CancellationToken ct = default)
    {
        Validate(code, name);
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_companies (id, code, name, website_url, is_active)
            VALUES (@id, @code, @name, @url, 1);", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@code", code));
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@url", (object?)NormalizeUrl(websiteUrl) ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company created: {Code} by {CreatedBy}", code, createdBy ?? "unknown");
        var created = new CompanyRecord(id, code, name, true, now, now)
        {
            WebsiteUrl = NormalizeUrl(websiteUrl)
        };
        await _audit.LogAsync(
            actorEmail: createdBy,
            action: AuditActions.Create,
            resourceType: AuditResources.Company,
            resourceId: id.ToString(),
            resourceLabel: $"{name} ({code})",
            before: null,
            after: ForAudit(created));
        return created;
    }

    public async Task UpdateAsync(Guid id, string code, string name,
                                   string? websiteUrl, bool isActive, bool isHidden, CancellationToken ct = default)
    {
        Validate(code, name);

        // Pre-read for audit-log before-state.
        var existing = (await GetAllAsync(ct)).FirstOrDefault(c => c.Id == id);

        // Conditional UPDATE form: assigns is_hidden only when the column
        // exists, so this method works on envs that haven't yet applied
        // the 2026-05-05_18-00 migration. Once every env has migrated,
        // collapse to a plain UPDATE.
        var hasHiddenColumn = await ColumnExistsAsync("EMPOWER.RPT_companies", "is_hidden", ct);
        var sql = hasHiddenColumn
            ? @"UPDATE EMPOWER.RPT_companies
                   SET code = @code, name = @name,
                       website_url = @url, is_active = @active, is_hidden = @hidden,
                       updated_at = SYSUTCDATETIME()
                 WHERE id = @id;"
            : @"UPDATE EMPOWER.RPT_companies
                   SET code = @code, name = @name,
                       website_url = @url, is_active = @active,
                       updated_at = SYSUTCDATETIME()
                 WHERE id = @id;";

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@code", code));
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@url", (object?)NormalizeUrl(websiteUrl) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@active", isActive));
        if (hasHiddenColumn)
            cmd.Parameters.Add(new SqlParameter("@hidden", isHidden));
        await cmd.ExecuteNonQueryAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation(
            "Company updated: {Id} ({Code}), active={IsActive}, hidden={IsHidden}",
            id, code, isActive, hasHiddenColumn ? isHidden.ToString() : "n/a (pre-migration)");

        var afterRecord = (await GetAllAsync(ct)).FirstOrDefault(c => c.Id == id);
        await _audit.LogAsync(
            actorEmail: null,
            action: AuditActions.Update,
            resourceType: AuditResources.Company,
            resourceId: id.ToString(),
            resourceLabel: $"{name} ({code})",
            before: existing is null ? null : ForAudit(existing),
            after: afterRecord is null ? null : ForAudit(afterRecord));
    }

    private async Task<bool> ColumnExistsAsync(string fullTableName, string column, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT CASE WHEN COL_LENGTH(@t, @c) IS NULL THEN 0 ELSE 1 END;", conn);
        cmd.Parameters.Add(new SqlParameter("@t", fullTableName));
        cmd.Parameters.Add(new SqlParameter("@c", column));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i && i == 1;
    }

    // Empty/whitespace → null; bare "example.com" → "https://example.com" so
    // the href in the master dashboard actually navigates (browsers treat a
    // scheme-less href as relative to the current origin).
    private static string? NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        return "https://" + trimmed;
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_companies SET is_active = @active, updated_at = SYSUTCDATETIME() WHERE id = @id",
            conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@active", isActive));
        await cmd.ExecuteNonQueryAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company active-state changed: {Id} → {IsActive}", id, isActive);

        await _audit.LogAsync(
            actorEmail: null,
            action: isActive ? AuditActions.Enable : AuditActions.Disable,
            resourceType: AuditResources.Company,
            resourceId: id.ToString(),
            resourceLabel: null,
            before: null,
            after: new { IsActive = isActive });
    }

    public async Task SetHiddenAsync(Guid id, bool isHidden, CancellationToken ct = default)
    {
        // No-op gracefully on pre-migration envs — the toggle in the
        // admin UI will appear unchanged but the row stays not-hidden
        // (column doesn't exist yet). Apply the migration to enable.
        if (!await ColumnExistsAsync("EMPOWER.RPT_companies", "is_hidden", ct))
        {
            _logger.LogWarning("SetHiddenAsync called before is_hidden column exists — skipping update for {Id}", id);
            return;
        }

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "UPDATE EMPOWER.RPT_companies SET is_hidden = @hidden, updated_at = SYSUTCDATETIME() WHERE id = @id",
            conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@hidden", isHidden));
        await cmd.ExecuteNonQueryAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company hidden-state changed: {Id} → {IsHidden}", id, isHidden);

        await _audit.LogAsync(
            actorEmail: null,
            action: isHidden ? AuditActions.Hide : AuditActions.Show,
            resourceType: AuditResources.Company,
            resourceId: id.ToString(),
            resourceLabel: null,
            before: null,
            after: new { IsHidden = isHidden });
    }

    // Pre-flight count of dependent rows. Counts are cheap (COUNT(*) on
    // indexed company_id columns) so the admin sees the blast radius
    // BEFORE clicking Delete. The actual cascade re-walks the tables; if
    // a row gets added between this read and the delete it just goes
    // along for the ride. Tables already cascading via FK from companies
    // (user_companies, company_connections + their further cascades,
    // admins, kpis, library_sections, personal_tiles) are counted here
    // for transparency.
    public async Task<CompanyDeleteImpact> GetDeleteImpactAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        string companyName = string.Empty;
        await using (var nameCmd = new SqlCommand(
            "SELECT name FROM EMPOWER.RPT_companies WHERE id = @id", conn))
        {
            nameCmd.Parameters.Add(new SqlParameter("@id", id));
            var nameRaw = await nameCmd.ExecuteScalarAsync(ct);
            if (nameRaw is null) throw new KeyNotFoundException($"Company {id} not found.");
            companyName = nameRaw as string ?? string.Empty;
        }

        // One round-trip with a single batch query — cheaper than 14
        // individual COUNTs.
        const string sql = @"
            SELECT
              (SELECT COUNT(*) FROM EMPOWER.RPT_company_connections WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_saved_reports       WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_report_shares       WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_report_schedules    WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_grid_templates      WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_master_dashboard_tabs  WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_master_dashboard_tiles WHERE company_id = @id OR source_company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_library_sections    WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_company_kpis        WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_user_companies      WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_admins              WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_master_dashboard_personal_tiles WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_schema_config       WHERE company_id = @id),
              (SELECT COUNT(*) FROM EMPOWER.RPT_schema_config_history WHERE company_id = @id);";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Impact query returned no rows.");

        return new CompanyDeleteImpact(
            CompanyId: id,
            CompanyName: companyName,
            Connections: reader.GetInt32(0),
            SavedReports: reader.GetInt32(1),
            ReportShares: reader.GetInt32(2),
            ReportSchedules: reader.GetInt32(3),
            GridTemplates: reader.GetInt32(4),
            DashboardTabs: reader.GetInt32(5),
            DashboardTiles: reader.GetInt32(6),
            LibrarySections: reader.GetInt32(7),
            Kpis: reader.GetInt32(8),
            UserGrants: reader.GetInt32(9),
            Admins: reader.GetInt32(10),
            PersonalPins: reader.GetInt32(11),
            SchemaConfigs: reader.GetInt32(12),
            SchemaConfigHistoryRows: reader.GetInt32(13));
    }

    // Hard-delete a company and everything that belongs to it. Walks the
    // tables that have NO ACTION FKs to RPT_companies in dependency order
    // inside a single transaction, then drops the company row. Remaining
    // children (user_companies, company_connections + their further
    // cascades, admins, kpis, library_sections, personal_tiles,
    // user_preferences, schema_config_history) cascade via FK.
    public async Task DeleteAsync(Guid id, string? deletedBy, CancellationToken ct = default)
    {
        // Snapshot the impact BEFORE we mutate so the audit log captures
        // what was destroyed. Throws KeyNotFoundException if the company
        // doesn't exist, which is the right signal to the caller.
        var impact = await GetDeleteImpactAsync(id, ct);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // Order matters here because of FK dependencies in BOTH
            // directions. Reports cascade shares + schedules + batch_items
            // via report_id, so we don't need to delete those rows
            // explicitly — but we DO need to delete the report rows
            // themselves before the company row because saved_reports →
            // companies is NO ACTION (FK blocks the delete otherwise).
            //
            // Similarly: tabs → sections cascade is via tab_id, so deleting
            // tabs takes sections with them. Tiles have a second FK
            // (source_company_id) — delete by both columns.
            await ExecAsync(conn, tx, ct,
                "DELETE FROM EMPOWER.RPT_master_dashboard_tiles WHERE company_id = @id OR source_company_id = @id", id);
            await ExecAsync(conn, tx, ct,
                "DELETE FROM EMPOWER.RPT_master_dashboard_tabs WHERE company_id = @id", id);
            await ExecAsync(conn, tx, ct,
                "DELETE FROM EMPOWER.RPT_report_schedules WHERE company_id = @id", id);
            await ExecAsync(conn, tx, ct,
                "DELETE FROM EMPOWER.RPT_report_shares WHERE company_id = @id", id);
            await ExecAsync(conn, tx, ct,
                "DELETE FROM EMPOWER.RPT_saved_reports WHERE company_id = @id", id);
            await ExecAsync(conn, tx, ct,
                "DELETE FROM EMPOWER.RPT_grid_templates WHERE company_id = @id", id);
            await ExecAsync(conn, tx, ct,
                "DELETE FROM EMPOWER.RPT_schema_config WHERE company_id = @id", id);
            // Finally the company itself. Remaining children (user_companies,
            // company_connections + downstream teams/sources/custom_tables,
            // admins, kpis, library_sections, personal_tiles, user_preferences,
            // schema_config_history) drop via FK CASCADE.
            await using (var cmd = new SqlCommand(
                "DELETE FROM EMPOWER.RPT_companies WHERE id = @id", conn, tx))
            {
                cmd.Parameters.Add(new SqlParameter("@id", id));
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows == 0)
                    throw new KeyNotFoundException($"Company {id} disappeared mid-delete.");
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        // Cache invalidations. Cast wide — almost every per-company
        // service has a cache entry keyed (or prefixed) by company id;
        // an explicit invalidate prevents stale reads after the hard
        // delete. Master Dashboard tiles cache is keyed per (user,
        // company); blasting the whole prefix is cheap.
        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _cache.Invalidate("ReportDbService:");
        _cache.Invalidate("SchemaService:");
        _cache.Invalidate("MasterDashboardService:");
        _cache.Invalidate("ScheduleService:");
        _cache.Invalidate("ThemeService:");

        _logger.LogWarning(
            "Company deleted: {Id} '{Name}' by {User} (impact: {Reports} reports, {Tabs} tabs, {Tiles} tiles, {Connections} connections, {Schemas} schemas)",
            id, impact.CompanyName, deletedBy ?? "unknown",
            impact.SavedReports, impact.DashboardTabs, impact.DashboardTiles,
            impact.Connections, impact.SchemaConfigs);

        await _audit.LogAsync(
            actorEmail: deletedBy,
            action: AuditActions.Delete,
            resourceType: AuditResources.Company,
            resourceId: id.ToString(),
            resourceLabel: impact.CompanyName,
            before: impact,
            after: null);
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct, string sql, Guid id)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UploadLogoAsync(Guid id, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        if (bytes is null || bytes.Length == 0)
            throw new ArgumentException("Logo bytes are required. Use ClearLogoAsync to delete.", nameof(bytes));
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Content type is required.", nameof(contentType));

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_companies
               SET logo = @logo,
                   logo_content_type = @ct,
                   updated_at = SYSUTCDATETIME()
             WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@logo", System.Data.SqlDbType.VarBinary) { Value = bytes });
        cmd.Parameters.Add(new SqlParameter("@ct", contentType));
        await cmd.ExecuteNonQueryAsync(ct);

        // CompanySummary / ICompanyRegistry don't carry the logo today, so
        // an Invalidate() isn't strictly required — but future callers that
        // cache the full record would want a fresh read after an upload.
        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company logo uploaded: {Id} ({Bytes} bytes, {Ct})", id, bytes.Length, contentType);
    }

    public async Task ClearLogoAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_companies
               SET logo = NULL,
                   logo_content_type = NULL,
                   updated_at = SYSUTCDATETIME()
             WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company logo cleared: {Id}", id);
    }

    private static void Validate(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
    }
}
