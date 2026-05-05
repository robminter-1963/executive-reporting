using System.Data;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class GridTemplateService : IGridTemplateService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;

    public GridTemplateService(
        IConfiguration configuration,
        ConfigDbCache cache,
        EditorModeState editorMode)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _editorMode = editorMode;
    }

    public Task<List<GridTemplate>> GetTemplatesAsync(string userId, Guid? connectionId = null) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("GridTemplateService", "List", userId, connectionId),
            async () =>
            {
                var templates = new List<GridTemplate>();
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                // When connectionId is provided, the caller is picking a template to
                // apply to a specific report — only return templates built against
                // that connection's schema. When omitted, return all (used by the
                // admin list page).
                var sql = @"
                    SELECT id, name, description, owner_id, owner_email, is_shared, field_ids, column_state, connection_id, created_at, updated_at
                    FROM EMPOWER.RPT_grid_templates
                    WHERE (owner_id = @UserId OR is_shared = 1)";
                if (connectionId.HasValue)
                    sql += " AND connection_id = @ConnectionId";
                sql += " ORDER BY name";

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add(new SqlParameter("@UserId", userId));
                if (connectionId.HasValue)
                    cmd.Parameters.Add(new SqlParameter("@ConnectionId", connectionId.Value));

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    templates.Add(ReadTemplate(reader));
                }
                return templates;
            },
            bypass: _editorMode.IsActive);

    public Task<GridTemplate?> GetTemplateAsync(Guid id) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("GridTemplateService", "ById", id),
            async () =>
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT id, name, description, owner_id, owner_email, is_shared, field_ids, column_state, connection_id, created_at, updated_at FROM EMPOWER.RPT_grid_templates WHERE id = @Id", conn);
                cmd.Parameters.Add(new SqlParameter("@Id", id));

                await using var reader = await cmd.ExecuteReaderAsync();
                return await reader.ReadAsync() ? ReadTemplate(reader) : null;
            },
            bypass: _editorMode.IsActive);

    public async Task<GridTemplate> SaveTemplateAsync(GridTemplate template)
    {
        template.Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id;
        template.CreatedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_grid_templates
            (id, name, description, owner_id, owner_email, is_shared, field_ids, column_state, connection_id, created_at, updated_at)
            VALUES (@Id, @Name, @Desc, @OwnerId, @OwnerEmail, @IsShared, @FieldIds, @ColumnState, @ConnectionId, @CreatedAt, @UpdatedAt)", conn);
        AddParams(cmd, template);
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("GridTemplateService:");
        return template;
    }

    public async Task UpdateTemplateAsync(GridTemplate template)
    {
        template.UpdatedAt = DateTime.UtcNow;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_grid_templates
            SET name = @Name, description = @Desc, is_shared = @IsShared,
                field_ids = @FieldIds, column_state = @ColumnState, updated_at = @UpdatedAt
            WHERE id = @Id AND owner_id = @OwnerId", conn);
        AddParams(cmd, template);
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("GridTemplateService:");
    }

    public async Task DeleteTemplateAsync(Guid id, string userId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_grid_templates WHERE id = @Id AND owner_id = @UserId", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", id));
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("GridTemplateService:");
    }

    public async Task<ResolvedTemplate?> ResolveTemplateAsync(Guid? gridTemplateId)
    {
        if (!gridTemplateId.HasValue) return null;
        var template = await GetTemplateAsync(gridTemplateId.Value);
        if (template is null) return null;

        var fieldIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(template.FieldIds) ?? new();
        List<string>? columnOrder = null;
        List<string>? hiddenColumns = null;
        string? sortField = null;
        string? sortDir = null;

        if (!string.IsNullOrWhiteSpace(template.ColumnState))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(template.ColumnState);
                if (doc.RootElement.TryGetProperty("TableColumnOrder", out var coEl))
                    columnOrder = System.Text.Json.JsonSerializer.Deserialize<List<string>>(coEl.GetRawText());
                if (doc.RootElement.TryGetProperty("HiddenColumns", out var hcEl))
                    hiddenColumns = System.Text.Json.JsonSerializer.Deserialize<List<string>>(hcEl.GetRawText());
                if (doc.RootElement.TryGetProperty("TableSortField", out var sfEl))
                    sortField = sfEl.GetString();
                if (doc.RootElement.TryGetProperty("TableSortDirection", out var sdEl))
                    sortDir = sdEl.GetString();
            }
            catch { }
        }

        return new ResolvedTemplate(fieldIds, columnOrder, hiddenColumns, sortField, sortDir);
    }

    /// <summary>
    /// Merges a report's saved column order with the template's current field set.
    /// Preserves per-report ordering for fields still in the template, drops fields
    /// removed from the template, and appends any newly-added template fields at the
    /// end so they become visible in the report.
    /// </summary>
    public static List<string> MergeTemplateAndReportOrder(IList<string> templateFieldIds, IList<string>? reportOrder)
    {
        if (reportOrder is null || reportOrder.Count == 0)
            return new List<string>(templateFieldIds);

        var fieldSet = new HashSet<string>(templateFieldIds, StringComparer.OrdinalIgnoreCase);
        var merged = reportOrder.Where(id => fieldSet.Contains(id)).ToList();
        var inMerged = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
        foreach (var id in templateFieldIds)
            if (!inMerged.Contains(id))
                merged.Add(id);
        return merged;
    }

    /// <summary>
    /// Filters a report's saved hidden-column list down to fields that still exist
    /// in the template, so removed template fields don't leave dangling hidden state.
    /// New template fields are not auto-hidden — they appear by default.
    /// </summary>
    public static List<string>? FilterHiddenToTemplate(IList<string> templateFieldIds, IList<string>? hidden)
    {
        if (hidden is null || hidden.Count == 0) return null;
        var fieldSet = new HashSet<string>(templateFieldIds, StringComparer.OrdinalIgnoreCase);
        var filtered = hidden.Where(id => fieldSet.Contains(id)).ToList();
        return filtered.Count > 0 ? filtered : null;
    }

    /// <summary>
    /// Weaves per-report calc-column keys into a template's field-only
    /// column order. Templates don't own calcs (they're per-report
    /// derivations), so when a template is applied or a templated report
    /// loads, the saved combined order's calc positions need to be
    /// preserved in the resulting merged order. Each calc is inserted at
    /// the position of the next template field that follows it in the
    /// saved order — preserving "calc immediately before / after field X"
    /// relationships. Calcs not found in the saved order get appended at
    /// the end.
    /// </summary>
    public static List<string> WeaveCalcsIntoTemplateOrder(
        IList<string> templateOrder,
        IList<string>? savedOrder,
        IEnumerable<string>? calcKeysSource)
    {
        var merged = new List<string>(templateOrder);
        var calcKeys = (calcKeysSource ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (calcKeys.Count == 0) return merged;
        var saved = savedOrder ?? Array.Empty<string>();
        foreach (var calcKey in calcKeys)
        {
            if (merged.Contains(calcKey, StringComparer.OrdinalIgnoreCase)) continue;
            var savedIdx = -1;
            for (int i = 0; i < saved.Count; i++)
            {
                if (string.Equals(saved[i], calcKey, StringComparison.OrdinalIgnoreCase))
                {
                    savedIdx = i;
                    break;
                }
            }
            int insertAt = merged.Count;
            if (savedIdx >= 0)
            {
                for (int i = savedIdx + 1; i < saved.Count; i++)
                {
                    var idxInMerged = merged.FindIndex(s =>
                        string.Equals(s, saved[i], StringComparison.OrdinalIgnoreCase));
                    if (idxInMerged >= 0)
                    {
                        insertAt = idxInMerged;
                        break;
                    }
                }
            }
            merged.Insert(insertAt, calcKey);
        }
        return merged;
    }

    private static GridTemplate ReadTemplate(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        OwnerId = reader.GetString(3),
        OwnerEmail = reader.GetString(4),
        IsShared = reader.GetBoolean(5),
        FieldIds = reader.GetString(6),
        ColumnState = reader.IsDBNull(7) ? null : reader.GetString(7),
        ConnectionId = reader.IsDBNull(8) ? null : reader.GetGuid(8),
        CreatedAt = reader.GetDateTime(9),
        UpdatedAt = reader.GetDateTime(10)
    };

    private static void AddParams(SqlCommand cmd, GridTemplate t)
    {
        cmd.Parameters.Add(new SqlParameter("@Id", t.Id));
        cmd.Parameters.Add(new SqlParameter("@Name", t.Name));
        cmd.Parameters.Add(new SqlParameter("@Desc", (object?)t.Description ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@OwnerId", t.OwnerId));
        cmd.Parameters.Add(new SqlParameter("@OwnerEmail", t.OwnerEmail));
        cmd.Parameters.Add(new SqlParameter("@IsShared", t.IsShared));
        cmd.Parameters.Add(new SqlParameter("@FieldIds", t.FieldIds));
        cmd.Parameters.Add(new SqlParameter("@ColumnState", (object?)t.ColumnState ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ConnectionId", (object?)t.ConnectionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", t.CreatedAt));
        cmd.Parameters.Add(new SqlParameter("@UpdatedAt", t.UpdatedAt));
    }
}
