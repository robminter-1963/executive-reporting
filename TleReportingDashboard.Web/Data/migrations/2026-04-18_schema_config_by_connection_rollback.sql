-- Rollback for 2026-04-18_schema_config_by_connection.sql
-- Restores the company_id-keyed PK shape. company_id was never dropped, so
-- we just put everything back the way it was.

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_schema_config_history_connection_updated'
             AND object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history'))
    DROP INDEX IX_schema_config_history_connection_updated ON EMPOWER.RPT_schema_config_history;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_history_connection')
    ALTER TABLE EMPOWER.RPT_schema_config_history DROP CONSTRAINT FK_schema_config_history_connection;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history') AND name = 'connection_id')
    ALTER TABLE EMPOWER.RPT_schema_config_history DROP COLUMN connection_id;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_connection')
    ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT FK_schema_config_connection;
GO

IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config')
             AND type = 'PK')
    ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT PK_schema_config;
GO

-- Re-establish company_id PK (it was left in place by the forward migration)
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config')
                 AND type = 'PK')
    ALTER TABLE EMPOWER.RPT_schema_config ADD CONSTRAINT PK_schema_config PRIMARY KEY (company_id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_company')
    ALTER TABLE EMPOWER.RPT_schema_config
        ADD CONSTRAINT FK_schema_config_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config') AND name = 'connection_id')
    ALTER TABLE EMPOWER.RPT_schema_config DROP COLUMN connection_id;
GO
