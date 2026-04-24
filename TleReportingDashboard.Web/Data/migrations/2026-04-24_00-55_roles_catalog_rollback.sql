-- ============================================================================
-- Rollback — roles catalog.
--
-- Drops RPT_users.role_id (and its FK) and then RPT_roles. Users keep their
-- is_admin flag, so rollback doesn't change who's an admin; it just removes
-- the role label layer.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_users_role')
BEGIN
    ALTER TABLE EMPOWER.RPT_users DROP CONSTRAINT FK_users_role;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_users')
             AND name = 'role_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_users DROP COLUMN role_id;
END
GO

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_roles')
BEGIN
    DROP TABLE EMPOWER.RPT_roles;
END
GO
