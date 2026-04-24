-- ============================================================================
-- Rollback — row-level scoping foundation.
-- Drops scope_rule from RPT_roles and RPT_user_connection_logins table.
-- Idempotent: safe to re-run.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_roles_scope_rule'
             AND parent_object_id = OBJECT_ID('EMPOWER.RPT_roles'))
BEGIN
    ALTER TABLE EMPOWER.RPT_roles DROP CONSTRAINT CK_roles_scope_rule;
END
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints
           WHERE name = 'DF_roles_scope_rule'
             AND parent_object_id = OBJECT_ID('EMPOWER.RPT_roles'))
BEGIN
    ALTER TABLE EMPOWER.RPT_roles DROP CONSTRAINT DF_roles_scope_rule;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_roles')
             AND name = 'scope_rule')
BEGIN
    ALTER TABLE EMPOWER.RPT_roles DROP COLUMN scope_rule;
END
GO

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_user_connection_logins')
BEGIN
    DROP TABLE EMPOWER.RPT_user_connection_logins;
END
GO
