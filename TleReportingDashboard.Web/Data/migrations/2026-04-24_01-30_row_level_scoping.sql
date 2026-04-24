-- ============================================================================
-- Permissions Phase 1 — row-level scoping foundation.
--
-- Adds the two pieces the QueryBuilder hook needs to scope a query down to
-- "rows this user owns" when their role demands it:
--
--   * RPT_roles.scope_rule — 'all' (default) or 'self'. 'all' = no auto-
--                           filter; 'self' = inject
--                           `AND <owner_col> = @external_user_id` into the
--                           emitted WHERE. Future values (team, branch)
--                           slot in here.
--   * RPT_user_connection_logins — per-connection external id for a user.
--                           A company can host multiple LOS / CRM
--                           connections (Encompass + Salesforce, etc.),
--                           and the user's login in each can differ — so
--                           the lookup is keyed by connection_id, not
--                           company_id. No fallback chain: if the row is
--                           missing for (user, connection) the user sees
--                           zero rows for any self-scoped report on that
--                           connection.
--
-- Administrator role is force-set to 'all' on every run so renames /
-- drift can't accidentally scope admins.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. RPT_roles.scope_rule ─────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_roles')
                 AND name = 'scope_rule')
BEGIN
    ALTER TABLE EMPOWER.RPT_roles
        ADD scope_rule NVARCHAR(32) NOT NULL
            CONSTRAINT DF_roles_scope_rule DEFAULT 'all';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_roles_scope_rule'
                 AND parent_object_id = OBJECT_ID('EMPOWER.RPT_roles'))
BEGIN
    ALTER TABLE EMPOWER.RPT_roles
        ADD CONSTRAINT CK_roles_scope_rule
            CHECK (scope_rule IN ('all', 'self'));
END
GO

-- Pin Administrator to 'all' always so a rename or accidental scope flip
-- in the UI can't lock admins out of their own data.
UPDATE EMPOWER.RPT_roles
   SET scope_rule = 'all',
       updated_at = SYSUTCDATETIME()
 WHERE name = 'Administrator' AND scope_rule <> 'all';
GO

-- ── 2. RPT_user_connection_logins ──────────────────────────────────────────
-- Per (user, connection) external login used by the self-scope predicate.
-- user_id accepts either an Entra OID (post-sign-in) or an email stub
-- (pre-provisioned by an admin before the user signs in), matching the
-- existing convention in RPT_user_companies; BindSignedInUserAsync rewrites
-- stubs to the real OID on first sign-in.
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_user_connection_logins')
BEGIN
    CREATE TABLE EMPOWER.RPT_user_connection_logins (
        user_id          NVARCHAR(128)    NOT NULL,
        connection_id    UNIQUEIDENTIFIER NOT NULL
                                          REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
        external_user_id NVARCHAR(64)     NOT NULL,
        created_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY (user_id, connection_id)
    );
    CREATE INDEX IX_user_connection_logins_user ON EMPOWER.RPT_user_connection_logins (user_id);
END
GO
