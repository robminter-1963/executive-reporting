using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class UserManagementService : IUserManagementService
{
    private readonly string _connStr;
    private readonly IAdminService _admins;
    private readonly IRoleService _roles;
    private readonly ITeamSourceService _teamSources;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly ILogger<UserManagementService> _logger;

    public UserManagementService(IConfiguration configuration,
                                 IAdminService admins,
                                 IRoleService roles,
                                 ITeamSourceService teamSources,
                                 ConfigDbCache cache,
                                 EditorModeState editorMode,
                                 ILogger<UserManagementService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for UserManagementService.");
        _admins = admins;
        _roles = roles;
        _teamSources = teamSources;
        _cache = cache;
        _editorMode = editorMode;
        _logger = logger;
    }

    // ── Users ───────────────────────────────────────────────────────────────

    // Shared SELECT list. LEFT JOIN on RPT_roles so users with a null
    // role_id still come back; role_name comes along for the UI label.
    private const string UserSelectSql = @"
        SELECT u.email, u.user_id, u.display_name, u.is_admin, u.is_active,
               u.last_visited_company_id, u.created_at, u.created_by, u.updated_at,
               u.role_id, r.name AS role_name, u.prefers_company_picker,
               r.admin_sections AS role_admin_sections
          FROM EMPOWER.RPT_users u
     LEFT JOIN EMPOWER.RPT_roles r ON r.id = u.role_id";

    public Task<List<UserRecord>> GetAllAsync(CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserManagementService", "All"),
            async () =>
            {
                var rows = new List<UserRecord>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(
                    UserSelectSql + " ORDER BY u.is_active DESC, u.display_name, u.email;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(ReadUser(reader));
                }
                return rows;
            },
            bypass: _editorMode.IsActive);

    public Task<UserRecord?> GetByEmailAsync(string email, CancellationToken ct = default)
        => GetByEmailAsync(email, forceRefresh: false, ct);

    public Task<UserRecord?> GetByEmailAsync(string email, bool forceRefresh, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserManagementService", "ByEmail", email),
            async () =>
            {
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(
                    UserSelectSql + " WHERE u.email = @email;", conn);
                cmd.Parameters.Add(new SqlParameter("@email", email));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
            },
            bypass: forceRefresh || _editorMode.IsActive);

    public async Task<UserRecord> CreateAsync(string email, string? displayName, Guid? roleId,
                                              string? createdBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));

        email = email.Trim();
        var isAdmin = await ResolveAdminFromRoleAsync(roleId, ct);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_users (email, display_name, is_admin, role_id, created_by)
            VALUES (@email, @displayName, @isAdmin, @roleId, @createdBy);", conn);
        cmd.Parameters.Add(new SqlParameter("@email", email));
        cmd.Parameters.Add(new SqlParameter("@displayName", (object?)displayName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@isAdmin", isAdmin));
        cmd.Parameters.Add(new SqlParameter("@roleId", (object?)roleId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@createdBy", (object?)createdBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("UserManagementService:");

        // Mirror to RPT_admins for the legacy IsAdmin() check.
        if (isAdmin)
        {
            await _admins.AssignAsync(email, Models.AdminScope.Global, null, createdBy);
        }

        _logger.LogInformation("User created: {Email} role={RoleId} admin={IsAdmin} by {CreatedBy}",
            email, roleId, isAdmin, createdBy ?? "unknown");

        return (await GetByEmailAsync(email, ct))!;
    }

    public async Task UpdateAsync(string email, string? displayName, Guid? roleId, bool isActive,
                                  CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));

        var isAdmin = await ResolveAdminFromRoleAsync(roleId, ct);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_users
               SET display_name = @displayName,
                   is_admin     = @isAdmin,
                   role_id      = @roleId,
                   is_active    = @isActive,
                   updated_at   = SYSUTCDATETIME()
             WHERE email = @email;", conn);
        cmd.Parameters.Add(new SqlParameter("@email", email));
        cmd.Parameters.Add(new SqlParameter("@displayName", (object?)displayName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@isAdmin", isAdmin));
        cmd.Parameters.Add(new SqlParameter("@roleId", (object?)roleId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@isActive", isActive));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("UserManagementService:");

        // Keep RPT_admins in sync with the derived boolean. If the role
        // changed from Administrator → something else, the global row gets
        // revoked; if the reverse, a global row gets assigned.
        await SyncAdminFlagAsync(email, isAdmin, ct);

        _logger.LogInformation("User updated: {Email} role={RoleId} admin={IsAdmin} active={IsActive}",
            email, roleId, isAdmin, isActive);
    }

    // Looks up the role and returns true if it's the Administrator row.
    // Null / unknown / non-admin roles all evaluate false. Keeps the
    // is_admin flag as a derived projection of the picked role.
    private async Task<bool> ResolveAdminFromRoleAsync(Guid? roleId, CancellationToken ct)
    {
        if (roleId is null) return false;
        var role = await _roles.GetByIdAsync(roleId.Value, ct);
        return role is not null
            && string.Equals(role.Name, RoleRecord.AdministratorName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DeleteAsync(string email, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Clean up company access first, then RPT_admins mirror, then the
        // user row. RPT_user_companies rows can be keyed by either the
        // (eventually-real) user_id OR the email stub used for pre-provisioning
        // — delete by both to cover both lifecycle stages.
        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
        {
            await using var cmd = new SqlCommand(@"
                DELETE uc
                  FROM EMPOWER.RPT_user_companies uc
             LEFT JOIN EMPOWER.RPT_users u ON u.user_id = uc.user_id OR u.email = uc.user_id
                 WHERE u.email = @email;

                DELETE FROM EMPOWER.RPT_users WHERE email = @email;", conn, tx);
            cmd.Parameters.Add(new SqlParameter("@email", email));
            await cmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }

        // Remove the legacy admin row (if any). AdminService exposes revoke
        // by id only, so drop straight into SQL here.
        await using (var adminDel = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_admins WHERE email = @email", conn))
        {
            adminDel.Parameters.Add(new SqlParameter("@email", email));
            await adminDel.ExecuteNonQueryAsync(ct);
        }

        _admins.Invalidate();
        _cache.Invalidate("UserManagementService:");
        _logger.LogInformation("User deleted: {Email}", email);
    }

    public async Task BindSignedInUserAsync(string email, string userId, string? displayName,
                                            CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(userId))
            return;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Update-only: fill in user_id on an existing pre-provisioned row
        // (admin added the user via Admin → Users) and capture their
        // display_name if we didn't have one yet. Deliberately NOT INSERT
        // for unknown emails — an authenticated identity that has no
        // RPT_users row is "not registered here," and auto-creating it
        // would mask that state (the picker's "You do not have access to
        // this site" card keys off the missing row).
        //
        // The legacy-admin backfill (email in RPT_admins but not RPT_users)
        // was already handled by the Phase 1 migration's bootstrap INSERT
        // from RPT_admins, so we don't need to cover it here.
        await using (var merge = new SqlCommand(@"
            UPDATE EMPOWER.RPT_users
               SET user_id      = @userId,
                   display_name = COALESCE(display_name, @displayName),
                   updated_at   = SYSUTCDATETIME()
             WHERE email = @email;", conn))
        {
            merge.Parameters.Add(new SqlParameter("@email", email));
            merge.Parameters.Add(new SqlParameter("@userId", userId));
            merge.Parameters.Add(new SqlParameter("@displayName", (object?)displayName ?? DBNull.Value));
            await merge.ExecuteNonQueryAsync(ct);
        }

        // Any pre-provisioned company-access rows we wrote under the email
        // stub get rebound to the real user_id now. No-op when the admin
        // granted access after first sign-in (user_id was used directly).
        await using (var rebind = new SqlCommand(@"
            UPDATE EMPOWER.RPT_user_companies
               SET user_id = @userId
             WHERE user_id = @email;", conn))
        {
            rebind.Parameters.Add(new SqlParameter("@userId", userId));
            rebind.Parameters.Add(new SqlParameter("@email", email));
            await rebind.ExecuteNonQueryAsync(ct);
        }

        // RPT_admins.user_id is nullable today — keep it in sync so Phase 2
        // can drop RPT_admins without re-running a backfill.
        await using (var adminSync = new SqlCommand(@"
            UPDATE EMPOWER.RPT_admins
               SET user_id = @userId
             WHERE email = @email AND user_id IS NULL;", conn))
        {
            adminSync.Parameters.Add(new SqlParameter("@userId", userId));
            adminSync.Parameters.Add(new SqlParameter("@email", email));
            await adminSync.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task SetLastVisitedCompanyAsync(string email, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_users
               SET last_visited_company_id = @companyId,
                   updated_at              = SYSUTCDATETIME()
             WHERE email = @email;", conn);
        cmd.Parameters.Add(new SqlParameter("@email", email));
        cmd.Parameters.Add(new SqlParameter("@companyId", companyId));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate(ConfigDbCache.Key("UserManagementService", "ByEmail", email));
        _cache.Invalidate("UserManagementService:All");
    }

    public async Task ClearLastVisitedCompanyAsync(string email, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_users
               SET last_visited_company_id = NULL,
                   updated_at              = SYSUTCDATETIME()
             WHERE email = @email
               AND last_visited_company_id IS NOT NULL;", conn);
        cmd.Parameters.Add(new SqlParameter("@email", email));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate(ConfigDbCache.Key("UserManagementService", "ByEmail", email));
        _cache.Invalidate("UserManagementService:All");
    }

    public async Task SetPrefersCompanyPickerAsync(string email, bool prefers, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // Unconditional UPDATE — an earlier guarded form
        // (`AND prefers_company_picker <> @prefers`) was no-opping silently
        // in some BIT/bool comparison cases, leaving the picker preference
        // never actually written. The unconditional write is cheap (one
        // row, one column) and avoids that whole class of bug.
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_users
               SET prefers_company_picker = @prefers,
                   updated_at             = SYSUTCDATETIME()
             WHERE email = @email;", conn);
        // Explicit BIT type so the inferred parameter type can't drift
        // between provider versions or affect the WHERE comparison.
        cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 256) { Value = email });
        cmd.Parameters.Add(new SqlParameter("@prefers", System.Data.SqlDbType.Bit) { Value = prefers });
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate(ConfigDbCache.Key("UserManagementService", "ByEmail", email));
        _cache.Invalidate("UserManagementService:All");
    }

    // ── Company access ──────────────────────────────────────────────────────

    public Task<string?> ResolveCanonicalUserEmailAsync(string loginEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(loginEmail)) return Task.FromResult<string?>(null);
        return _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserManagementService", "ResolveCanonicalEmail", loginEmail),
            async () =>
            {
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);

                // 1. Direct hit on RPT_users.email — the common case
                //    (single-AD users; the address they signed in with
                //    matches their canonical login).
                await using (var direct = new SqlCommand(
                    "SELECT email FROM EMPOWER.RPT_users WHERE email = @loginEmail;", conn))
                {
                    direct.Parameters.Add(new SqlParameter("@loginEmail", System.Data.SqlDbType.NVarChar, 256)
                    {
                        Value = loginEmail
                    });
                    if (await direct.ExecuteScalarAsync(ct) is string hit)
                        return hit;
                }

                // 2. Indirect hit via RPT_user_companies.email — the
                //    multi-AD case. The address the user signed in with
                //    is registered as a company-specific contact email
                //    on someone else's grant row; we route back to the
                //    user it belongs to. uc.user_id can be either the
                //    canonical OID or the email stub (pre-binding), so
                //    both forms are checked in the join.
                await using (var indirect = new SqlCommand(@"
                    SELECT TOP 1 u.email
                      FROM EMPOWER.RPT_users u
                      JOIN EMPOWER.RPT_user_companies uc
                        ON uc.user_id = u.user_id OR uc.user_id = u.email
                     WHERE uc.email = @loginEmail;", conn))
                {
                    indirect.Parameters.Add(new SqlParameter("@loginEmail", System.Data.SqlDbType.NVarChar, 256)
                    {
                        Value = loginEmail
                    });
                    return await indirect.ExecuteScalarAsync(ct) as string;
                }
            },
            bypass: _editorMode.IsActive);
    }

    public Task<List<UserCompanyAccess>> GetCompanyAccessAsync(string email, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserManagementService", "CompanyAccess", email),
            async () =>
            {
                var rows = new List<UserCompanyAccess>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
                    SELECT c.id, c.code, c.name, uc.role, uc.is_default, uc.created_at, uc.email
                      FROM EMPOWER.RPT_user_companies uc
                      JOIN EMPOWER.RPT_users u
                        ON u.email = @email
                     JOIN EMPOWER.RPT_companies c ON c.id = uc.company_id
                     WHERE uc.user_id = u.user_id OR uc.user_id = u.email
                     ORDER BY c.name;", conn);
                cmd.Parameters.Add(new SqlParameter("@email", email));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new UserCompanyAccess(
                        email,
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetBoolean(4),
                        reader.GetDateTime(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
                return rows;
            },
            bypass: _editorMode.IsActive);

    public async Task UpsertCompanyAccessAsync(string email, Guid companyId, string role,
                                                string? companyEmail = null,
                                                CancellationToken ct = default)
    {
        if (!UserRoles.IsValid(role))
            throw new ArgumentException($"Role must be one of: {string.Join(", ", UserRoles.All)}.", nameof(role));

        // Tri-state on companyEmail:
        //   null  → don't touch the column (caller is only setting role)
        //   ""    → clear the override (fall back to login email)
        //   any   → trim + persist as the override
        // Empty → NULL semantics keep the "no value" invariant clean instead
        // of also treating empty strings as "no override" at read time.
        bool updateEmail = companyEmail is not null;
        var trimmedCompanyEmail = string.IsNullOrWhiteSpace(companyEmail) ? null : companyEmail.Trim();

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Resolve the stable key: prefer user_id (real Entra OID) if we have
        // one; fall back to the email as a stub until first sign-in. Both
        // forms are valid in RPT_user_companies.user_id per the pre-prov
        // scheme — BindSignedInUserAsync() rewrites stubs on sign-in.
        var key = await ResolveUserKeyAsync(conn, email, ct)
                  ?? throw new InvalidOperationException($"User '{email}' is not registered.");

        // Keep the legacy permission column populated too — Phase 2 drops it,
        // but until then anything reading it (nothing today, future-proofing)
        // should see a sensible mapping.
        var legacyPermission = role == UserRoles.Viewer ? "View" : "Edit";

        // Two MERGE shapes — one preserves the existing email when the caller
        // didn't pass one, the other overwrites. Done with two separate SQL
        // bodies rather than a CASE inside one MERGE so the "don't touch"
        // semantics are bulletproof.
        var sql = updateEmail
            ? @"MERGE EMPOWER.RPT_user_companies AS t
                USING (SELECT @userId AS user_id, @companyId AS company_id) AS s
                   ON t.user_id = s.user_id AND t.company_id = s.company_id
                WHEN MATCHED THEN
                    UPDATE SET role       = @role,
                               permission = @permission,
                               email      = @email
                WHEN NOT MATCHED THEN
                    INSERT (user_id, company_id, role, permission, is_default, email)
                    VALUES (@userId, @companyId, @role, @permission, 0, @email);"
            : @"MERGE EMPOWER.RPT_user_companies AS t
                USING (SELECT @userId AS user_id, @companyId AS company_id) AS s
                   ON t.user_id = s.user_id AND t.company_id = s.company_id
                WHEN MATCHED THEN
                    UPDATE SET role       = @role,
                               permission = @permission
                WHEN NOT MATCHED THEN
                    INSERT (user_id, company_id, role, permission, is_default)
                    VALUES (@userId, @companyId, @role, @permission, 0);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@userId", key));
        cmd.Parameters.Add(new SqlParameter("@companyId", companyId));
        cmd.Parameters.Add(new SqlParameter("@role", role));
        cmd.Parameters.Add(new SqlParameter("@permission", legacyPermission));
        if (updateEmail)
        {
            // Explicit size on the @email parameter. Without it, ADO.NET
            // infers the size from the value's length, and a small inferred
            // size from one execution can prime the parameter type cache and
            // truncate a longer value on a subsequent run. The column is
            // NVARCHAR(256) — match it.
            cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 256)
            {
                Value = (object?)trimmedCompanyEmail ?? DBNull.Value
            });
        }
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("UserManagementService:CompanyAccess:");

        _logger.LogInformation("Company access upserted: {Email} → {CompanyId} role={Role} companyEmailSet={Set}",
            email, companyId, role, updateEmail);
    }

    // ── Per-connection logins (row-level scoping inputs) ───────────────────

    public Task<List<UserConnectionLogin>> GetConnectionLoginsAsync(string email, Guid companyId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserManagementService", "ConnectionLogins", email, companyId),
            async () =>
            {
                var rows = new List<UserConnectionLogin>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                // LEFT JOIN so connections with no login row for this user still
                // come back (ExternalUserId = null). Resolves user_id by either
                // Entra OID OR the email stub to match the pre-provisioning path.
                await using var cmd = new SqlCommand(@"
                    SELECT cc.id, cc.name, cc.is_active, ucl.external_user_id
                      FROM EMPOWER.RPT_company_connections cc
                      JOIN EMPOWER.RPT_users u ON u.email = @email
                 LEFT JOIN EMPOWER.RPT_user_connection_logins ucl
                        ON ucl.connection_id = cc.id
                       AND (ucl.user_id = u.user_id OR ucl.user_id = u.email)
                     WHERE cc.company_id = @companyId
                     ORDER BY cc.is_active DESC, cc.name;", conn);
                cmd.Parameters.Add(new SqlParameter("@email", email));
                cmd.Parameters.Add(new SqlParameter("@companyId", companyId));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new UserConnectionLogin(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetBoolean(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
                return rows;
            },
            bypass: _editorMode.IsActive);

    public async Task UpsertConnectionLoginAsync(string email, Guid connectionId, string? externalUserId,
                                                  CancellationToken ct = default)
    {
        var trimmed = string.IsNullOrWhiteSpace(externalUserId) ? null : externalUserId.Trim();

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        var key = await ResolveUserKeyAsync(conn, email, ct)
                  ?? throw new InvalidOperationException($"User '{email}' is not registered.");

        if (trimmed is null)
        {
            // Empty/whitespace = clear the row. Keeps the "no row = no
            // match" invariant simple instead of also treating empty
            // strings as "no match" at query time.
            await using var del = new SqlCommand(@"
                DELETE FROM EMPOWER.RPT_user_connection_logins
                 WHERE user_id = @userId AND connection_id = @connectionId;", conn);
            del.Parameters.Add(new SqlParameter("@userId", key));
            del.Parameters.Add(new SqlParameter("@connectionId", connectionId));
            await del.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var cmd = new SqlCommand(@"
                MERGE EMPOWER.RPT_user_connection_logins AS t
                USING (SELECT @userId AS user_id, @connectionId AS connection_id) AS s
                   ON t.user_id = s.user_id AND t.connection_id = s.connection_id
                WHEN MATCHED THEN
                    UPDATE SET external_user_id = @externalUserId,
                               updated_at       = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (user_id, connection_id, external_user_id)
                    VALUES (@userId, @connectionId, @externalUserId);", conn);
            cmd.Parameters.Add(new SqlParameter("@userId", key));
            cmd.Parameters.Add(new SqlParameter("@connectionId", connectionId));
            cmd.Parameters.Add(new SqlParameter("@externalUserId", trimmed));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _cache.Invalidate("UserManagementService:ConnectionLogins:");
        _cache.Invalidate("UserManagementService:ExternalUserId:");
        _logger.LogInformation("Connection login upserted: {Email} → {ConnectionId} externalId={ExternalId}",
            email, connectionId, trimmed ?? "(cleared)");
    }

    public Task<string?> GetExternalUserIdAsync(string email, Guid connectionId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserManagementService", "ExternalUserId", email, connectionId),
            async () =>
            {
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
                    SELECT ucl.external_user_id
                      FROM EMPOWER.RPT_user_connection_logins ucl
                      JOIN EMPOWER.RPT_users u ON u.email = @email
                     WHERE ucl.connection_id = @connectionId
                       AND (ucl.user_id = u.user_id OR ucl.user_id = u.email);", conn);
                cmd.Parameters.Add(new SqlParameter("@email", email));
                cmd.Parameters.Add(new SqlParameter("@connectionId", connectionId));
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result is null || result is DBNull) return null;
                var value = (string)result;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            },
            bypass: _editorMode.IsActive);

    public async Task RevokeCompanyAccessAsync(string email, Guid companyId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // Delete matches either keying variant so a pre-signed-in pre-prov
        // grant gets cleaned up too.
        await using var cmd = new SqlCommand(@"
            DELETE uc
              FROM EMPOWER.RPT_user_companies uc
              JOIN EMPOWER.RPT_users u ON u.email = @email
             WHERE uc.company_id = @companyId
               AND (uc.user_id = u.user_id OR uc.user_id = u.email);", conn);
        cmd.Parameters.Add(new SqlParameter("@email", email));
        cmd.Parameters.Add(new SqlParameter("@companyId", companyId));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("UserManagementService:CompanyAccess:");

        _logger.LogInformation("Company access revoked: {Email} → {CompanyId}", email, companyId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static UserRecord ReadUser(SqlDataReader r) => new(
        r.GetString(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.GetBoolean(3),
        r.GetBoolean(4),
        r.IsDBNull(5) ? null : r.GetGuid(5),
        r.GetDateTime(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.GetDateTime(8))
    {
        RoleId   = r.IsDBNull(9)  ? null : r.GetGuid(9),
        RoleName = r.IsDBNull(10) ? null : r.GetString(10),
        PrefersCompanyPicker = r.GetBoolean(11),
        RoleAdminSections = r.IsDBNull(12) ? null : ParseAdminSectionsJson(r.GetString(12))
    };

    // Mirror of RoleService.ParseSectionsJson — kept here so UserRecord
    // population doesn't pull in a service dependency. Filters to known
    // section keys and swallows malformed JSON (returns null).
    private static List<string>? ParseAdminSectionsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return arr?.Where(AdminSections.IsValid).ToList();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ResolveUserKeyAsync(SqlConnection conn, string email, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT user_id FROM EMPOWER.RPT_users WHERE email = @email;", conn);
        cmd.Parameters.Add(new SqlParameter("@email", email));
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null) return null;                 // user isn't registered
        if (result is DBNull) return email;              // pre-provisioned — stub with the email
        return (string)result;
    }

    private async Task SyncAdminFlagAsync(string email, bool isAdmin, CancellationToken ct)
    {
        var currentAdmins = await _admins.GetAdminsAsync();
        var existingGlobal = currentAdmins.FirstOrDefault(a =>
            a.Scope == Models.AdminScope.Global &&
            string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));

        if (isAdmin && existingGlobal is null)
        {
            await _admins.AssignAsync(email, Models.AdminScope.Global, null, "user-management");
        }
        else if (!isAdmin && existingGlobal is not null)
        {
            await _admins.RevokeAsync(existingGlobal.Id);
        }
    }

    // ── Team assignments ───────────────────────────────────────────────────

    public async Task<List<AssignableTeam>> GetAssignableTeamsAsync(string email, CancellationToken ct = default)
    {
        // Step 1: list the (company, connection) pairs the user has access to.
        // Same filter as GetConnectionLoginsAsync — granted companies only,
        // active connections only.
        var targets = new List<(Guid ConnectionId, string ConnectionName, Guid CompanyId, string CompanyName)>();
        await using (var conn = new SqlConnection(_connStr))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
                SELECT cc.id, cc.name, cc.company_id, co.name
                  FROM EMPOWER.RPT_company_connections cc
                  JOIN EMPOWER.RPT_companies co ON co.id = cc.company_id
                  JOIN EMPOWER.RPT_users u ON u.email = @email
                  JOIN EMPOWER.RPT_user_companies uc
                    ON uc.company_id = cc.company_id
                   AND (uc.user_id = u.user_id OR uc.user_id = u.email)
                 WHERE cc.is_active = 1
                 ORDER BY co.name, cc.name;", conn);
            cmd.Parameters.Add(new SqlParameter("@email", email));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                targets.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetGuid(2),
                    reader.GetString(3)));
            }
        }

        // Step 2: for each target, run the connection's configured teams_sql
        // live against the source DB. A bad SQL, missing config, or
        // unreachable source on one connection shouldn't block the editor
        // from loading teams on the others — log and skip.
        var rows = new List<AssignableTeam>();
        foreach (var (connId, connName, companyId, companyName) in targets)
        {
            try
            {
                var teams = await _teamSources.QueryTeamsAsync(connId, ct);
                foreach (var t in teams)
                {
                    rows.Add(new AssignableTeam(
                        connId, t.TeamId, t.TeamName, t.TeamType,
                        companyId, companyName, connName));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Skipping team source for connection {ConnectionId} ({ConnectionName}) while building assignable teams for {Email}: {Message}",
                    connId, connName, email, ex.Message);
            }
        }
        return rows;
    }

    public Task<List<UserTeamAssignment>> GetUserTeamsAsync(string email, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("UserManagementService", "UserTeams", email),
            async () =>
            {
                var rows = new List<UserTeamAssignment>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);

                // Resolve on either user_id form (Entra OID or email stub) so
                // assignments survive the stub→OID rewrite on first sign-in.
                await using var cmd = new SqlCommand(@"
                    SELECT ut.connection_id, ut.team_id
                      FROM EMPOWER.RPT_user_teams ut
                      JOIN EMPOWER.RPT_users u ON u.email = @email
                     WHERE ut.user_id = u.user_id OR ut.user_id = u.email
                     ORDER BY ut.connection_id, ut.team_id;", conn);
                cmd.Parameters.Add(new SqlParameter("@email", email));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new UserTeamAssignment(reader.GetGuid(0), reader.GetInt32(1)));
                }
                return rows;
            },
            bypass: _editorMode.IsActive);

    public async Task SetUserTeamsAsync(string email, IReadOnlyList<UserTeamAssignment> teams,
                                         CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        var key = await ResolveUserKeyAsync(conn, email, ct)
                  ?? throw new InvalidOperationException($"User '{email}' is not registered.");

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // Full replace: simpler than diffing and correct for the UI
            // semantics (the editor shows the full desired state). Also
            // wipes any rows left under an older user_id form if this call
            // comes post-sign-in — the key resolver picks the OID now.
            await using (var del = new SqlCommand(@"
                DELETE FROM EMPOWER.RPT_user_teams
                 WHERE user_id IN (
                    SELECT user_id FROM EMPOWER.RPT_users WHERE email = @email
                    UNION SELECT email FROM EMPOWER.RPT_users WHERE email = @email
                 );", conn, tx))
            {
                del.Parameters.Add(new SqlParameter("@email", email));
                await del.ExecuteNonQueryAsync(ct);
            }

            // Dedup in case the UI passed duplicates.
            var seen = new HashSet<(Guid, int)>();
            foreach (var t in teams)
            {
                if (!seen.Add((t.ConnectionId, t.TeamId))) continue;
                await using var ins = new SqlCommand(@"
                    INSERT INTO EMPOWER.RPT_user_teams (user_id, connection_id, team_id)
                    VALUES (@userId, @connectionId, @teamId);", conn, tx);
                ins.Parameters.Add(new SqlParameter("@userId", key));
                ins.Parameters.Add(new SqlParameter("@connectionId", t.ConnectionId));
                ins.Parameters.Add(new SqlParameter("@teamId", t.TeamId));
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _cache.Invalidate(ConfigDbCache.Key("UserManagementService", "UserTeams", email));
            _logger.LogInformation("Team assignments set for {Email}: {Count} team(s).", email, seen.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
