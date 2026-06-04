using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// DB-backed admin service. Reads RPT_admins on first access and caches the
// full list — admin rosters are tiny and rarely change, so in-memory lookup
// is both fast and keeps the sync IsAdmin surface that existing callers
// already rely on.
//
// Bootstrap: on first load, any email in appsettings Admins.Emails that
// isn't already in RPT_admins gets inserted as a 'global' admin. This keeps
// the deploy story backwards-compatible while the table fills in.
public class AdminService : IAdminService
{
    private readonly string _connStr;
    private readonly IOptionsMonitor<AdminOptions> _options;
    private readonly IAuditLogger _audit;
    private readonly ILogger<AdminService> _logger;
    private readonly object _lock = new();

    private List<AdminEntry>? _cache;

    public AdminService(
        IConfiguration configuration,
        IOptionsMonitor<AdminOptions> options,
        IAuditLogger audit,
        ILogger<AdminService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for AdminService.");
        _options = options;
        _audit = audit;
        _logger = logger;
    }

    // ── Public API ──────────────────────────────────────────────────────

    public bool IsAdmin(string? userEmail) => IsGlobalAdmin(userEmail);

    public bool IsGlobalAdmin(string? userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return false;
        return GetCache().Any(a =>
            a.Scope == AdminScope.Global &&
            string.Equals(a.Email, userEmail, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsCompanyAdmin(string? userEmail, Guid companyId)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return false;
        var cache = GetCache();
        // Global admin wins everywhere — no per-company row needed.
        if (cache.Any(a => a.Scope == AdminScope.Global &&
                           string.Equals(a.Email, userEmail, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return cache.Any(a =>
            a.Scope == AdminScope.Company &&
            a.CompanyId == companyId &&
            string.Equals(a.Email, userEmail, StringComparison.OrdinalIgnoreCase));
    }

    public Task<List<AdminEntry>> GetAdminsAsync() =>
        Task.FromResult(GetCache().ToList());

    public async Task<AdminEntry> AssignAsync(string email, AdminScope scope, Guid? companyId, string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));
        if (scope == AdminScope.Global && companyId is not null)
            throw new ArgumentException("Global admins must not be scoped to a company.");
        if (scope == AdminScope.Company && companyId is null)
            throw new ArgumentException("Company admins must include a companyId.");

        var id = Guid.NewGuid();
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_admins (id, email, scope, company_id, created_by)
            VALUES (@id, @email, @scope, @companyId, @createdBy);", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@email", email));
        cmd.Parameters.Add(new SqlParameter("@scope", scope == AdminScope.Global ? "global" : "company"));
        cmd.Parameters.Add(new SqlParameter("@companyId", (object?)companyId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@createdBy", (object?)createdBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync();

        Invalidate();
        _logger.LogInformation("Admin assigned: {Email} scope={Scope} company={CompanyId} by {CreatedBy}",
            email, scope, companyId, createdBy ?? "unknown");

        var entry = new AdminEntry
        {
            Id = id,
            Email = email,
            Scope = scope,
            CompanyId = companyId,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
        // SOC-2: every admin grant lands in the audit log. Action is
        // "grant" rather than "create" — granting privilege is the verb
        // a reviewer will scan for.
        await _audit.LogAsync(
            actorEmail: createdBy,
            action: AuditActions.Grant,
            resourceType: AuditResources.Admin,
            resourceId: id.ToString(),
            resourceLabel: $"{email} ({(scope == AdminScope.Global ? "global" : "company")})",
            before: null,
            after: new { entry.Email, Scope = entry.Scope.ToString(), entry.CompanyId });
        return entry;
    }

    public async Task RevokeAsync(Guid adminId)
    {
        // Snapshot the row BEFORE deleting so the audit log has a "before"
        // record. After the DELETE the row is gone and there'd be nothing
        // for the review UI to display besides the bare id.
        var existing = GetCache().FirstOrDefault(a => a.Id == adminId);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_admins WHERE id = @id", conn);
        cmd.Parameters.Add(new SqlParameter("@id", adminId));
        await cmd.ExecuteNonQueryAsync();

        Invalidate();
        _logger.LogInformation("Admin revoked: {Id}", adminId);

        // SOC-2: every admin revoke lands in the audit log. Captures the
        // pre-revoke shape so reviewers see who lost what scope, even
        // after the row is gone.
        await _audit.LogAsync(
            actorEmail: null, // accessor pulls from HttpContext
            action: AuditActions.Revoke,
            resourceType: AuditResources.Admin,
            resourceId: adminId.ToString(),
            resourceLabel: existing is null
                ? adminId.ToString()
                : $"{existing.Email} ({(existing.Scope == AdminScope.Global ? "global" : "company")})",
            before: existing is null ? null : new
            {
                existing.Email,
                Scope = existing.Scope.ToString(),
                existing.CompanyId,
                existing.CreatedAt
            },
            after: null);
    }

    public void Invalidate()
    {
        lock (_lock) { _cache = null; }
    }

    // ── Cache + bootstrap ───────────────────────────────────────────────

    private List<AdminEntry> GetCache()
    {
        if (_cache is not null) return _cache;
        lock (_lock)
        {
            if (_cache is not null) return _cache;
            _cache = LoadAndBootstrap();
            return _cache;
        }
    }

    private List<AdminEntry> LoadAndBootstrap()
    {
        var rows = Load();

        // One-time backfill from appsettings.Admins.Emails. Emails already
        // present as global admins are skipped. Entries added this way count
        // as "seed" in the audit trail.
        var existingGlobalEmails = new HashSet<string>(
            rows.Where(r => r.Scope == AdminScope.Global).Select(r => r.Email),
            StringComparer.OrdinalIgnoreCase);

        var toBackfill = _options.CurrentValue.Emails
            .Where(e => !string.IsNullOrWhiteSpace(e) && !existingGlobalEmails.Contains(e))
            .ToList();

        if (toBackfill.Count > 0)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);
                conn.Open();
                foreach (var email in toBackfill)
                {
                    var id = Guid.NewGuid();
                    using var cmd = new SqlCommand(@"
                        INSERT INTO EMPOWER.RPT_admins (id, email, scope, created_by)
                        VALUES (@id, @email, 'global', 'appsettings-seed');", conn);
                    cmd.Parameters.Add(new SqlParameter("@id", id));
                    cmd.Parameters.Add(new SqlParameter("@email", email));
                    cmd.ExecuteNonQuery();
                    _logger.LogInformation("Seeded global admin from appsettings: {Email}", email);
                }
                // Re-load so the cache includes the seeded rows.
                rows = Load();
            }
            catch (Exception ex)
            {
                // Non-fatal: table might not exist yet on a fresh env. Fall
                // back to appsettings-only behavior for this process.
                _logger.LogWarning(ex, "Could not backfill admins from appsettings — continuing with in-memory only.");
                foreach (var email in toBackfill)
                {
                    rows.Add(new AdminEntry
                    {
                        Id = Guid.Empty,
                        Email = email,
                        Scope = AdminScope.Global,
                        CreatedBy = "appsettings-runtime"
                    });
                }
            }
        }

        return rows;
    }

    private List<AdminEntry> Load()
    {
        var rows = new List<AdminEntry>();
        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT id, email, user_id, scope, company_id, created_at, created_by FROM EMPOWER.RPT_admins",
                conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AdminEntry
                {
                    Id = reader.GetGuid(0),
                    Email = reader.GetString(1),
                    UserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Scope = reader.GetString(3).Equals("global", StringComparison.OrdinalIgnoreCase)
                        ? AdminScope.Global
                        : AdminScope.Company,
                    CompanyId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    CreatedAt = reader.GetDateTime(5),
                    CreatedBy = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load RPT_admins — admin API will be empty for this process.");
        }
        return rows;
    }
}
