-- Adds an optional `description` column to RPT_custom_primary_tables so
-- admins can attach a free-text note explaining what each table alias is
-- for ("Loan terms — joined from dl via LNKEY", etc.). Displayed in the
-- Admin → Table Aliases tab next to the alias and editable from the same
-- add/edit dialog.
--
-- Idempotent: skips when the column already exists.

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
                 AND name = 'description')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD description NVARCHAR(500) NULL;
END
GO
