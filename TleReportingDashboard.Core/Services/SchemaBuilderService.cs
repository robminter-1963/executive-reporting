using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public record DbTableInfo(string SchemaName, string TableName, string FullName);
public record DbColumnInfo(string ColumnName, string DataType, int? MaxLength, int? Precision, int? Scale, bool IsNullable);
public record DbCodeSet(int CodeId, string Description);

// Provider-agnostic join hint surfaced by Schema Builder. Today it's
// populated only from Dataverse relationship metadata; SQL Server /
// Postgres FK discovery (via REFERENTIAL_CONSTRAINTS) would slot in
// behind the same shape later.
//
//   SourceTable = entity holding the FK (e.g. "contact")
//   SourceColumn = FK attribute on SourceTable (e.g. "parentcustomerid")
//   TargetTable = referenced entity (e.g. "account")
//   TargetColumn = primary key on TargetTable (e.g. "accountid")
//   Direction = "many-to-one" or "one-to-many" — same SQL shape, different
//               admin-facing wording in any UI that surfaces it
//   RelationshipName = stable id from the provider, useful as JoinDefinition.Id
public record SuggestedJoin(string SourceTable, string SourceColumn,
                            string TargetTable, string TargetColumn,
                            string Direction, string RelationshipName);

// Introspects a data-source connection's schema: tables, columns, row
// counts, code sets. Dispatches by the connection's connection_type so it
// hits INFORMATION_SCHEMA on both SQL Server and Postgres, and the
// Metadata API on Dataverse, without choking on provider-specific keywords.
public class SchemaBuilderService
{
    private readonly ICompanyConnectionResolver _connectionResolver;
    private readonly ICompanyConnectionAdminService _connectionAdmin;
    private readonly DataverseSchemaClient _dataverse;
    private readonly ILogger<SchemaBuilderService> _logger;

    public SchemaBuilderService(
        ICompanyConnectionResolver connectionResolver,
        ICompanyConnectionAdminService connectionAdmin,
        DataverseSchemaClient dataverse,
        ILogger<SchemaBuilderService> logger)
    {
        _connectionResolver = connectionResolver;
        _connectionAdmin = connectionAdmin;
        _dataverse = dataverse;
        _logger = logger;
    }

    // Resolves the connection record without trying to build a SQL/Pg
    // connection string — Dataverse rows would throw inside
    // CompanyConnectionStringBuilder.Build, so callers that only need the
    // type (or that route to Dataverse) shouldn't go through the resolver.
    private async Task<CompanyConnectionRecord> ResolveRecordAsync(Guid connectionId)
    {
        return await _connectionAdmin.GetByIdAsync(connectionId)
            ?? throw new InvalidOperationException($"Connection {connectionId} not found.");
    }

    private static bool IsDataverse(string? connectionType) =>
        string.Equals(connectionType, "dataverse", StringComparison.OrdinalIgnoreCase);

    // Returns (type, connectionString, tableFilterSql, schemaFilterSql) for a
    // given connection id. Both filters are free-form WHERE fragments the
    // admin can set per connection. Falls back to SQL Server / TLE primary
    // when no id is supplied (the one remaining place where "Current" is legal).
    private async Task<(string Type, string ConnStr, string? TableFilter, string? SchemaFilter)> ResolveContextAsync(Guid? connectionId)
    {
        if (connectionId is Guid id)
        {
            var record = await _connectionAdmin.GetByIdAsync(id)
                ?? throw new InvalidOperationException($"Connection {id} not found.");
            var connStr = await _connectionResolver.GetByIdAsync(id);
            return (record.ConnectionType, connStr, record.TableFilterSql, record.SchemaFilterSql);
        }
        // No id supplied — fall back to the registry-wide default connection
        // so legacy call sites still resolve without a hardcoded company.
        var fallback = await _connectionResolver.GetDefaultConnectionStringAsync();
        return ("sqlserver", fallback, null, null);
    }

