-- Adds Dataverse-flavored fields to RPT_company_connections + relaxes
-- the connection_type CHECK constraint to allow 'dataverse' alongside
-- 'sqlserver' / 'postgres'. Setup-only — query pipeline / scope
-- resolver / schema builder are unchanged in this migration; they'll
-- continue to NotSupportedException on a Dataverse-typed connection
-- until those subsystems are extended.
--
-- Why string-typed credentials (not byte[] cert / key like Postgres):
--   v1 ships only the Entra OAuth2 client_credentials path. Future
--   support for certificate-based auth would add dv_certificate +
--   dv_certificate_password columns alongside these.
--
-- Idempotent throughout — re-running on an already-patched DB is a
-- no-op for each ALTER and the CHECK swap.

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_environment_url') IS NULL
    ALTER TABLE EMPOWER.RPT_company_connections ADD dv_environment_url NVARCHAR(500) NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_tenant_id') IS NULL
    ALTER TABLE EMPOWER.RPT_company_connections ADD dv_tenant_id NVARCHAR(100) NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_client_id') IS NULL
    ALTER TABLE EMPOWER.RPT_company_connections ADD dv_client_id NVARCHAR(100) NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'dv_client_secret') IS NULL
    ALTER TABLE EMPOWER.RPT_company_connections ADD dv_client_secret NVARCHAR(500) NULL;
GO

-- Drop the existing inline CHECK constraint (auto-named by SQL Server)
-- and re-create with an explicit name so future migrations can target
-- it without dynamic SQL. Names like "CK__RPT_compa__conne__..."
-- aren't portable across DB restores, so we look it up dynamically.
DECLARE @ck NVARCHAR(200);
SELECT @ck = c.name
  FROM sys.check_constraints c
  JOIN sys.tables t ON t.object_id = c.parent_object_id
  JOIN sys.schemas s ON s.schema_id = t.schema_id
 WHERE s.name = 'EMPOWER'
   AND t.name = 'RPT_company_connections'
   AND c.definition LIKE '%connection_type%'
   AND c.name <> 'CK_RPT_company_connections_type';   -- preserve our renamed copy on re-runs
IF @ck IS NOT NULL
    EXEC('ALTER TABLE EMPOWER.RPT_company_connections DROP CONSTRAINT ' + QUOTENAME(@ck));
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = 'CK_RPT_company_connections_type'
       AND parent_object_id = OBJECT_ID('EMPOWER.RPT_company_connections'))
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections WITH NOCHECK
        ADD CONSTRAINT CK_RPT_company_connections_type
            CHECK (connection_type IN ('sqlserver', 'postgres', 'dataverse'));
END
GO
