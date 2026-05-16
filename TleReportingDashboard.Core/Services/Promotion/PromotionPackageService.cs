using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.Promotion;

public sealed class PromotionPackageService : IPromotionPackageService
{
    private readonly ISchemaConfigStore _schemaStore;
    private readonly ICompanyConnectionAdminService _connections;
    private readonly ICompanyRegistry _companies;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PromotionPackageService> _logger;

    public PromotionPackageService(
        ISchemaConfigStore schemaStore,
        ICompanyConnectionAdminService connections,
        ICompanyRegistry companies,
        IConfiguration configuration,
        ILogger<PromotionPackageService> logger)
    {
        _schemaStore = schemaStore;
        _connections = connections;
        _companies = companies;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<byte[]> ExportAsync(
        IReadOnlyList<Guid> schemaConfigConnectionIds,
        string? exportedBy,
        string? notes,
        CancellationToken ct = default)
    {
        var pkg = new PromotionPackage
        {
            SourceEnvironment = _configuration["Environment:Label"] ?? "UNKNOWN",
            ExportedAtUtc = DateTime.UtcNow,
            ExportedBy = exportedBy,
            Notes = notes
        };

        // Resolve company labels up front so the package travels with
        // human-readable context — the importer's GUIDs won't match
        // staging's, so name-based mapping is what the admin uses to
        // pick the target.
        var allCompanies = await _companies.GetActiveAsync();
        var companiesById = allCompanies.ToDictionary(c => c.Id, c => c.Name);

        foreach (var connId in schemaConfigConnectionIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            // The connection's owning company drives the human label;
            // GetByCompanyAsync is the only listing API on the admin
            // service, so we have to scan to find this one.
            CompanyConnectionRecord? conn = null;
            foreach (var (companyId, _) in companiesById)
            {
                var inCompany = await _connections.GetByCompanyAsync(companyId, ct);
                conn = inCompany.FirstOrDefault(c => c.Id == connId);
                if (conn is not null) break;
            }
            if (conn is null)
            {
                _logger.LogWarning("Export skipped unknown connection id {ConnId}", connId);
                continue;
            }

            var schema = _schemaStore.GetForConnection(connId);
            if (schema.Fields.Count == 0 && schema.Joins.Count == 0)
            {
                _logger.LogInformation(
                    "Export skipped empty schema for connection {ConnId} ({Name})",
                    connId, conn.Name);
                continue;
            }

            pkg.SchemaConfigs.Add(new PromotionPackage.SchemaConfigEntry
            {
                SourceConnectionName = conn.Name,
                SourceCompanyName = companiesById.GetValueOrDefault(conn.CompanyId) ?? "(unknown)",
                Schema = schema
            });
        }

        var json = JsonSerializer.Serialize(pkg, AppJson.Indented);
        _logger.LogInformation(
            "Promotion package exported: {Entries} schema entries from {Env} by {User}",
            pkg.SchemaConfigs.Count, pkg.SourceEnvironment, exportedBy ?? "unknown");
        return Encoding.UTF8.GetBytes(json);
    }

    public PromotionPackage Parse(byte[] packageBytes)
    {
        if (packageBytes is null || packageBytes.Length == 0)
            throw new InvalidOperationException("Package is empty.");

        PromotionPackage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PromotionPackage>(packageBytes, AppJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Package isn't valid JSON: {ex.Message}", ex);
        }
        if (parsed is null)
            throw new InvalidOperationException("Package deserialized to null.");

        if (parsed.PackageVersion != PromotionPackage.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Package version {parsed.PackageVersion} isn't supported by this build " +
                $"(expected {PromotionPackage.CurrentVersion}). Update the importing app or re-export from a matching staging build.");
        }

        // Source-environment allowlist. Empty config = accept anything.
        // Production's appsettings should pin this to "STAGING" so a
        // bundle exported from prod (sneakernet'd back) can't reapply.
        var allowedCsv = _configuration["Promotion:AllowedSourceEnvironments"];
        if (!string.IsNullOrWhiteSpace(allowedCsv))
        {
            var allowed = allowedCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!allowed.Contains(parsed.SourceEnvironment))
            {
                throw new InvalidOperationException(
                    $"Package source environment '{parsed.SourceEnvironment}' isn't in the allowed list for this instance ({allowedCsv}).");
            }
        }

        return parsed;
    }

    public async Task<ImportResult> ImportSchemaConfigAsync(
        PromotionPackage.SchemaConfigEntry entry,
        Guid targetConnectionId,
        string? importedBy,
        CancellationToken ct = default)
    {
        if (entry?.Schema is null)
            return new ImportResult(false, "Entry has no schema payload.");

        // Round-trip through JSON so the saved instance doesn't share
        // memory with the package's copy — same defensive copy the
        // schema store's Clone path uses.
        var json = JsonSerializer.Serialize(entry.Schema, AppJson.Indented);
        var copy = JsonSerializer.Deserialize<SchemaConfig>(json, AppJson.Indented) ?? new SchemaConfig();

        try
        {
            await _schemaStore.SaveAsync(
                copy,
                targetConnectionId,
                importedBy is null
                    ? $"promotion-import:{entry.SourceConnectionName}"
                    : $"promotion-import:{entry.SourceConnectionName} (by {importedBy})");
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Schema config import failed for target {TargetId}", targetConnectionId);
            return new ImportResult(false, $"Database error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema config import failed for target {TargetId}", targetConnectionId);
            return new ImportResult(false, ex.Message);
        }

        return new ImportResult(true,
            $"Imported schema with {copy.Fields.Count} fields, {copy.Joins.Count} joins.");
    }
}
