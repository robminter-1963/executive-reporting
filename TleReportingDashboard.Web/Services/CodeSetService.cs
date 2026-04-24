using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace TleReportingDashboard.Web.Services;

public class CodeSetService : ICodeSetService
{
    private readonly ICompanyConnectionResolver _connectionResolver;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CodeSetService> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public CodeSetService(ICompanyConnectionResolver connectionResolver, IMemoryCache cache, ILogger<CodeSetService> logger)
    {
        _connectionResolver = connectionResolver;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<CodeSetValue>> GetCodeSetValuesAsync(int codeSetId)
    {
        var cacheKey = $"CodeSet_{codeSetId}";
        if (_cache.TryGetValue(cacheKey, out List<CodeSetValue>? cached) && cached is not null)
            return cached;

        string connStr;
        try
        {
            // Resolves the registry-wide default (any active is_default row)
            // rather than a hardcoded company. Code sets are currently read
            // from whichever DB the default connection points at.
            connStr = await _connectionResolver.GetDefaultConnectionStringAsync();
        }
        catch (InvalidOperationException)
        {
            // Unconfigured — CodeSetService silently returns empty so filter
            // dropdowns just have no options rather than the whole page crashing.
            return [];
        }

        var results = new List<CodeSetValue>();

        try
        {
            await using var connection = new SqlConnection(connStr);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CODENUM, CODEDESC FROM EMPOWER.SET_CODESETS WHERE CODEID = @CodeId AND ISACTIVE = 'Y' ORDER BY CODEDESC";
            command.CommandTimeout = 10;
            command.Parameters.Add(new SqlParameter("@CodeId", SqlDbType.Int) { Value = codeSetId });

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString()?.Trim();
                var desc = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                if (!string.IsNullOrEmpty(desc))
                {
                    results.Add(new CodeSetValue(code ?? desc, desc));
                }
            }

            _cache.Set(cacheKey, results, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load codeset values for CodeSetId={CodeSetId}", codeSetId);
        }

        return results;
    }
}
