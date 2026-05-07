using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class LibrarySectionService : ILibrarySectionService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly ILogger<LibrarySectionService> _logger;

    public LibrarySectionService(
        IConfiguration configuration,
        ConfigDbCache cache,
        ILogger<LibrarySectionService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _logger = logger;
    }

    public Task<List<LibrarySection>> GetSectionsAsync(Guid companyId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("LibrarySectionService", "ByCompany", companyId),
            async () =>
            {
                var rows = new List<LibrarySection>();
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(@"
                    SELECT id, company_id, name, sort_order, is_active, created_at
                      FROM EMPOWER.RPT_library_sections
                     WHERE company_id = @CompanyId AND is_active = 1
                     ORDER BY sort_order, name;", conn);
                cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
                try
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        rows.Add(new LibrarySection
                        {
                            Id = reader.GetGuid(0),
                            CompanyId = reader.GetGuid(1),
                            Name = reader.GetString(2),
                            SortOrder = reader.GetInt32(3),
                            IsActive = reader.GetBoolean(4),
                            CreatedAt = reader.GetDateTime(5)
                        });
                    }
                }
                catch (SqlException ex) when (ex.Number == 208) // Invalid object name
                {
                    // Migration not applied on this DB yet — return empty so
                    // the Library / Builder fall back to the catch-all bucket.
                    _logger.LogDebug(ex, "RPT_library_sections not present yet — returning empty list.");
                }
                return rows;
            });

    public async Task<LibrarySection> CreateSectionAsync(Guid companyId, string name, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Section name is required.", nameof(name));
        var section = new LibrarySection
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Name = name.Trim(),
            SortOrder = sortOrder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_library_sections
                (id, company_id, name, sort_order, is_active, created_at)
            VALUES (@Id, @CompanyId, @Name, @SortOrder, 1, @CreatedAt);", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", section.Id));
        cmd.Parameters.Add(new SqlParameter("@CompanyId", section.CompanyId));
        cmd.Parameters.Add(new SqlParameter("@Name", section.Name));
        cmd.Parameters.Add(new SqlParameter("@SortOrder", section.SortOrder));
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", section.CreatedAt));
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            // Unique-index collision (duplicate active name in same company).
            // Re-throw as InvalidOperationException so the dialog can show
            // a clear message instead of a SQL error.
            throw new InvalidOperationException(
                $"A section named \"{section.Name}\" already exists for this company.");
        }
        _cache.Invalidate("LibrarySectionService:");
        _logger.LogInformation("Library section created: {Id} {Name} for company {CompanyId}",
            section.Id, section.Name, section.CompanyId);
        return section;
    }

    public async Task RenameSectionAsync(Guid sectionId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Section name is required.", nameof(newName));
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_library_sections
               SET name = @Name
             WHERE id = @Id;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", sectionId));
        cmd.Parameters.Add(new SqlParameter("@Name", newName.Trim()));
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            throw new InvalidOperationException(
                $"A section named \"{newName.Trim()}\" already exists for this company.");
        }
        _cache.Invalidate("LibrarySectionService:");
    }

    public async Task ReorderSectionsAsync(Guid companyId, IList<Guid> orderedIds)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            for (var i = 0; i < orderedIds.Count; i++)
            {
                await using var cmd = new SqlCommand(@"
                    UPDATE EMPOWER.RPT_library_sections
                       SET sort_order = @Order
                     WHERE id = @Id AND company_id = @CompanyId;", conn, (SqlTransaction)tx);
                cmd.Parameters.Add(new SqlParameter("@Order", i));
                cmd.Parameters.Add(new SqlParameter("@Id", orderedIds[i]));
                cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        _cache.Invalidate("LibrarySectionService:");
    }

    public async Task DeleteSectionAsync(Guid sectionId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // Soft-delete + null out the FK on every report that pointed here.
        // Two-statement transaction so the section flip and the report
        // unlinks land together.
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using (var unlinkCmd = new SqlCommand(@"
                UPDATE EMPOWER.RPT_saved_reports
                   SET library_section_id = NULL
                 WHERE library_section_id = @Id;", conn, (SqlTransaction)tx))
            {
                unlinkCmd.Parameters.Add(new SqlParameter("@Id", sectionId));
                await unlinkCmd.ExecuteNonQueryAsync();
            }
            await using (var deactivateCmd = new SqlCommand(@"
                UPDATE EMPOWER.RPT_library_sections
                   SET is_active = 0
                 WHERE id = @Id;", conn, (SqlTransaction)tx))
            {
                deactivateCmd.Parameters.Add(new SqlParameter("@Id", sectionId));
                await deactivateCmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        _cache.Invalidate("LibrarySectionService:");
        _cache.Invalidate("ReportDbService:Reports:");
    }
}
