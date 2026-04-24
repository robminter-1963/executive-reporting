using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// DB-backed company registry. Caches the active company list in-memory;
// admin screens that mutate RPT_companies should call Invalidate() so the
// next read picks up the change. Read volume is high (dropdowns), write
// volume is near-zero.
public sealed class CompanyRegistry : ICompanyRegistry
{
    private readonly string _connStr;
    private readonly ILogger<CompanyRegistry> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private List<CompanySummary>? _cache;

    public CompanyRegistry(IConfiguration configuration, ILogger<CompanyRegistry> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for CompanyRegistry.");
        _logger = logger;
    }

    public async Task<List<CompanySummary>> GetActiveAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;
        await _mutex.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;
            _cache = await LoadAsync(ct);
            return _cache;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<CompanySummary?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var list = await GetActiveAsync(ct);
        return list.FirstOrDefault(c => c.Id == id);
    }

    public async Task<CompanySummary?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var list = await GetActiveAsync(ct);
        return list.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public void Invalidate() => _cache = null;

    private async Task<List<CompanySummary>> LoadAsync(CancellationToken ct)
    {
        var rows = new List<CompanySummary>();
        try
        {
            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);
            // display_order + logo columns come along on the cached read so
            // the picker page can render every card without a second query.
            // Logo bytes are typically small (< 500 KB) so the cache cost is
            // bounded by the company count.
            await using var cmd = new SqlCommand(@"
                SELECT id, code, name, is_active, display_order, logo, logo_content_type, website_url
                  FROM EMPOWER.RPT_companies
                 WHERE is_active = 1
                 ORDER BY display_order, name;", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new CompanySummary(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetBoolean(3))
                {
                    DisplayOrder = reader.GetInt32(4),
                    Logo = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                    LogoContentType = reader.IsDBNull(6) ? null : reader.GetString(6),
                    WebsiteUrl = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load RPT_companies — returning empty list.");
        }
        return rows;
    }
}
