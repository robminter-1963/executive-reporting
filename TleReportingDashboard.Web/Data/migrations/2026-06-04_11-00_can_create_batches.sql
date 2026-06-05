-- Per-user permission to author report batches. Admins grant this on
-- the user editor; users with it can create / edit / delete / grant
-- access on batches THEY own (created_by = their email). Admins still
-- have full CRUD on every batch; users with neither this flag nor any
-- access grants see no Batches tab at all.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('EMPOWER.RPT_users')
       AND name = 'can_create_batches')
BEGIN
    ALTER TABLE EMPOWER.RPT_users
      ADD can_create_batches BIT NOT NULL CONSTRAINT DF_RPT_users_can_create_batches DEFAULT (0);
END;
GO
