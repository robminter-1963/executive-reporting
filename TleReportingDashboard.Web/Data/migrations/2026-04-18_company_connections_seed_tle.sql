-- ============================================================================
-- Seed: populate RPT_company_connections with the TLE primary connection
-- matching the current Database-TLE entry in appsettings.Development.json:
--
--   "Database-TLE": "Data Source=tleempprod.loan.local;
--                    Initial Catalog=EMPOWER;
--                    ApplicationIntent=ReadOnly;
--                    Integrated Security=true;
--                    Encrypt=True;
--                    TrustServerCertificate=True;"
--
-- Idempotent: a repeat run is a no-op (matches on company_id + name).
-- Depends on 2026-04-18_company_connections.sql having been applied.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1 FROM EMPOWER.RPT_company_connections
    WHERE company_id = '00000000-0000-0000-0000-000000000001'   -- TLE
      AND name = 'primary'
)
BEGIN
    INSERT INTO EMPOWER.RPT_company_connections (
        company_id,
        name,
        connection_type,
        is_default,
        is_active,
        ss_data_source,
        ss_initial_catalog,
        ss_integrated_security,
        ss_user_id,
        ss_password,
        ss_application_intent,
        ss_encrypt,
        ss_trust_server_certificate
    )
    VALUES (
        '00000000-0000-0000-0000-000000000001',   -- TLE company id
        'primary',
        'sqlserver',
        1,                                         -- is_default
        1,                                         -- is_active
        'tleempprod.loan.local',                   -- Data Source
        'EMPOWER',                                 -- Initial Catalog
        1,                                         -- Integrated Security = true
        NULL,                                      -- no user_id (integrated)
        NULL,                                      -- no password  (integrated)
        'ReadOnly',                                -- ApplicationIntent
        1,                                         -- Encrypt = true
        1                                          -- TrustServerCertificate = true
    );
END
GO

-- Sanity check — should return one row
SELECT id, company_id, name, connection_type, is_default, is_active,
       ss_data_source, ss_initial_catalog, ss_application_intent
FROM EMPOWER.RPT_company_connections
WHERE company_id = '00000000-0000-0000-0000-000000000001';
