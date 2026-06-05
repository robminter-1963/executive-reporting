-- Report Batches: admin-authored collections of reports (potentially across
-- multiple companies) packaged into a single multi-sheet Excel workbook.
-- Granted to specific users via RPT_report_batch_access; non-admin users
-- see only the batches they've been granted, with a Run button.
--
-- Three tables:
--   RPT_report_batches         — the batch definition (name, owner, audit cols)
--   RPT_report_batch_items     — the reports included in a batch, ordered
--   RPT_report_batch_access    — per-user grants for who can Run a batch

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_report_batches' AND schema_id = SCHEMA_ID('EMPOWER'))
BEGIN
    CREATE TABLE EMPOWER.RPT_report_batches (
        id              UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        name            NVARCHAR(200)    NOT NULL,
        description     NVARCHAR(1000)   NULL,
        created_at      DATETIME2        NOT NULL CONSTRAINT DF_RPT_report_batches_created_at DEFAULT (SYSUTCDATETIME()),
        created_by      NVARCHAR(256)    NULL,
        updated_at      DATETIME2        NOT NULL CONSTRAINT DF_RPT_report_batches_updated_at DEFAULT (SYSUTCDATETIME()),
        updated_by      NVARCHAR(256)    NULL
    );
END;
GO

-- One row per report included in a batch. CASCADE on batch_id so deleting a
-- batch drops all its items; CASCADE on report_id so deleting a report drops
-- it from every batch (matches the personal-tile cascade in the 2026-06-02
-- migration). sort_order drives the worksheet order in the output workbook.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_report_batch_items' AND schema_id = SCHEMA_ID('EMPOWER'))
BEGIN
    CREATE TABLE EMPOWER.RPT_report_batch_items (
        id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        batch_id        UNIQUEIDENTIFIER  NOT NULL,
        report_id       UNIQUEIDENTIFIER  NOT NULL,
        sort_order      INT               NOT NULL CONSTRAINT DF_RPT_report_batch_items_sort_order DEFAULT (0),
        sheet_name      NVARCHAR(31)      NULL, -- optional override; defaults to report.name truncated to 31 chars (Excel sheet-name limit)
        created_at      DATETIME2         NOT NULL CONSTRAINT DF_RPT_report_batch_items_created_at DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_RPT_report_batch_items_batch
            FOREIGN KEY (batch_id) REFERENCES EMPOWER.RPT_report_batches(id) ON DELETE CASCADE,
        CONSTRAINT FK_RPT_report_batch_items_report
            FOREIGN KEY (report_id) REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE
    );
    CREATE INDEX IX_RPT_report_batch_items_batch_sort
        ON EMPOWER.RPT_report_batch_items (batch_id, sort_order);
END;
GO

-- Per-user grant to Run a batch. user_email holds the login (preferred_username
-- claim) — matches the column used in RPT_report_shares.shared_with_id for
-- 'user' shares. UNIQUE(batch_id, user_email) prevents double-grants. CASCADE
-- on batch_id; soft-deletes via revoke (just drop the row).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_report_batch_access' AND schema_id = SCHEMA_ID('EMPOWER'))
BEGIN
    CREATE TABLE EMPOWER.RPT_report_batch_access (
        id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        batch_id        UNIQUEIDENTIFIER  NOT NULL,
        user_email      NVARCHAR(256)     NOT NULL,
        granted_at      DATETIME2         NOT NULL CONSTRAINT DF_RPT_report_batch_access_granted_at DEFAULT (SYSUTCDATETIME()),
        granted_by      NVARCHAR(256)     NULL,
        CONSTRAINT FK_RPT_report_batch_access_batch
            FOREIGN KEY (batch_id) REFERENCES EMPOWER.RPT_report_batches(id) ON DELETE CASCADE,
        CONSTRAINT UQ_RPT_report_batch_access
            UNIQUE (batch_id, user_email)
    );
    CREATE INDEX IX_RPT_report_batch_access_user
        ON EMPOWER.RPT_report_batch_access (user_email);
END;
GO
