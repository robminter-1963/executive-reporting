-- Rollback for 2026-05-01_14-30_user_prefers_company_picker.sql
-- Drops the prefers_company_picker column + its default constraint from RPT_users.

IF EXISTS (
    SELECT 1 FROM sys.default_constraints
     WHERE name = 'DF_RPT_users_prefers_company_picker'
)
BEGIN
    ALTER TABLE EMPOWER.RPT_users
        DROP CONSTRAINT DF_RPT_users_prefers_company_picker;
END
GO

IF COL_LENGTH('EMPOWER.RPT_users', 'prefers_company_picker') IS NOT NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_users
        DROP COLUMN prefers_company_picker;
END
GO
