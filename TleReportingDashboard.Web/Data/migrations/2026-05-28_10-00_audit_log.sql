-- ============================================================================
-- RPT_audit_log — SOC-2 change-management trail for security-affecting
-- admin actions. Captures: who, when, what, before/after, against which
-- resource. Append-only by convention; do not UPDATE or DELETE rows from
-- application code. (Schema doesn't physically prevent it — operationally
-- you'd grant the app role only INSERT+SELECT on this table.)
--
-- Scope explicitly EXCLUDES end-user content (saved reports, personal
-- preferences, favorites, individual schedules). Those are like a user's
-- own Word doc — not in scope for SOC-2 change management. The audit log
-- only records admin actions on:
--   * RPT_admins (grants/revokes of global or company admin)
--   * RPT_roles (admin_sections, scope_rule changes)
--   * RPT_users + RPT_user_companies (other-user edits, access grants)
--   * RPT_companies (create / disable / hide / display-order)
--   * RPT_company_connections (config + credential rotations)
--   * RPT_library_sections, RPT_custom_primary_tables, custom filters
--   * RPT_master_dashboard_* (shared per-company layout)
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_audit_log')
BEGIN
    CREATE TABLE EMPOWER.RPT_audit_log (
        id              BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        -- When the action happened on the server. UTC throughout the app.
        occurred_at     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        -- Who did it. Email is the stable display key; user_id is the
        -- Entra oid when resolvable (helps if the user later renames).
        actor_email     NVARCHAR(256)    NULL,
        actor_user_id   NVARCHAR(128)    NULL,
        -- Verb: 'create' | 'update' | 'delete' | 'grant' | 'revoke' |
        --        'enable' | 'disable' | 'reorder'. See AuditActions.
        action          NVARCHAR(32)     NOT NULL,
        -- The kind of thing changed. See AuditResources.
        resource_type   NVARCHAR(64)     NOT NULL,
        -- Surrogate identifier of the changed row. GUIDs stored as text,
        -- ints as text — keeps the column generic across resource types.
        resource_id     NVARCHAR(128)    NULL,
        -- Human-readable label for the changed row at the time of change
        -- ("ACME · primary", "Loan Officer", "rob@ralis.com"). Lets the
        -- review UI display useful context without joining the source table
        -- (which may have been deleted by the time someone reviews).
        resource_label  NVARCHAR(500)    NULL,
        -- Snapshot before / after, stored as JSON. Either may be null:
        -- before=null on create, after=null on delete. The review UI diffs
        -- these client-side.
        before_json     NVARCHAR(MAX)    NULL,
        after_json      NVARCHAR(MAX)    NULL,
        -- Optional grouping id when one logical operation produced multiple
        -- log rows (e.g. saving a connection that also re-clears the
        -- previous default would log both with the same correlation_id).
        correlation_id  NVARCHAR(64)     NULL,
        -- Free-text note the service-layer can attach ("imported via
        -- promotion package", "appsettings bootstrap", etc).
        notes           NVARCHAR(500)    NULL
    );

    -- Most reads are "show me everything in the last N days" — index
    -- occurred_at DESC for that scan, plus filters by actor and by
    -- (resource_type, resource_id) for the resource-history drill-in.
    CREATE INDEX IX_audit_log_occurred_at
        ON EMPOWER.RPT_audit_log (occurred_at DESC);
    CREATE INDEX IX_audit_log_actor
        ON EMPOWER.RPT_audit_log (actor_email, occurred_at DESC);
    CREATE INDEX IX_audit_log_resource
        ON EMPOWER.RPT_audit_log (resource_type, resource_id, occurred_at DESC);
END;
GO
