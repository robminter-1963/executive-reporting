using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// SQL-backed audit logger. Writes RPT_audit_log via parameterized INSERT;
// reads back through QueryAsync for the Admin review UI.
//
// Failure tolerance: a broken write must NEVER break the business action
// that triggered it (granting an admin should succeed even if the audit
// table is gone). All exceptions during writes are caught + logged via
// ILogger; the caller sees a successful Task. Reads propagate normally —
// a broken read is the review UI's problem, not a business path.
//
// Concurrency / pooling: the connection is short-lived per call (standard
// ADO.NET pattern) so the underlying TCP socket is pooled and reused.
// One INSERT per LogAsync; no batching today. If you log thousands of
// events per second this would need work, but admin actions land at
// human cadence so a per-call round-trip is fine.
public sealed class AuditLogger : IAuditLogger
{
    private readonly string _connStr;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ILogger<AuditLogger> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // Lets the review UI render diffs of nullable fields cleanly —
        // null shows up as "null" in the JSON instead of omitting the key.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public AuditLogger(
        IConfiguration configuration,
        ICurrentUserAccessor currentUser,
        ILogger<AuditLogger> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for AuditLogger.");
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task LogAsync(
        string? actorEmail,
        string action,
        string resourceType,
        string? resourceId,
        string? resourceLabel,
        object? before = null,
        object? after = null,
        string? notes = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        try
        {
            // Caller didn't pass an explicit actor — fall back to whoever
            // is signed in for the current HTTP request. Background jobs
            // (worker, scheduled tasks) call with actorEmail=null and we
            // record "(system)" rather than a misleading user.
            var actor = string.IsNullOrWhiteSpace(actorEmail)
                ? _currentUser.Email
                : actorEmail;
            var userId = _currentUser.UserId;

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
                INSERT INTO EMPOWER.RPT_audit_log
                    (actor_email, actor_user_id, action, resource_type, resource_id,
                     resource_label, before_json, after_json, correlation_id, notes)
                VALUES
                    (@actor_email, @actor_user_id, @action, @resource_type, @resource_id,
                     @resource_label, @before_json, @after_json, @correlation_id, @notes);",
                conn);
            cmd.Parameters.Add(new SqlParameter("@actor_email", (object?)actor ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@actor_user_id", (object?)userId ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@action", action));
            cmd.Parameters.Add(new SqlParameter("@resource_type", resourceType));
            cmd.Parameters.Add(new SqlParameter("@resource_id", (object?)resourceId ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@resource_label", Trim(resourceLabel, 500)));
            cmd.Parameters.Add(new SqlParameter("@before_json", (object?)Serialize(before) ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@after_json", (object?)Serialize(after) ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@correlation_id", (object?)correlationId ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@notes", Trim(notes, 500)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // Never propagate. The originating business action must
            // succeed even if we couldn't write the audit row — the
            // server log still has enough detail to reconstruct what
            // happened. A SOC-2 auditor would flag persistent failures
            // here; the spike-counter / alert is the operations team's
            // job, not this method's.
            _logger.LogError(ex,
                "Audit log write failed: action={Action} resource={ResourceType}/{ResourceId} actor={Actor}",
                action, resourceType, resourceId, actorEmail ?? "(unknown)");
        }
    }

    public async Task<List<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder(@"
            SELECT TOP (@take) id, occurred_at, actor_email, actor_user_id,
                   action, resource_type, resource_id, resource_label,
                   before_json, after_json, correlation_id, notes
              FROM EMPOWER.RPT_audit_log
             WHERE 1 = 1");

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // Cap the page size at 1000 regardless of caller input so a bad
        // request can't pull the whole table into memory.
        var take = Math.Clamp(query.Take, 1, 1000);
        cmd.Parameters.Add(new SqlParameter("@take", take));

        if (query.FromUtc is DateTime from)
        {
            sql.Append(" AND occurred_at >= @from");
            cmd.Parameters.Add(new SqlParameter("@from", from));
        }
        if (query.ToUtc is DateTime to)
        {
            sql.Append(" AND occurred_at < @to");
            cmd.Parameters.Add(new SqlParameter("@to", to));
        }
        if (!string.IsNullOrWhiteSpace(query.ActorEmail))
        {
            sql.Append(" AND actor_email = @actor");
            cmd.Parameters.Add(new SqlParameter("@actor", query.ActorEmail));
        }
        if (!string.IsNullOrWhiteSpace(query.ResourceType))
        {
            sql.Append(" AND resource_type = @rtype");
            cmd.Parameters.Add(new SqlParameter("@rtype", query.ResourceType));
        }
        if (!string.IsNullOrWhiteSpace(query.ResourceId))
        {
            sql.Append(" AND resource_id = @rid");
            cmd.Parameters.Add(new SqlParameter("@rid", query.ResourceId));
        }
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            sql.Append(" AND action = @action");
            cmd.Parameters.Add(new SqlParameter("@action", query.Action));
        }
        if (query.BeforeId is long beforeId)
        {
            sql.Append(" AND id < @beforeid");
            cmd.Parameters.Add(new SqlParameter("@beforeid", beforeId));
        }
        // Newest first — index IX_audit_log_occurred_at DESC.
        sql.Append(" ORDER BY id DESC;");
        cmd.CommandText = sql.ToString();

        var results = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AuditEntry(
                Id:             reader.GetInt64(0),
                OccurredAtUtc:  reader.GetDateTime(1),
                ActorEmail:     reader.IsDBNull(2) ? null : reader.GetString(2),
                ActorUserId:    reader.IsDBNull(3) ? null : reader.GetString(3),
                Action:         reader.GetString(4),
                ResourceType:   reader.GetString(5),
                ResourceId:     reader.IsDBNull(6) ? null : reader.GetString(6),
                ResourceLabel:  reader.IsDBNull(7) ? null : reader.GetString(7),
                BeforeJson:     reader.IsDBNull(8) ? null : reader.GetString(8),
                AfterJson:      reader.IsDBNull(9) ? null : reader.GetString(9),
                CorrelationId:  reader.IsDBNull(10) ? null : reader.GetString(10),
                Notes:          reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return results;
    }

    public async Task<List<string>> GetDistinctActorsAsync(CancellationToken ct = default)
    {
        // Bounded scan: the IX_audit_log_actor index makes DISTINCT cheap.
        // No date cap — admin rosters are tiny so the distinct-actor list
        // stays small even years out.
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            SELECT DISTINCT actor_email
              FROM EMPOWER.RPT_audit_log
             WHERE actor_email IS NOT NULL
             ORDER BY actor_email;", conn);
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        return result;
    }

    private static string? Serialize(object? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.Serialize(value, JsonOptions); }
        catch { return value.ToString(); }
    }

    private static object Trim(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return DBNull.Value;
        return value.Length <= maxLen ? value : value[..maxLen];
    }
}
