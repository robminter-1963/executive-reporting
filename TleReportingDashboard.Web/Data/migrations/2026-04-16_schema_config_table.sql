-- Adds the DB-backed schema config store. Idempotent.
-- Run once per environment; the app auto-seeds from schema_config.json on
-- the first request if the row is missing.

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_schema_config')
BEGIN
    CREATE TABLE EMPOWER.RPT_schema_config (
        id          INT              NOT NULL PRIMARY KEY,
        json        NVARCHAR(MAX)    NOT NULL,
        updated_by  NVARCHAR(256)    NULL,
        updated_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_schema_config_singleton CHECK (id = 1)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_schema_config_history')
BEGIN
    CREATE TABLE EMPOWER.RPT_schema_config_history (
        history_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
        json         NVARCHAR(MAX)    NOT NULL,
        updated_by   NVARCHAR(256)    NULL,
        updated_at   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_schema_config_history_updated_at
        ON EMPOWER.RPT_schema_config_history (updated_at DESC);
END
GO
