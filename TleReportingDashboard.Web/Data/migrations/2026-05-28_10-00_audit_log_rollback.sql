-- Rollback the audit-log table. Drops the indexes implicitly with the table.
-- Idempotent: safe to re-run.
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_audit_log')
    DROP TABLE EMPOWER.RPT_audit_log;
GO
