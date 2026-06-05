using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// SQL Server-backed implementation of IBatchService. Persists batches +
// items + access grants in RPT_report_batches / RPT_report_batch_items /
// RPT_report_batch_access. ExecuteAsync fans out reports through the
// existing IQueryService and packages the results via IExportService's
// multi-sheet writer.
public sealed class BatchService : IBatchService
{
    private readonly string _connectionString;
    private readonly IReportService _reports;
    private readonly IQueryService _queryService;
    private readonly IExportService _exportService;
    private readonly ICompanyRegistry _companies;
    private readonly ILogger<BatchService> _logger;

    public BatchService(
        IConfiguration configuration,
        IReportService reports,
        IQueryService queryService,
        IExportService exportService,
        ICompanyRegistry companies,
        ILogger<BatchService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _reports = reports;
        _queryService = queryService;
        _exportService = exportService;
        _companies = companies;
        _logger = logger;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ── List / Read ──────────────────────────────────────────────────────

    public async Task<List<BatchRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(@"
            SELECT id, name, description, created_at, created_by, updated_at, updated_by
              FROM EMPOWER.RPT_report_batches
             ORDER BY name;", conn);
        return await ReadBatchListAsync(cmd, ct);
    }

    public async Task<List<BatchRecord>> GetForUserAsync(string userEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return new();
        await using var conn = await OpenConnectionAsync(ct);
        // Union: batches owned by the user (created_by) ∪ batches granted
        // to the user (access table). DISTINCT prevents the dupe row when
        // an owner has also granted themselves. ORDER BY name happens
        // after the union so the merged list is alphabetical.
        await using var cmd = new SqlCommand(@"
            SELECT DISTINCT b.id, b.name, b.description, b.created_at, b.created_by, b.updated_at, b.updated_by
              FROM EMPOWER.RPT_report_batches b
              LEFT JOIN EMPOWER.RPT_report_batch_access a ON a.batch_id = b.id
             WHERE b.created_by = @Email OR a.user_email = @Email
             ORDER BY b.name;", conn);
        cmd.Parameters.Add(new SqlParameter("@Email", userEmail));
        return await ReadBatchListAsync(cmd, ct);
    }

    public async Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        BatchRecord? batch;
        await using (var cmd = new SqlCommand(@"
            SELECT id, name, description, created_at, created_by, updated_at, updated_by
              FROM EMPOWER.RPT_report_batches WHERE id = @Id;", conn))
        {
            cmd.Parameters.Add(new SqlParameter("@Id", id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            batch = ReadBatchRow(reader);
        }

        // Hydrate items with display-friendly joins so the editor can
        // render labels without follow-up lookups. LEFT JOIN on report —
        // FK CASCADE means a missing report row shouldn't happen in
        // steady state, but defend anyway in case of mid-flight delete.
        await using (var cmd = new SqlCommand(@"
            SELECT i.id, i.batch_id, i.report_id, i.sort_order, i.sheet_name,
                   r.name AS report_name, r.internal_name AS report_internal_name,
                   r.owner_email AS report_owner_email, r.company_id
              FROM EMPOWER.RPT_report_batch_items i
              LEFT JOIN EMPOWER.RPT_saved_reports r ON r.id = i.report_id
             WHERE i.batch_id = @Id
             ORDER BY i.sort_order, i.id;", conn))
        {
            cmd.Parameters.Add(new SqlParameter("@Id", id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // DisplayLabel convention: prefer internal_name when set so
                // admin-curated taxonomy wins over the user-facing name —
                // disambiguates same-named reports owned by different users
                // both in the editor and in the generated worksheet labels.
                var name = reader.IsDBNull(5) ? null : reader.GetString(5);
                var internalName = reader.IsDBNull(6) ? null : reader.GetString(6);
                var label = string.IsNullOrWhiteSpace(internalName) ? name : internalName;
                batch.Items.Add(new BatchItem
                {
                    Id = reader.GetInt32(0),
                    BatchId = reader.GetGuid(1),
                    ReportId = reader.GetGuid(2),
                    SortOrder = reader.GetInt32(3),
                    SheetName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ReportName = label,
                    ReportOwnerEmail = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CompanyId = reader.IsDBNull(8) ? (Guid?)null : reader.GetGuid(8)
                });
            }
        }

        // Resolve company names in one batch read so the editor's table
        // can group rows by company. Skipped when there are no items.
        if (batch.Items.Count > 0)
        {
            var companyIds = batch.Items
                .Where(i => i.CompanyId.HasValue)
                .Select(i => i.CompanyId!.Value)
                .Distinct()
                .ToList();
            if (companyIds.Count > 0)
            {
                var all = await _companies.GetActiveAsync(ct);
                var byId = all.ToDictionary(c => c.Id);
                foreach (var item in batch.Items)
                {
                    if (item.CompanyId.HasValue && byId.TryGetValue(item.CompanyId.Value, out var company))
                    {
                        item.CompanyName = company.Name;
                        item.CompanyCode = company.Code;
                    }
                }
            }
        }

        await using (var cmd = new SqlCommand(@"
            SELECT id, batch_id, user_email, granted_at, granted_by
              FROM EMPOWER.RPT_report_batch_access
             WHERE batch_id = @Id
             ORDER BY user_email;", conn))
        {
            cmd.Parameters.Add(new SqlParameter("@Id", id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                batch.Access.Add(new BatchAccessGrant
                {
                    Id = reader.GetInt32(0),
                    BatchId = reader.GetGuid(1),
                    UserEmail = reader.GetString(2),
                    GrantedAt = reader.GetDateTime(3),
                    GrantedBy = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
        }

        return batch;
    }

    public async Task<bool> CanRunAsync(Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default)
    {
        if (isAdmin) return true;
        if (string.IsNullOrWhiteSpace(userEmail)) return false;
        await using var conn = await OpenConnectionAsync(ct);
        // Owner OR granted. Single round-trip; either side satisfies.
        await using var cmd = new SqlCommand(@"
            SELECT TOP 1 1
              FROM EMPOWER.RPT_report_batches b
              LEFT JOIN EMPOWER.RPT_report_batch_access a
                ON a.batch_id = b.id AND a.user_email = @Email
             WHERE b.id = @BatchId
               AND (b.created_by = @Email OR a.user_email IS NOT NULL);", conn);
        cmd.Parameters.Add(new SqlParameter("@BatchId", batchId));
        cmd.Parameters.Add(new SqlParameter("@Email", userEmail));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    public async Task<bool> CanEditAsync(Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default)
    {
        if (isAdmin) return true;
        if (string.IsNullOrWhiteSpace(userEmail)) return false;
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(@"
            SELECT TOP 1 1 FROM EMPOWER.RPT_report_batches
             WHERE id = @BatchId AND created_by = @Email;", conn);
        cmd.Parameters.Add(new SqlParameter("@BatchId", batchId));
        cmd.Parameters.Add(new SqlParameter("@Email", userEmail));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    // ── Mutate ───────────────────────────────────────────────────────────

    public async Task<BatchRecord> CreateAsync(BatchRecord batch, string createdBy, CancellationToken ct = default)
    {
        batch.Id = batch.Id == Guid.Empty ? Guid.NewGuid() : batch.Id;
        batch.CreatedAt = DateTime.UtcNow;
        batch.UpdatedAt = batch.CreatedAt;
        batch.CreatedBy = createdBy;
        batch.UpdatedBy = createdBy;

        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_report_batches
                (id, name, description, created_at, created_by, updated_at, updated_by)
             VALUES (@Id, @Name, @Description, @CreatedAt, @CreatedBy, @UpdatedAt, @UpdatedBy);", conn);
        AddBatchInsertParams(cmd, batch);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Batch created: {Id} '{Name}' by {User}", batch.Id, batch.Name, createdBy);
        return batch;
    }

    public async Task<BatchRecord> UpdateAsync(BatchRecord batch, string updatedBy, CancellationToken ct = default)
    {
        batch.UpdatedAt = DateTime.UtcNow;
        batch.UpdatedBy = updatedBy;

        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_report_batches
               SET name = @Name, description = @Description,
                   updated_at = @UpdatedAt, updated_by = @UpdatedBy
             WHERE id = @Id;", conn);
        AddBatchUpdateParams(cmd, batch);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0) throw new KeyNotFoundException($"Batch {batch.Id} not found.");
        _logger.LogInformation("Batch updated: {Id} '{Name}' by {User}", batch.Id, batch.Name, updatedBy);
        return batch;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        // CASCADE on items + access via FK — single DELETE drops everything.
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_report_batches WHERE id = @Id;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", id));
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Batch deleted: {Id}", id);
    }

    public async Task SetItemsAsync(Guid batchId, IReadOnlyList<BatchItem> items, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var del = new SqlCommand(
                "DELETE FROM EMPOWER.RPT_report_batch_items WHERE batch_id = @BatchId;", conn, tx))
            {
                del.Parameters.Add(new SqlParameter("@BatchId", batchId));
                await del.ExecuteNonQueryAsync(ct);
            }
            var sortOrder = 0;
            foreach (var item in items)
            {
                await using var ins = new SqlCommand(@"
                    INSERT INTO EMPOWER.RPT_report_batch_items
                        (batch_id, report_id, sort_order, sheet_name)
                     VALUES (@BatchId, @ReportId, @SortOrder, @SheetName);", conn, tx);
                ins.Parameters.Add(new SqlParameter("@BatchId", batchId));
                ins.Parameters.Add(new SqlParameter("@ReportId", item.ReportId));
                ins.Parameters.Add(new SqlParameter("@SortOrder", sortOrder++));
                ins.Parameters.Add(new SqlParameter("@SheetName",
                    string.IsNullOrWhiteSpace(item.SheetName) ? (object)DBNull.Value : item.SheetName));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task GrantAccessAsync(Guid batchId, string userEmail, string grantedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return;
        await using var conn = await OpenConnectionAsync(ct);
        // Idempotent — UNIQUE(batch_id, user_email) constraint catches dupes;
        // we use MERGE so a re-grant is a silent no-op (no audit churn).
        await using var cmd = new SqlCommand(@"
            MERGE EMPOWER.RPT_report_batch_access AS t
            USING (SELECT @BatchId AS batch_id, @Email AS user_email) AS src
               ON t.batch_id = src.batch_id AND t.user_email = src.user_email
            WHEN NOT MATCHED THEN
                INSERT (batch_id, user_email, granted_by)
                VALUES (src.batch_id, src.user_email, @GrantedBy);", conn);
        cmd.Parameters.Add(new SqlParameter("@BatchId", batchId));
        cmd.Parameters.Add(new SqlParameter("@Email", userEmail));
        cmd.Parameters.Add(new SqlParameter("@GrantedBy", (object?)grantedBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeAccessAsync(Guid batchId, string userEmail, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(@"
            DELETE FROM EMPOWER.RPT_report_batch_access
             WHERE batch_id = @BatchId AND user_email = @Email;", conn);
        cmd.Parameters.Add(new SqlParameter("@BatchId", batchId));
        cmd.Parameters.Add(new SqlParameter("@Email", userEmail));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Execute ──────────────────────────────────────────────────────────

    public async Task<(byte[] FileBytes, string FileName)> ExecuteAsync(
        Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default)
    {
        if (!await CanRunAsync(batchId, userEmail, isAdmin, ct))
            throw new UnauthorizedAccessException("You don't have permission to run this batch.");

        var batch = await GetByIdAsync(batchId, ct)
            ?? throw new KeyNotFoundException($"Batch {batchId} not found.");

        var sheets = new List<MultiSheetExcelInput>(batch.Items.Count);

        foreach (var item in batch.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
        {
            // Base label = the admin's per-item override or the report's
            // name. We then prepend the company code so a cross-company
            // workbook reads as "tle - Loans By Status" /
            // "abc - Loans By Status" instead of leaving the reader to
            // figure out which company a same-named sheet belongs to.
            // ExportService.ClampSheetName handles the 31-char Excel
            // limit + invalid-char stripping + de-dup across sheets.
            var baseLabel = !string.IsNullOrWhiteSpace(item.SheetName)
                ? item.SheetName!
                : item.ReportName ?? $"Report {item.ReportId:N}";
            var sheetName = string.IsNullOrWhiteSpace(item.CompanyCode)
                ? baseLabel
                : $"{item.CompanyCode} - {baseLabel}";

            QueryResponse data;
            try
            {
                var report = await _reports.GetReportByIdAsync(item.ReportId)
                    ?? throw new InvalidOperationException("Report not found.");

                var request = QueryRequestFactory.FromSavedReport(report, QueryRequest.MaxPageSize);
                data = await _queryService.ExecuteQueryAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Batch {BatchId} item report {ReportId} failed; emitting error sheet.",
                    batchId, item.ReportId);
                data = BuildErrorSheet(item, ex);
            }
            sheets.Add(new MultiSheetExcelInput(sheetName, data));
        }

        var bytes = _exportService.ExportToMultiSheetExcel(sheets);
        var safeName = SafeFileName(batch.Name);
        var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";

        _logger.LogInformation(
            "Batch executed: {Id} '{Name}' by {User} — {Sheets} sheets, {Bytes:N0} bytes",
            batch.Id, batch.Name, userEmail, sheets.Count, bytes.Length);

        return (bytes, fileName);
    }

    // Replaces a failed report's sheet with a single-cell error message so
    // the recipient can see WHY a sheet is empty rather than getting a
    // silent blank. Keeps the rest of the batch viable — one broken
    // report doesn't ruin the whole download.
    private static QueryResponse BuildErrorSheet(BatchItem item, Exception ex)
    {
        var label = item.ReportName ?? item.ReportId.ToString();
        return new QueryResponse
        {
            Columns = new List<ColumnMeta>
            {
                new() { FieldId = "error", Label = "Error", DataType = "text" }
            },
            Rows = new List<Dictionary<string, object?>>
            {
                new() { ["error"] = $"Couldn't run \"{label}\": {ex.Message}" }
            },
            TotalCount = 1
        };
    }

    // ── Read helpers ─────────────────────────────────────────────────────

    private static async Task<List<BatchRecord>> ReadBatchListAsync(SqlCommand cmd, CancellationToken ct)
    {
        var list = new List<BatchRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadBatchRow(reader));
        return list;
    }

    private static BatchRecord ReadBatchRow(System.Data.Common.DbDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        CreatedAt = r.GetDateTime(3),
        CreatedBy = r.IsDBNull(4) ? null : r.GetString(4),
        UpdatedAt = r.GetDateTime(5),
        UpdatedBy = r.IsDBNull(6) ? null : r.GetString(6)
    };

    // Insert-time parameter set. Binds every column the INSERT writes.
    // DateTime values are explicitly typed as DateTime2 because SqlClient's
    // default DateTime → SqlDbType.DateTime inference has a 1753 minimum,
    // and a default(DateTime) value (0001-01-01) would overflow on every
    // call site that constructs a BatchRecord without explicitly setting
    // the timestamps (e.g. the editor's update payload). The actual columns
    // in RPT_report_batches are DATETIME2 — match the type explicitly here
    // so values round-trip cleanly regardless of caller hygiene.
    private static void AddBatchInsertParams(SqlCommand cmd, BatchRecord batch)
    {
        cmd.Parameters.Add(new SqlParameter("@Id", batch.Id));
        cmd.Parameters.Add(new SqlParameter("@Name", batch.Name));
        cmd.Parameters.Add(new SqlParameter("@Description",
            string.IsNullOrWhiteSpace(batch.Description) ? (object)DBNull.Value : batch.Description));
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", System.Data.SqlDbType.DateTime2) { Value = batch.CreatedAt });
        cmd.Parameters.Add(new SqlParameter("@CreatedBy", (object?)batch.CreatedBy ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@UpdatedAt", System.Data.SqlDbType.DateTime2) { Value = batch.UpdatedAt });
        cmd.Parameters.Add(new SqlParameter("@UpdatedBy", (object?)batch.UpdatedBy ?? DBNull.Value));
    }

    // Update-time parameter set. Binds ONLY the parameters the UPDATE
    // statement references — avoids the SqlClient overflow on @CreatedAt
    // when callers send a BatchRecord without re-populating it (the column
    // is preserved server-side; clients have no business resetting it).
    private static void AddBatchUpdateParams(SqlCommand cmd, BatchRecord batch)
    {
        cmd.Parameters.Add(new SqlParameter("@Id", batch.Id));
        cmd.Parameters.Add(new SqlParameter("@Name", batch.Name));
        cmd.Parameters.Add(new SqlParameter("@Description",
            string.IsNullOrWhiteSpace(batch.Description) ? (object)DBNull.Value : batch.Description));
        cmd.Parameters.Add(new SqlParameter("@UpdatedAt", System.Data.SqlDbType.DateTime2) { Value = batch.UpdatedAt });
        cmd.Parameters.Add(new SqlParameter("@UpdatedBy", (object?)batch.UpdatedBy ?? DBNull.Value));
    }

    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "batch";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "batch" : clean;
    }
}
