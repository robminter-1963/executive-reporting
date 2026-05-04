-- Rollback for 2026-05-03_15-00_role_admin_sections.sql

IF COL_LENGTH('EMPOWER.RPT_roles', 'admin_sections') IS NOT NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_roles DROP COLUMN admin_sections;
END
GO
