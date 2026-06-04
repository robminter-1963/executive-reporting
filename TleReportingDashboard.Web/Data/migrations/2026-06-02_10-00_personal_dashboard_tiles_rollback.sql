-- Rollback personal-tiles table. Indexes drop implicitly with the table.
-- Idempotent: safe to re-run.
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_master_dashboard_personal_tiles')
    DROP TABLE EMPOWER.RPT_master_dashboard_personal_tiles;
GO
