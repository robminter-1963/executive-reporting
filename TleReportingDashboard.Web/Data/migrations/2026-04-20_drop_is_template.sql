-- ============================================================================
-- Drop is_template from RPT_saved_reports
--
-- Report templates (starter reports) were historically stored as rows in
-- RPT_saved_reports with is_template = 1 and a separate "Templates" section
-- in the Report Library. That model collided with the Grid Templates feature
-- (RPT_grid_templates) — users expected one "template" concept, not two.
--
-- Going forward, templates live exclusively in RPT_grid_templates. Any rows
-- here with is_template = 1 become regular reports.
--
-- The column has a system-named DEFAULT constraint and is used by two indexes,
-- both of which must be dropped before ALTER TABLE DROP COLUMN will succeed.
-- The (company_id, owner_id) composite index is recreated without is_template.
-- ============================================================================

-- 1. Drop the DEFAULT constraint (auto-named, lookup via sys.default_constraints).
DECLARE @DefaultConstraintName SYSNAME = (
    SELECT dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.default_object_id = dc.object_id
    WHERE dc.parent_object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
      AND c.name = 'is_template'
);

IF @DefaultConstraintName IS NOT NULL
BEGIN
    DECLARE @Sql NVARCHAR(200) =
        N'ALTER TABLE EMPOWER.RPT_saved_reports DROP CONSTRAINT ' + QUOTENAME(@DefaultConstraintName);
    EXEC sp_executesql @Sql;
END
GO

-- 2. Drop the single-column index on is_template.
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_saved_reports_is_template'
             AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
    DROP INDEX IX_saved_reports_is_template ON EMPOWER.RPT_saved_reports;
GO

-- 3. Drop the composite index that includes is_template, then recreate it
--    without that column so (company_id, owner_id) lookups stay indexed.
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_saved_reports_company_owner'
             AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
    DROP INDEX IX_saved_reports_company_owner ON EMPOWER.RPT_saved_reports;
GO

-- 4. Drop the column.
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
             AND name = 'is_template')
    ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN is_template;
GO

-- 5. Recreate the composite index without is_template.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_saved_reports_company_owner'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
    CREATE INDEX IX_saved_reports_company_owner
        ON EMPOWER.RPT_saved_reports (company_id, owner_id);
GO
