-- Drops the legacy data_source_type / connection_ref columns from
-- RPT_companies. These were the appsettings-key fan-out from before
-- RPT_company_connections existed (see 2026-04-18_company_connections),
-- and they hard-coded a "one type per company" assumption that breaks
-- the moment a company has connections of different types (e.g. a
-- SQL Server LOS connection AND a Dataverse HR connection on the same
-- tenant). The runtime never falls back to these columns — every
-- per-company connection now resolves through RPT_company_connections —
-- so dropping them is a no-op at fire time.
--
-- Idempotent.

IF COL_LENGTH('EMPOWER.RPT_companies', 'data_source_type') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN data_source_type;
GO

IF COL_LENGTH('EMPOWER.RPT_companies', 'connection_ref') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN connection_ref;
GO
