-- ============================================================================
-- Adds RPT_user_companies.email — a per-company email override for users who
-- have separate addresses per tenant (e.g. one Outlook account per company).
--
-- NULL means "fall back to RPT_users.email" (the login / Entra address). Auth
-- still goes by Entra OID; this column is used by callers that need to REACH
-- the user inside a particular company's context (scheduled deliveries,
-- share notifications, display labels). Existing rows stay NULL — current
-- behavior continues to use the login email until an admin sets an override.
--
-- Idempotent: COL_LENGTH guards on the ALTER.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF COL_LENGTH('EMPOWER.RPT_user_companies', 'email') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_user_companies
        ADD email NVARCHAR(256) NULL;
END
GO
