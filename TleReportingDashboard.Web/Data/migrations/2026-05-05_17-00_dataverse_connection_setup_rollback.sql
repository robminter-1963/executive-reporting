-- Rollback for 2026-05-05_17-00_dataverse_connection_setup.sql.
-- Drops the four dv_* columns and tightens the connection_type CHECK
-- back to ('sqlserver', 'postgres'). Any rows with type='dataverse'
-- would block the constraint re-add; expect them to be cleaned up
-- before rolling back (SELECT id, name FROM RPT_company_connections
-- WHERE connection_type = 'dataverse' first).

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = 'CK_RPT_company_connections_type'
       AND parent_object_id = OBJECT_ID('EMPOWER.RPT_company_connections'))
    ALTER TABLE EMPOWER.RPT_company_connections
        DROP CONSTRAINT CK_RPT_company_connections_type;
GO

-- Restore a narrower CHECK matching the pre-Dataverse seed.
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints c
      JOIN sys.tables t ON t.object_id = c.parent_object_id
     WHERE t.name = 'RPT_company_connections'
       AND c.definition LIKE '%connection_type%')
    ALTER TABLE EMPOWER.RPT_company_connections WITH NOCHECK
        ADD CONSTRAINT CK_RPT_company_connections_type_legacy
            CHECK (connection_type IN ('sqlserver', 'postgres'));
GO

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_client_secret') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_company_connections DROP COLUMN dv_client_secret;
GO

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_client_id') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_company_connections DROP COLUMN dv_client_id;
GO

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_tenant_id') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_company_connections DROP COLUMN dv_tenant_id;
GO

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_environment_url') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_company_connections DROP COLUMN dv_environment_url;
GO