    // Opens a provider-appropriate DbConnection for the given connection id.
    // Caller owns disposal.
    private static DbConnection CreateConnection(string type, string connStr) =>
        type.ToLowerInvariant() switch
        {
            "sqlserver" => new SqlConnection(connStr),
            "postgres"  => new NpgsqlConnection(connStr),
            var other   => throw new InvalidOperationException($"Unsupported connection_type '{other}'.")
        };

    // Creates a command+parameter with a provider-appropriate @-style prefix.
    // SqlClient and Npgsql both accept "@name" so a single helper works.
    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public async Task<List<DbTableInfo>> GetTablesAsync(Guid? connectionId, string? filter = null)
    {
        // Dataverse routes to the Metadata API — no SQL connection string,
        // no INFORMATION_SCHEMA. Entity LogicalName is the closest analog
        // to a table name; we surface it as the TableName and use the
        // schema slot for "dataverse" so existing call sites that key off
        // (schema, table) pairs stay legible.
        if (connectionId is Guid dvId)
        {
            var dvRecord = await ResolveRecordAsync(dvId);
            if (IsDataverse(dvRecord.ConnectionType))
            {
                var entities = await _dataverse.GetEntitiesAsync(dvRecord);
                IEnumerable<DataverseSchemaClient.DvEntity> filtered = entities;
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    filtered = filtered.Where(e =>
                        e.LogicalName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        e.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));
                }
                return filtered
                    .Select(e => new DbTableInfo("dataverse", e.LogicalName, e.LogicalName))
                    .ToList();
            }
        }

        var (type, connStr, tableFilter, schemaFilter) = await ResolveContextAsync(connectionId);

        var tables = new List<DbTableInfo>();
        await using var conn = CreateConnection(type, connStr);
        await conn.OpenAsync();

        // INFORMATION_SCHEMA.TABLES exists on both — the only dialect quirk
        // is case: SQL Server accepts uppercase identifiers, Postgres wants
        // lowercase. We use uppercase (INFORMATION_SCHEMA.TABLES), which
        // both resolve via their catalog normalization rules.
        // Schema and table filters are applied independently so admins can
        // narrow on both axes (e.g. "TABLE_SCHEMA = 'EMPOWER'" +
        // "TABLE_NAME LIKE 'LN[_]%'").
        var sql = @"SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_TYPE = 'BASE TABLE'";
        if (!string.IsNullOrWhiteSpace(schemaFilter))
            sql += $" AND ({schemaFilter})";
        if (!string.IsNullOrWhiteSpace(tableFilter))
            sql += $" AND ({tableFilter})";
        if (!string.IsNullOrWhiteSpace(filter))
            sql += " AND TABLE_NAME LIKE @Filter";
        sql += " ORDER BY TABLE_SCHEMA, TABLE_NAME";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (!string.IsNullOrWhiteSpace(filter))
            AddParam(cmd, "@Filter", $"%{filter}%");

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            tables.Add(new DbTableInfo(schema, table, $"{schema}.{table}"));
        }
        return tables;
    }

    public async Task<List<DbColumnInfo>> GetColumnsAsync(Guid? connectionId, string? schemaName, string tableName)
    {
        // Dataverse path: pull attributes via the Metadata API and map them
        // into DbColumnInfo so the rest of the schema editor flow (column
        // picker, autocomplete, type inference) is provider-agnostic.
        if (connectionId is Guid dvId)
        {
            var dvRecord = await ResolveRecordAsync(dvId);
            if (IsDataverse(dvRecord.ConnectionType))
            {
                var attrs = await _dataverse.GetAttributesAsync(dvRecord, tableName);
                return attrs
                    .Select(a => new DbColumnInfo(
                        ColumnName: a.LogicalName,
                        DataType: a.AttributeType,
                        MaxLength: null,
                        Precision: null,
                        Scale: null,
                        IsNullable: !a.IsRequired))
                    .ToList();
            }
        }

        var (type, connStr, _, _) = await ResolveContextAsync(connectionId);

        var columns = new List<DbColumnInfo>();
        await using var conn = CreateConnection(type, connStr);
        await conn.OpenAsync();

        // Bare table names (no schema supplied) are a legitimate case when
        // an alias entry was saved without its schema prefix. Drop the
        // TABLE_SCHEMA filter in that case — returns every schema's copy
        // of the table, which is the most permissive thing we can do.
        await using var cmd = conn.CreateCommand();
        var hasSchema = !string.IsNullOrWhiteSpace(schemaName);
        cmd.CommandText = hasSchema
            ? @"SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table
                ORDER BY ORDINAL_POSITION"
            : @"SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @Table
                ORDER BY TABLE_SCHEMA, ORDINAL_POSITION";
        if (hasSchema) AddParam(cmd, "@Schema", schemaName!);
        AddParam(cmd, "@Table", tableName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new DbColumnInfo(
                reader.GetString(0),
                reader.GetString(1),
                // NUMERIC_PRECISION and MaxLength come back as differently-sized
                // integer types across providers (tinyint in SQL Server,
                // smallint in Postgres). GetValue + convert is more robust
                // than type-specific Getters.
                reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                reader.GetString(5) == "YES"
            ));
        }
        return columns;
    }

    /// <summary>
    /// Returns the set of column names that are uniquely keyed on a single
    /// column (primary key or single-column UNIQUE constraint). Used to
    /// flag eligible "Then By" tiebreaker picks in the Report Builder so
    /// admins can see which columns make OFFSET pagination deterministic.
    /// Composite (multi-column) unique constraints aren't included — none
    /// of their member columns are unique on their own.
    /// Both Postgres and SQL Server expose this via information_schema.
    /// SQL Server's UNIQUE INDEXES (not constraints) won't appear; for
    /// most reporting use cases the constraint coverage is sufficient.
    /// </summary>
    public async Task<HashSet<string>> GetUniqueColumnNamesAsync(
        Guid? connectionId, string? schemaName, string tableName)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Dataverse uniqueness derives from IsPrimaryId on attributes, not
        // from a constraint catalog. We surface that single column so the
        // OFFSET-pagination tiebreaker logic still has something stable
        // to fall back on. Multi-column uniqueness isn't a Dataverse
        // concept at the schema layer.
        if (connectionId is Guid dvId)
        {
            var dvRecord = await ResolveRecordAsync(dvId);
            if (IsDataverse(dvRecord.ConnectionType))
            {
                var attrs = await _dataverse.GetAttributesAsync(dvRecord, tableName);
                foreach (var a in attrs.Where(a => a.IsPrimaryId))
                    unique.Add(a.LogicalName);
                return unique;
            }
        }

        var (type, connStr, _, _) = await ResolveContextAsync(connectionId);

        await using var conn = CreateConnection(type, connStr);
        await conn.OpenAsync();

        var hasSchema = !string.IsNullOrWhiteSpace(schemaName);
        // CTE picks single-column constraints, then joins back to surface
        // the column name. Schema-optional so callers that store bare
        // table names (legacy aliases) still get a result.
        var sql = hasSchema
            ? @"WITH single_col AS (
                    SELECT constraint_name, table_schema, table_name
                    FROM information_schema.key_column_usage
                    WHERE table_schema = @Schema AND table_name = @Table
                    GROUP BY constraint_name, table_schema, table_name
                    HAVING COUNT(*) = 1
                )
                SELECT DISTINCT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                    AND tc.table_schema = kcu.table_schema
                    AND tc.table_name = kcu.table_name
                JOIN single_col sc
                    ON sc.constraint_name = tc.constraint_name
                    AND sc.table_schema = tc.table_schema
                    AND sc.table_name = tc.table_name
                WHERE tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE')
                    AND tc.table_schema = @Schema
                    AND tc.table_name = @Table"
            : @"WITH single_col AS (
                    SELECT constraint_name, table_schema, table_name
                    FROM information_schema.key_column_usage
                    WHERE table_name = @Table
                    GROUP BY constraint_name, table_schema, table_name
                    HAVING COUNT(*) = 1
                )
                SELECT DISTINCT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                    AND tc.table_schema = kcu.table_schema
                    AND tc.table_name = kcu.table_name
                JOIN single_col sc
                    ON sc.constraint_name = tc.constraint_name
                    AND sc.table_schema = tc.table_schema
                    AND sc.table_name = tc.table_name
                WHERE tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE')
                    AND tc.table_name = @Table";

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (hasSchema) AddParam(cmd, "@Schema", schemaName!);
            AddParam(cmd, "@Table", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                unique.Add(reader.GetString(0));
            }
        }
        catch
        {
            // Non-fatal — if information_schema isn't accessible (rare but
            // possible on locked-down accounts) the marker just won't show.
        }
        return unique;
    }

    /// <summary>
    /// Returns the primary-key columns of a target-DB table in PK ordinal
    /// order. Composite PKs are returned in their declared order. Used by
    /// the Report Builder to inject a stable ORDER BY behind the scenes
    /// when no user sort is configured — keeps OFFSET pagination
    /// deterministic even on Postgres without forcing the admin to pick
    /// a tiebreaker manually.
    /// </summary>
    public async Task<List<string>> GetPrimaryKeyColumnsAsync(
        Guid? connectionId, string? schemaName, string tableName)
    {
        var pks = new List<string>();
        // Dataverse: the primary id is whichever attribute has IsPrimaryId=true.
        // Single-column always — no composite PK concept at this level.
        if (connectionId is Guid dvId)
        {
            var dvRecord = await ResolveRecordAsync(dvId);
            if (IsDataverse(dvRecord.ConnectionType))
            {
                var attrs = await _dataverse.GetAttributesAsync(dvRecord, tableName);
                pks.AddRange(attrs.Where(a => a.IsPrimaryId).Select(a => a.LogicalName));
                return pks;
            }
        }

        var (type, connStr, _, _) = await ResolveContextAsync(connectionId);

        await using var conn = CreateConnection(type, connStr);
        await conn.OpenAsync();

        var hasSchema = !string.IsNullOrWhiteSpace(schemaName);
        var sql = hasSchema
            ? @"SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                    AND tc.table_schema = kcu.table_schema
                    AND tc.table_name = kcu.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                    AND tc.table_schema = @Schema
                    AND tc.table_name = @Table
                ORDER BY kcu.ordinal_position"
            : @"SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                    AND tc.table_schema = kcu.table_schema
                    AND tc.table_name = kcu.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                    AND tc.table_name = @Table
                ORDER BY kcu.ordinal_position";

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (hasSchema) AddParam(cmd, "@Schema", schemaName!);
            AddParam(cmd, "@Table", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pks.Add(reader.GetString(0));
            }
        }
        catch
        {
            // Non-fatal — table may have no PK or information_schema may
            // be locked down. Caller treats empty as "no auto-fallback".
        }
        return pks;
    }

    /// <summary>
    /// Same as <see cref="GetPrimaryKeyColumnsAsync"/>, but also folds in
    /// admin-asserted IsUnique fields whose SourceColumn lives on the same
    /// table. Designed for the OFFSET pagination tiebreaker path: when the
    /// connection's account can't read information_schema constraints, the
    /// live lookup returns empty and the admin's manual flag carries the
    /// fallback. When the lookup succeeds, IsUnique fields supplement it
    /// (e.g. UNIQUE INDEXES that don't surface as constraints) without
    /// duplicating columns. Composite PKs still depend on the live lookup
    /// since the admin flag is single-column. Caller supplies the schema's
    /// FieldConfigs so this stays free of an ISchemaService dependency
    /// (avoids a circular service graph).
    /// </summary>
    public async Task<List<string>> GetEffectivePrimaryKeyColumnsAsync(
        Guid? connectionId, string? schemaName, string tableName,
        IEnumerable<FieldConfig> schemaFields)
    {
        var pks = await GetPrimaryKeyColumnsAsync(connectionId, schemaName, tableName);
        if (schemaFields is null) return pks;

        foreach (var f in schemaFields)
        {
            if (!f.IsUnique) continue;
            if (!string.IsNullOrWhiteSpace(f.SqlExpression)) continue;
            if (string.IsNullOrWhiteSpace(f.SourceColumn)) continue;
            // Match the field's SourceTable against the (schema, table)
            // pair we were called with. Admins write SourceTable as either
            // "schema.table" or bare "table" — accept both, plus a suffix
            // match so "EMPOWER.LOANS" matches a callsite that resolved
            // schema=EMPOWER and table=LOANS.
            var src = f.SourceTable ?? string.Empty;
            var qualified = string.IsNullOrWhiteSpace(schemaName)
                ? tableName
                : $"{schemaName}.{tableName}";
            var matches =
                string.Equals(src, qualified, StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith("." + tableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(src, tableName, StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;
            if (!pks.Any(c => string.Equals(c, f.SourceColumn, StringComparison.OrdinalIgnoreCase)))
                pks.Add(f.SourceColumn);
        }
        return pks;
    }

    public async Task<List<DbCodeSet>> GetCodeSetsAsync(Guid? connectionId)
    {
        // SET_CODESETS is Empower-specific (SQL Server). Return empty for
        // non-SQL-Server connections rather than blowing up.
        var (type, connStr, _, _) = await ResolveContextAsync(connectionId);
        if (!string.Equals(type, "sqlserver", StringComparison.OrdinalIgnoreCase))
            return new List<DbCodeSet>();

        var sets = new List<DbCodeSet>();
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"SELECT DISTINCT CODEID, MIN(CODEDESC) AS SampleDesc
              FROM EMPOWER.SET_CODESETS WHERE ISACTIVE = 'Y'
              GROUP BY CODEID ORDER BY CODEID", conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sets.Add(new DbCodeSet(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        return sets;
    }

    public async Task<List<string>> GetCodeSetValuesPreviewAsync(Guid? connectionId, int codeSetId)
    {
        var (type, connStr, _, _) = await ResolveContextAsync(connectionId);
        if (!string.Equals(type, "sqlserver", StringComparison.OrdinalIgnoreCase))
            return new List<string>();

        var values = new List<string>();
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "SELECT CODEDESC FROM EMPOWER.SET_CODESETS WHERE CODEID = @Id AND ISACTIVE = 'Y' ORDER BY CODEDESC", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", codeSetId));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) values.Add(reader.GetString(0).Trim());
        }
        return values;
    }

    /// <summary>
    /// Returns join hints for an entity/table — relationships where the
    /// entity participates as either the FK holder (many-to-one) or the
    /// parent (one-to-many). For Dataverse, sourced from the Metadata API's
    /// relationship endpoints. For SQL Server / Postgres, returns an empty
    /// list today; FK-catalog discovery can populate this same shape later
    /// without changing callers.
    /// </summary>
    public async Task<List<SuggestedJoin>> GetSuggestedJoinsAsync(Guid? connectionId, string tableName)
    {
        if (connectionId is not Guid id || string.IsNullOrWhiteSpace(tableName))
            return new List<SuggestedJoin>();

        var record = await ResolveRecordAsync(id);
        if (!IsDataverse(record.ConnectionType))
            return new List<SuggestedJoin>();

        var m2o = await _dataverse.GetManyToOneRelationshipsAsync(record, tableName);
        var o2m = await _dataverse.GetOneToManyRelationshipsAsync(record, tableName);

        var joins = new List<SuggestedJoin>(m2o.Count + o2m.Count);
        foreach (var r in m2o)
        {
            joins.Add(new SuggestedJoin(
                SourceTable: r.ReferencingEntity,
                SourceColumn: r.ReferencingAttribute,
                TargetTable: r.ReferencedEntity,
                TargetColumn: r.ReferencedAttribute,
                Direction: "many-to-one",
                RelationshipName: r.SchemaName));
        }
        foreach (var r in o2m)
        {
            // Skip the inverse view of relationships we already captured
            // from the M:1 side — they describe the same FK pair.
            if (m2o.Any(x => string.Equals(x.SchemaName, r.SchemaName, StringComparison.OrdinalIgnoreCase)))
                continue;
            joins.Add(new SuggestedJoin(
                SourceTable: r.ReferencingEntity,
                SourceColumn: r.ReferencingAttribute,
                TargetTable: r.ReferencedEntity,
                TargetColumn: r.ReferencedAttribute,
                Direction: "one-to-many",
                RelationshipName: r.SchemaName));
        }
        return joins;
    }

    public async Task<int> GetRowCountAsync(Guid? connectionId, string schemaName, string tableName)
    {
        // Dataverse doesn't expose a cheap planner-style row estimate
        // analogous to sys.partitions / pg_class.reltuples. Returning 0
        // is the same fallback non-fatal-error path the SQL branches use
        // for locked-down accounts; admins can edit the row-count column
        // manually if they need it for budgeting decisions.
        if (connectionId is Guid dvId)
        {
            var dvRecord = await ResolveRecordAsync(dvId);
            if (IsDataverse(dvRecord.ConnectionType))
                return 0;
        }

        var (type, connStr, _, _) = await ResolveContextAsync(connectionId);

        // Two dialects, two fast-approximate strategies. Both avoid COUNT(*)
        // on large tables.
        if (string.Equals(type, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                @"SELECT SUM(p.rows) FROM sys.partitions p
                  INNER JOIN sys.tables t ON p.object_id = t.object_id
                  INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                  WHERE s.name = @Schema AND t.name = @Table AND p.index_id IN (0,1)", conn);
            cmd.Parameters.Add(new SqlParameter("@Schema", schemaName));
            cmd.Parameters.Add(new SqlParameter("@Table", tableName));
            var result = await cmd.ExecuteScalarAsync();
            return result is long l ? (int)l : 0;
        }
        else
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            // pg_class.reltuples is the planner's row-count estimate. Fast
            // and close enough for the "approximate row count" use.
            await using var cmd = new NpgsqlCommand(
                @"SELECT c.reltuples::bigint
                  FROM pg_class c
                  INNER JOIN pg_namespace n ON c.relnamespace = n.oid
                  WHERE n.nspname = @Schema AND c.relname = @Table AND c.relkind = 'r'", conn);
            cmd.Parameters.Add(new NpgsqlParameter("@Schema", schemaName));
            cmd.Parameters.Add(new NpgsqlParameter("@Table", tableName));
            var result = await cmd.ExecuteScalarAsync();
            return result is long l ? (int)Math.Min(l, int.MaxValue) : 0;
        }
    }

    public static string MapDbTypeToFieldDataType(string dbType) => dbType.ToLowerInvariant() switch
    {
        "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" => "text",
        // Postgres equivalents
        "character varying" or "character" => "text",
        "money" or "smallmoney" => "currency",
        "decimal" or "numeric" or "float" or "real" or "double precision" => "percent",
        "int" or "smallint" or "tinyint" or "bigint" or "integer" => "integer",
        "datetime" or "datetime2" or "date" or "smalldatetime" or "timestamp" or "timestamp without time zone" or "timestamp with time zone" => "date",
        "bit" or "boolean" => "boolean",
        // Dataverse AttributeType values — same canonical outputs as the
        // SQL Server / Postgres branches so downstream consumers don't
        // need to know which provider a column came from. Lookups /
        // owners / customers come back as text (the GUID id; the friendly
        // name lives on a related entity and isn't covered here).
        "string" or "memo" or "uniqueidentifier" or "lookup" or "owner" or "customer" or "entityname" => "text",
        "bigint_dv" or "biginteger" => "integer", // defensive — Dataverse uses BigInt; SQL bigint already maps above
        "double" => "percent",
        "picklist" or "state" or "status" or "multiselectpicklist" => "text",
        _ => "text"
    };

    public static int? ComputeMaxLength(DbColumnInfo col) => col.DataType.ToLowerInvariant() switch
    {
        "varchar" or "nvarchar" or "char" or "nchar" or "character varying" or "character" => col.MaxLength == -1 ? 255 : col.MaxLength,
        "money" or "smallmoney" => 15,
        "decimal" or "numeric" => col.Precision.HasValue ? col.Precision.Value + 2 : 10,
        "int" or "integer" => 10,
        "smallint" or "tinyint" => 5,
        "bigint" => 19,
        "datetime" or "datetime2" or "date" or "timestamp" or "timestamp without time zone" or "timestamp with time zone" => 10,
        "float" or "real" or "double precision" => 12,
        "bit" or "boolean" => 5,
        _ => null
    };
}
