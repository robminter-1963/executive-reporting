using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace TleReportingDashboard.Web.Services;

public record DbTableInfo(string SchemaName, string TableName, string FullName);
public record DbColumnInfo(string ColumnName, string DataType, int? MaxLength, int? Precision, int? Scale, bool IsNullable);
public record DbCodeSet(int CodeId, string Description);

// Introspects a data-source connection's schema: tables, columns, row
// counts, code sets. Dispatches by the connection's connection_type so it
// hits INFORMATION_SCHEMA on both SQL Server and Postgres without choking
// on provider-specific keywords.
public class SchemaBuilderService
{
    private readonly ICompanyConnectionResolver _connectionResolver;
    private readonly ICompanyConnectionAdminService _connectionAdmin;
    private readonly ILogger<SchemaBuilderService> _logger;

    public SchemaBuilderService(
        ICompanyConnectionResolver connectionResolver,
        ICompanyConnectionAdminService connectionAdmin,
        ILogger<SchemaBuilderService> logger)
    {
        _connectionResolver = connectionResolver;
        _connectionAdmin = connectionAdmin;
        _logger = logger;
    }

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

    public async Task<int> GetRowCountAsync(Guid? connectionId, string schemaName, string tableName)
    {
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
