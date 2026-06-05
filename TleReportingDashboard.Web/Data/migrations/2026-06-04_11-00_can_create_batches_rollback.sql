-- Rollback for 2026-06-04_11-00_can_create_batches.sql

IF EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('EMPOWER.RPT_users')
       AND name = 'can_create_batches')
BEGIN
    -- Default constraint must drop before the column can be removed.
    DECLARE @df NVARCHAR(256) = (
        SELECT dc.name FROM sys.default_constraints dc
         INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
         WHERE c.object_id = OBJECT_ID('EMPOWER.RPT_users')
           AND c.name = 'can_create_batches');
    IF @df IS NOT NULL
        EXEC('ALTER TABLE EMPOWER.RPT_users DROP CONSTRAINT ' + @df);
    ALTER TABLE EMPOWER.RPT_users DROP COLUMN can_create_batches;
END;
GO
