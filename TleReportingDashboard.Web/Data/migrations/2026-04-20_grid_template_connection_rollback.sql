IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_grid_templates')
             AND name = 'connection_id')
    ALTER TABLE EMPOWER.RPT_grid_templates DROP COLUMN connection_id;
GO
