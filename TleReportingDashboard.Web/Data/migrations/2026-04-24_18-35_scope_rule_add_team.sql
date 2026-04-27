-- ============================================================================
-- Permissions — add 'team' to the allowed scope_rule values.
--
-- The original migration anticipated this (see 2026-04-24_01-30_row_level_
-- scoping.sql, header comment). The UI now offers "Only for their team" as
-- a scope choice so roles can be created with it today; the team layout
-- itself — membership table, QueryBuilder predicate, etc. — will follow in
-- a later migration. Until then, 'team' is stored but behaves like 'all'
-- at query time (no auto-filter injected).
--
-- Idempotent: drops and recreates the CHECK with the superset of values.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_roles_scope_rule'
             AND parent_object_id = OBJECT_ID('EMPOWER.RPT_roles'))
BEGIN
    ALTER TABLE EMPOWER.RPT_roles DROP CONSTRAINT CK_roles_scope_rule;
END
GO

ALTER TABLE EMPOWER.RPT_roles
    ADD CONSTRAINT CK_roles_scope_rule
        CHECK (scope_rule IN ('all', 'self', 'team'));
GO
