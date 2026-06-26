-- Revert FKs widened by 2026-06-04_12-00_company_delete_cascades.sql
-- back to ON DELETE NO ACTION.

IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
     WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_user_preferences')
       AND name = 'FK_RPT_user_preferences_companies')
BEGIN
    ALTER TABLE EMPOWER.RPT_user_preferences DROP CONSTRAINT FK_RPT_user_preferences_companies;
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD CONSTRAINT FK_RPT_user_preferences_companies
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id) ON DELETE NO ACTION;
END;
GO

IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
     WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history')
       AND name = 'FK_RPT_schema_config_history_companies')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config_history DROP CONSTRAINT FK_RPT_schema_config_history_companies;
    ALTER TABLE EMPOWER.RPT_schema_config_history
        ADD CONSTRAINT FK_RPT_schema_config_history_companies
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id) ON DELETE NO ACTION;
END;
GO
