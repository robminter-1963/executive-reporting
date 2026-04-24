-- ============================================================================
-- Row-level scoping — owner field per primary table.
--
-- RPT_custom_primary_tables gets an owner_field_id NVARCHAR(128) NULL.
-- Points to the SchemaConfig field id whose column identifies who "owns"
-- a row in this primary table (e.g. "loans.loan_officer_id"). When a
-- self-scoped role runs a report with this primary table, the QueryBuilder
-- injects `<ownerCol> = @external_user_id` into the WHERE clause. NULL =
-- the primary table has no owner concept, so self-scoped queries on it
-- return nothing (the pipeline fails safe).
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
                 AND name = 'owner_field_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD owner_field_id NVARCHAR(128) NULL;
END
GO
