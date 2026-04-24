-- ============================================================================
-- Rollback — User Management Phase 1
--
-- Reverses 2026-04-23_23-45_user_management_phase1.sql:
--   * Drops the CK_user_companies_role check constraint
--   * Drops RPT_user_companies.role column
--   * Drops RPT_users table
--   * Drops RPT_companies.display_order column (and its default constraint)
--
-- Idempotent: safe to re-run.
-- ============================================================================

-- ── 1. RPT_user_companies.role ──────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_user_companies_role'
             AND parent_object_id = OBJECT_ID('EMPOWER.RPT_user_companies'))
BEGIN
    ALTER TABLE EMPOWER.RPT_user_companies
        DROP CONSTRAINT CK_user_companies_role;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_user_companies')
             AND name = 'role')
BEGIN
    ALTER TABLE EMPOWER.RPT_user_companies DROP COLUMN role;
END
GO

-- ── 2. RPT_users table ──────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_users')
BEGIN
    DROP TABLE EMPOWER.RPT_users;
END
GO

-- ── 3. RPT_companies.display_order ──────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.default_constraints
           WHERE name = 'DF_companies_display_order'
             AND parent_object_id = OBJECT_ID('EMPOWER.RPT_companies'))
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        DROP CONSTRAINT DF_companies_display_order;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_companies')
             AND name = 'display_order')
BEGIN
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN display_order;
END
GO
