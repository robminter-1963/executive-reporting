-- ============================================================================
-- Roles catalog
--
-- Introduces RPT_roles — a job-function role list managed by admins (Admin →
-- Roles) — and a role_id FK on RPT_users pointing to the user's single role.
--
-- Separate from:
--   * RPT_user_companies.role (values 'Editor' | 'Viewer' | 'Scheduler') —
--     that's a per-company permission tier and stays where it is until we
--     consolidate permissions in a later phase.
--   * RPT_users.is_admin (single boolean) — system-level "sees and edits
--     everything" switch. "Administrator" in the role list is the SAME
--     concept: when a user's role is 'Administrator', is_admin must be true;
--     otherwise false. The service layer keeps the two in sync so callers
--     can use either (IsAdmin for permission checks, role for UI labels).
--
-- Seeds 15 canonical roles on first run. Admins can add, rename, or disable
-- any role later — only 'Administrator' has hard-coded behavior in code
-- (matched by name), so leave that one intact even if renaming others.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. RPT_roles ────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_roles')
BEGIN
    CREATE TABLE EMPOWER.RPT_roles (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        name          NVARCHAR(100)    NOT NULL UNIQUE,
        description   NVARCHAR(500)    NULL,
        is_active     BIT              NOT NULL DEFAULT 1,
        sort_order    INT              NOT NULL DEFAULT 0,
        created_at    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        created_by    NVARCHAR(256)    NULL,
        updated_at    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ── 2. Seed the starter set ─────────────────────────────────────────────────
-- sort_order keeps 'Administrator' first; the rest follow the user's order.
INSERT INTO EMPOWER.RPT_roles (name, sort_order, created_by)
SELECT v.name, v.sort_order, 'bootstrap'
  FROM (VALUES
        ('Administrator',             0),
        ('Closer',                    1),
        ('Closing Manager',           2),
        ('Funder',                    3),
        ('Funder Manager',            4),
        ('Loan Originator',           5),
        ('Loan Originator Manager',   6),
        ('Lock Desk',                 7),
        ('Lock Desk Manager',         8),
        ('Post Closer',               9),
        ('Post Closing Manager',     10),
        ('Processing Manager',       11),
        ('Processor',                12),
        ('Underwriter',              13),
        ('Underwriting Manager',     14)
    ) v(name, sort_order)
 WHERE NOT EXISTS (SELECT 1 FROM EMPOWER.RPT_roles r WHERE r.name = v.name);
GO

-- ── 3. RPT_users.role_id ────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_users')
                 AND name = 'role_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_users
        ADD role_id UNIQUEIDENTIFIER NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_users_role')
BEGIN
    ALTER TABLE EMPOWER.RPT_users
        ADD CONSTRAINT FK_users_role
            FOREIGN KEY (role_id) REFERENCES EMPOWER.RPT_roles(id)
            ON DELETE SET NULL;
END
GO

-- ── 4. Backfill existing admins to role = Administrator ────────────────────
-- Every user currently carrying is_admin = 1 gets the 'Administrator' role
-- so the UI can surface a consistent label. New admins going forward will
-- be assigned the role by the service layer (which also sets is_admin).
UPDATE u
   SET role_id = r.id,
       updated_at = SYSUTCDATETIME()
  FROM EMPOWER.RPT_users u
  JOIN EMPOWER.RPT_roles r ON r.name = 'Administrator'
 WHERE u.is_admin = 1 AND u.role_id IS NULL;
GO
