-- ============================================================================
-- RPT_admins — user admin assignments (global + per-company)
--
-- Replaces the appsettings "Admins.Emails" list with a DB-backed admin
-- catalog that supports two scopes:
--   * 'global'  — admin across every company
--   * 'company' — admin only for the referenced company
--
-- Global admin implies company admin for every company (the service
-- collapses both into a single IsCompanyAdmin check).
--
-- Backfill: on first run, every email in the appsettings Admins.Emails list
-- should be inserted as a 'global' admin. That happens in the service-layer
-- bootstrap (AdminService.BootstrapAsync) rather than here so the list
-- stays configurable without redeploying SQL.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_admins')
BEGIN
    CREATE TABLE EMPOWER.RPT_admins (
        id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        email      NVARCHAR(256)    NOT NULL,         -- Entra email (lookup key)
        user_id    NVARCHAR(128)    NULL,             -- Entra object ID (populated on first sign-in)
        scope      NVARCHAR(20)     NOT NULL
                   CHECK (scope IN ('global', 'company')),
        company_id UNIQUEIDENTIFIER NULL
                   REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
        created_at DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        created_by NVARCHAR(256)    NULL,

        -- Global rows must have no company_id; company rows must have one.
        CONSTRAINT CK_admins_company_when_scoped CHECK (
            (scope = 'global'  AND company_id IS NULL)
            OR (scope = 'company' AND company_id IS NOT NULL)
        )
    );

    -- No dup global row per email
    CREATE UNIQUE INDEX UX_admins_global
        ON EMPOWER.RPT_admins (email)
        WHERE scope = 'global';

    -- No dup company row per (email, company)
    CREATE UNIQUE INDEX UX_admins_company
        ON EMPOWER.RPT_admins (email, company_id)
        WHERE scope = 'company';

    CREATE INDEX IX_admins_email   ON EMPOWER.RPT_admins (email);
    CREATE INDEX IX_admins_user_id ON EMPOWER.RPT_admins (user_id);
END
GO
