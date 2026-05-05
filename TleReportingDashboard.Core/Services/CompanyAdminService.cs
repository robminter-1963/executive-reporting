using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class CompanyAdminService : ICompanyAdminService
{
    private readonly string _connStr;
    private readonly ICompanyRegistry _registry;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly ILogger<CompanyAdminService> _logger;

    public CompanyAdminService(
        IConfiguration configuration,
        ICompanyRegistry registry,
        ConfigDbCache cache,
        EditorModeState editorMode,
        ILogger<CompanyAdminService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for CompanyAdminService.");
        _registry = registry;
        _cache = cache;
        _editorMode = editorMode;
        _logger = logger;
    }

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
                await using var cmd = new SqlCommand(
                    "SELECT id, code, name, data_source_type, connection_ref, is_active, created_at, updated_at, logo, logo_content_type, display_order, website_url FROM EMPOWER.RPT_companies ORDER BY is_active DESC, display_order, name",
                    conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new CompanyRecord(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        reader.GetDateTime(6),
                        reader.GetDateTime(7))
                    {
                        Logo = reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8),
                        LogoContentType = reader.IsDBNull(9) ? null : reader.GetString(9),
                        DisplayOrder = reader.GetInt32(10),
                        WebsiteUrl = reader.IsDBNull(11) ? null : reader.GetString(11)
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
    }

    public async Task<CompanyRecord> CreateAsync(string code, string name, string dataSourceType, string connectionRef,
                                                  string? websiteUrl, string? createdBy, CancellationToken ct = default)
    {
        Validate(code, name, dataSourceType, connectionRef);
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_companies (id, code, name, data_source_type, connection_ref, website_url, is_active)
            VALUES (@id, @code, @name, @type, @ref, @url, 1);", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@code", code));
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@type", dataSourceType));
        cmd.Parameters.Add(new SqlParameter("@ref", connectionRef));
        cmd.Parameters.Add(new SqlParameter("@url", (object?)NormalizeUrl(websiteUrl) ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company created: {Code} by {CreatedBy}", code, createdBy ?? "unknown");
        return new CompanyRecord(id, code, name, dataSourceType, connectionRef, true, now, now)
        {
            WebsiteUrl = NormalizeUrl(websiteUrl)
        };
    }

    public async Task UpdateAsync(Guid id, string code, string name, string dataSourceType, string connectionRef,
                                   string? websiteUrl, bool isActive, CancellationToken ct = default)
    {
        Validate(code, name, dataSourceType, connectionRef);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_companies
            SET code = @code, name = @name, data_source_type = @type, connection_ref = @ref,
                website_url = @url, is_active = @active, updated_at = SYSUTCDATETIME()
            WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@code", code));
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@type", dataSourceType));
        cmd.Parameters.Add(new SqlParameter("@ref", connectionRef));
        cmd.Parameters.Add(new SqlParameter("@url", (object?)NormalizeUrl(websiteUrl) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@active", isActive));
        await cmd.ExecuteNonQueryAsync(ct);

        _registry.Invalidate();
        _cache.Invalidate("CompanyAdminService:");
        _logger.LogInformation("Company updated: {Id} ({Code}), active={IsActive}", id, code, isActive);
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

    private static void Validate(string code, string name, string dataSourceType, string connectionRef)
    {
        if (string.IsNullOrWhiteSpace(code))           throw new ArgumentException("Code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))           throw new ArgumentException("Name is required.", nameof(name));
        if (dataSourceType is not ("sqlserver" or "postgres"))
            throw new ArgumentException("data_source_type must be 'sqlserver' or 'postgres'.", nameof(dataSourceType));
        if (string.IsNullOrWhiteSpace(connectionRef))  throw new ArgumentException("ConnectionRef is required.", nameof(connectionRef));
    }
}
