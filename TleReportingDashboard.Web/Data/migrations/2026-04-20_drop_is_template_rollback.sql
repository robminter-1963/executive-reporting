-- Restores is_template column, its default, and both indexes.

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'is_template')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD is_template BIT NOT NULL CONSTRAINT DF_RPT_saved_reports_is_template DEFAULT 0;
END
GO

-- Replace the composite (company_id, owner_id) index with the original three-column form.
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_saved_reports_company_owner'
             AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
    DROP INDEX IX_saved_reports_company_owner ON EMPOWER.RPT_saved_reports;
GO

CREATE INDEX IX_saved_reports_company_owner
    ON EMPOWER.RPT_saved_reports (company_id, owner_id, is_template);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_saved_reports_is_template'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
    CREATE INDEX IX_saved_reports_is_template
        ON EMPOWER.RPT_saved_reports (is_template);
GO
