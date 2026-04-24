-- ============================================================================
-- User Management — Phase 1
--
-- Adds:
--   * RPT_companies.display_order — admin-controlled ordering for the main
--     company picker page. Default 0 sorts to the top; later rows are
--     tie-broken by name.
--   * RPT_users — canonical user registry (email is the stable key). Stores
--     is_admin (single boolean — admins see every company), display_name,
--     is_active, and last_visited_company_id for URL resume. user_id is the
--     Entra object ID, NULL until the user first signs in (pre-provisioning
--     supported).
--   * RPT_user_companies.role — per-company role for non-admins
--     ('Editor' | 'Viewer' | 'Scheduler'). Backfilled from the existing
--     permission column ('View'→'Viewer', 'Edit'→'Editor'). The old
--     permission column stays for back-compat until Phase 2 rewrites readers.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. RPT_companies.display_order ──────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_companies')
                 AND name = 'display_order')
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        ADD display_order INT NOT NULL
            CONSTRAINT DF_companies_display_order DEFAULT 0;
END
GO

-- ── 2. RPT_users — canonical user registry ──────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_users')
BEGIN
    CREATE TABLE EMPOWER.RPT_users (
        email                    NVARCHAR(256)    NOT NULL PRIMARY KEY,
        user_id                  NVARCHAR(128)    NULL,             -- Entra OID, populated on first sign-in
        display_name             NVARCHAR(256)    NULL,
        is_admin                 BIT              NOT NULL DEFAULT 0,
        is_active                BIT              NOT NULL DEFAULT 1,
        last_visited_company_id  UNIQUEIDENTIFIER NULL
                                 REFERENCES EMPOWER.RPT_companies(id),
        created_at               DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        created_by               NVARCHAR(256)    NULL,
        updated_at               DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );

    -- user_id is the Entra OID used everywhere else in the DB. Unique when
    -- populated, but NULL is allowed for pre-provisioned users who haven't
    -- signed in yet — so a filtered unique index instead of a full one.
    CREATE UNIQUE INDEX UX_users_user_id
        ON EMPOWER.RPT_users (user_id)
        WHERE user_id IS NOT NULL;
END
GO

-- ── 2b. Seed RPT_users from existing data ───────────────────────────────────
-- Best-effort backfill so current signed-in users don't disappear. We pull
-- every known (email, user_id) pair from RPT_admins (which has both) and
-- insert a row for each. RPT_user_preferences / RPT_user_companies only
-- have user_id (no email), so those rows can't seed RPT_users directly;
-- they'll get picked up on the user's next sign-in.
INSERT INTO EMPOWER.RPT_users (email, user_id, is_admin, created_by)
SELECT DISTINCT a.email,
       a.user_id,
       1,                            -- every row in RPT_admins is, by definition, an admin
       'bootstrap'
FROM EMPOWER.RPT_admins a
WHERE NOT EXISTS (
    SELECT 1 FROM EMPOWER.RPT_users u
    WHERE u.email = a.email
);
GO

-- ── 3. RPT_user_companies.role ──────────────────────────────────────────────
-- Nullable first so the backfill can populate it before we clamp it NOT NULL.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_user_companies')
                 AND name = 'role')
BEGIN
    ALTER TABLE EMPOWER.RPT_user_companies
        ADD role NVARCHAR(20) NULL;
END
GO

-- Backfill role from the legacy permission column. Keep permission intact —
-- nothing reads role yet, and nothing will stop reading permission until
-- Phase 2 migrates the services. Belt-and-braces during the transition.
UPDATE EMPOWER.RPT_user_companies
   SET role = CASE permission
                  WHEN 'View' THEN 'Viewer'
                  WHEN 'Edit' THEN 'Editor'
                  ELSE 'Viewer'
              END
 WHERE role IS NULL;
GO

-- Now clamp NOT NULL + add the allowed-values check.
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_user_companies')
             AND name = 'role'
             AND is_nullable = 1)
BEGIN
    ALTER TABLE EMPOWER.RPT_user_companies
        ALTER COLUMN role NVARCHAR(20) NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_user_companies_role'
                 AND parent_object_id = OBJECT_ID('EMPOWER.RPT_user_companies'))
BEGIN
    ALTER TABLE EMPOWER.RPT_user_companies
        ADD CONSTRAINT CK_user_companies_role
            CHECK (role IN ('Editor', 'Viewer', 'Scheduler'));
END
GO
