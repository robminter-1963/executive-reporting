using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public class FieldReferenceService : IFieldReferenceService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly ILogger<FieldReferenceService> _logger;

    public FieldReferenceService(
        IConfiguration configuration,
        ConfigDbCache cache,
        ILogger<FieldReferenceService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _logger = logger;
    }

    public async Task<int> RenameAsync(string oldFieldId, string newFieldId)
    {
        if (string.IsNullOrWhiteSpace(oldFieldId) || string.IsNullOrWhiteSpace(newFieldId))
            return 0;
        if (string.Equals(oldFieldId, newFieldId, StringComparison.Ordinal))
            return 0;

        var updated = 0;
        updated += await RenameInSavedReportsAsync(oldFieldId, newFieldId);
        updated += await RenameInGridTemplatesAsync(oldFieldId, newFieldId);
        if (updated > 0)
        {
            // Stored JSON in saved_reports + grid_templates was rewritten —
            // anything cached off those tables must drop.
            _cache.Invalidate("ReportDbService:Reports:");
            _cache.Invalidate("GridTemplateService:");
            _cache.Invalidate("MasterDashboardService:");
        }
        _logger.LogInformation("Renamed field '{Old}' → '{New}' across {Count} stored rows", oldFieldId, newFieldId, updated);
        return updated;
    }

    private async Task<int> RenameInSavedReportsAsync(string oldId, string newId)
    {
        var updatedRows = 0;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var toUpdate = new List<(Guid Id, string? FieldIds, string? Filters, string? Aggregations, string? ColumnState)>();
        await using (var read = new SqlCommand(
            "SELECT id, field_ids, filters, aggregations, column_state FROM EMPOWER.RPT_saved_reports", conn))
        {
            await using var r = await read.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                toUpdate.Add((
                    r.GetGuid(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        foreach (var row in toUpdate)
        {
            var f = row.FieldIds;
            var flt = row.Filters;
            var agg = row.Aggregations;
            var cs = row.ColumnState;
            var changed = false;
            changed |= FieldIdRewriter.Rewrite(ref f, oldId, newId);
            changed |= FieldIdRewriter.Rewrite(ref flt, oldId, newId);
            changed |= FieldIdRewriter.Rewrite(ref agg, oldId, newId);
            changed |= FieldIdRewriter.Rewrite(ref cs, oldId, newId);
            if (!changed) continue;

            await using var upd = new SqlCommand(
                @"UPDATE EMPOWER.RPT_saved_reports SET
                    field_ids    = @FieldIds,
                    filters      = @Filters,
                    aggregations = @Aggregations,
                    column_state = @ColumnState,
                    updated_at   = SYSUTCDATETIME()
                  WHERE id = @Id", conn);
            upd.Parameters.Add(new SqlParameter("@Id", row.Id));
            upd.Parameters.Add(new SqlParameter("@FieldIds", (object?)f ?? DBNull.Value));
            upd.Parameters.Add(new SqlParameter("@Filters", (object?)flt ?? DBNull.Value));
            upd.Parameters.Add(new SqlParameter("@Aggregations", (object?)agg ?? DBNull.Value));
            upd.Parameters.Add(new SqlParameter("@ColumnState", (object?)cs ?? DBNull.Value));
            updatedRows += await upd.ExecuteNonQueryAsync();
        }

        return updatedRows;
    }

    private async Task<int> RenameInGridTemplatesAsync(string oldId, string newId)
    {
        var updatedRows = 0;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var toUpdate = new List<(Guid Id, string? FieldIds, string? ColumnState)>();
        await using (var read = new SqlCommand(
            "SELECT id, field_ids, column_state FROM EMPOWER.RPT_grid_templates", conn))
        {
            await using var r = await read.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                toUpdate.Add((
                    r.GetGuid(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2)));
            }
        }

        foreach (var row in toUpdate)
        {
            var f = row.FieldIds;
            var cs = row.ColumnState;
            var changed = false;
            changed |= FieldIdRewriter.Rewrite(ref f, oldId, newId);
            changed |= FieldIdRewriter.Rewrite(ref cs, oldId, newId);
            if (!changed) continue;

            await using var upd = new SqlCommand(
                @"UPDATE EMPOWER.RPT_grid_templates SET
                    field_ids    = @FieldIds,
                    column_state = @ColumnState,
                    updated_at   = SYSUTCDATETIME()
                  WHERE id = @Id", conn);
            upd.Parameters.Add(new SqlParameter("@Id", row.Id));
            upd.Parameters.Add(new SqlParameter("@FieldIds", (object?)f ?? DBNull.Value));
            upd.Parameters.Add(new SqlParameter("@ColumnState", (object?)cs ?? DBNull.Value));
            updatedRows += await upd.ExecuteNonQueryAsync();
        }

        return updatedRows;
    }
}

// Walks a JSON blob and swaps every field-id reference from oldId to newId.
// Treats three kinds of references, all common in column_state / filters / field_ids:
//   - string VALUE equal to oldId            (e.g. ["loan_number", ...])
//   - object KEY equal to oldId              (e.g. { "loan_number": {...} })
//   - nested recursively through objects/arrays
internal static class FieldIdRewriter
{
    public static bool Rewrite(ref string? json, string oldId, string newId)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return false; }
        if (node is null) return false;

        if (!RewriteNode(node, oldId, newId)) return false;

        json = node.ToJsonString();
        return true;
    }

    private static bool RewriteNode(JsonNode node, string oldId, string newId)
    {
        var changed = false;

        switch (node)
        {
            case JsonObject obj:
            {
                // Rewrite string values and recurse into children.
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var val = obj[key];
                    if (val is JsonValue jv && jv.TryGetValue<string>(out var sv) && sv == oldId)
                    {
                        obj[key] = newId;
                        changed = true;
                    }
                    else if (val is JsonObject || val is JsonArray)
                    {
                        if (RewriteNode(val!, oldId, newId)) changed = true;
                    }
                }
                // Rename any keys that match oldId (preserve the value).
                foreach (var key in obj.Select(kv => kv.Key).Where(k => k == oldId).ToList())
                {
                    var v = obj[key]?.DeepClone();
                    obj.Remove(key);
                    obj[newId] = v;
                    changed = true;
                }
                break;
            }
            case JsonArray arr:
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var val = arr[i];
                    if (val is JsonValue jv && jv.TryGetValue<string>(out var sv) && sv == oldId)
                    {
                        arr[i] = newId;
                        changed = true;
                    }
                    else if (val is JsonObject || val is JsonArray)
                    {
                        if (RewriteNode(val!, oldId, newId)) changed = true;
                    }
                }
                break;
            }
        }

        return changed;
    }
}
