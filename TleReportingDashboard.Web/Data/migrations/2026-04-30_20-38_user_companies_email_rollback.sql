-- Rollback for 2026-04-30_20-38_user_companies_email.sql.
-- Drops the per-company email column. Loses any per-company overrides
-- admins set after the forward migration ran — non-recoverable.

IF COL_LENGTH('EMPOWER.RPT_user_companies', 'email') IS NOT NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_user_companies DROP COLUMN email;
END
GO
