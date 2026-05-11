-- Rollback: 2026-05-09_18-00_drop_company_legacy_datasource.sql
--
-- Re-adds the columns as NOT NULL with neutral defaults so existing
-- rows satisfy the constraint. The original code paths consumed these
-- columns are gone, so a roll-back here is purely schema-shape — no
-- runtime caller will populate or read them.

IF COL_LENGTH('EMPOWER.RPT_companies', 'data_source_type') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        ADD data_source_type NVARCHAR(20) NOT NULL
            CONSTRAINT DF_RPT_companies_data_source_type DEFAULT 'sqlserver';
END
GO

IF COL_LENGTH('EMPOWER.RPT_companies', 'connection_ref') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        ADD connection_ref NVARCHAR(100) NOT NULL
            CONSTRAINT DF_RPT_companies_connection_ref DEFAULT '(legacy)';
END
GO
