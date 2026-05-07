-- Rollback for 2026-05-05_18-00_company_hidden_flag.sql.
-- Drops the default constraint first (SQL Server requires it) then the column.

IF EXISTS (
    SELECT 1 FROM sys.default_constraints
     WHERE name = 'DF_RPT_companies_is_hidden')
    ALTER TABLE EMPOWER.RPT_companies DROP CONSTRAINT DF_RPT_companies_is_hidden;
GO

IF COL_LENGTH('EMPOWER.RPT_companies', 'is_hidden') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN is_hidden;
GO
