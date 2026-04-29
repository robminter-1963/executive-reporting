-- ============================================================================
-- Table Alias typing — entity classification + key columns.
--
-- Adds three nullable columns to RPT_custom_primary_tables:
--   * table_type             — one of "LoanSingle", "LoanMultiple",
--                              "BorrowerSingle", "BorrowerMultiple", or NULL
--                              (= unclassified). The set is enforced at the
--                              app layer via a TableTypes constants class so
--                              it can be extended without a schema change.
--   * primary_column         — the column identifying the entity itself
--                              (e.g. "LNKEY" for an Empower loan table).
--                              First component of the table's compound key.
--   * additional_key_columns — comma-separated extras needed alongside
--                              primary_column to identify a single row.
--                              For "LoanMultiple": "IDX". For
--                              "BorrowerMultiple": "WHICHBORR,IDX".
--
-- All three are nullable — existing rows stay valid without migration data.
-- No CHECK constraint on table_type so future LOS adapters (Encompass,
-- Calyx, etc.) can register their own type names without a schema change.
--
-- Idempotent: each ADD COLUMN guarded by sys.columns.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
                 AND name = 'table_type')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD table_type NVARCHAR(40) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
                 AND name = 'primary_column')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD primary_column NVARCHAR(128) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
                 AND name = 'additional_key_columns')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD additional_key_columns NVARCHAR(500) NULL;
END
GO
