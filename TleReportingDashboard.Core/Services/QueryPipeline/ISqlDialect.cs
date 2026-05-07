using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

// Minimal dialect abstraction over the SQL differences between SQL Server
// and Postgres that the query pipeline actually cares about. Anything
// admin-authored in the schema (SqlExpression, SqlJoin, SqlPreamble) is
// already dialect-specific by virtue of being hand-written; the dialect
// interface only covers the bits the emitter generates on its own.
public interface ISqlDialect
{
    string Name { get; } // "sqlserver" | "postgres"

    // Quote an identifier used as a column alias in the SELECT list
    // (e.g. `AS [loan_number]` vs `AS "loan_number"`).
    string QuoteIdentifier(string name);

    // Prefix inserted directly after `SELECT` — for example `TOP(@Max) ` on
    // SQL Server. Empty on Postgres (which uses LIMIT at the end).
    string BuildRowLimitPrefix(string paramName);

    // Suffix appended after ORDER BY — for example ` LIMIT @Max` on
    // Postgres. Empty on SQL Server (which used TOP at the head).
    string BuildRowLimitSuffix(string paramName);

    // Create a provider-typed parameter. Both inherit from DbParameter so
    // callers can pass them to any DbCommand the matching connection owns.
    DbParameter CreateParameter(string name, object? value);

    // Open a DbConnection of the correct concrete type for this dialect.
    DbConnection CreateConnection(string connectionString);
}

public sealed class SqlServerDialect : ISqlDialect
{
    public string Name => "sqlserver";
    public string QuoteIdentifier(string name) => $"[{name}]";
    public string BuildRowLimitPrefix(string paramName) => $"TOP({paramName}) ";
    public string BuildRowLimitSuffix(string paramName) => string.Empty;
    public DbParameter CreateParameter(string name, object? value)
        => new SqlParameter(name, value ?? DBNull.Value);
    public DbConnection CreateConnection(string connectionString)
        => new SqlConnection(connectionString);
}

public sealed class PostgresDialect : ISqlDialect
{
    public string Name => "postgres";
    // Postgres is case-sensitive when identifiers are double-quoted.
    // Our column aliases come from our own field ids (lowercase by
    // convention), so quoting is safe and avoids keyword collisions.
    public string QuoteIdentifier(string name) => $"\"{name}\"";
    public string BuildRowLimitPrefix(string paramName) => string.Empty;
    public string BuildRowLimitSuffix(string paramName) => $" LIMIT {paramName}";
    public DbParameter CreateParameter(string name, object? value)
        => new NpgsqlParameter(name, value ?? DBNull.Value);
    public DbConnection CreateConnection(string connectionString)
        => new NpgsqlConnection(connectionString);
}

// Dataverse TDS endpoint speaks the SQL Server wire protocol — same
// SqlConnection / SqlParameter, same TOP for row limits, same bracket
// quoting. The dialect mainly exists so the rest of the pipeline can
// detect Dataverse via Name and short-circuit features the TDS endpoint
// doesn't support (no INSERT/UPDATE/DELETE, no OFFSET/FETCH paging,
// no temp tables, no stored procs, 5000-row implicit cap).
public sealed class DataverseTdsDialect : ISqlDialect
{
    public string Name => "dataverse";
    public string QuoteIdentifier(string name) => $"[{name}]";
    public string BuildRowLimitPrefix(string paramName) => $"TOP({paramName}) ";
    public string BuildRowLimitSuffix(string paramName) => string.Empty;
    public DbParameter CreateParameter(string name, object? value)
        => new SqlParameter(name, value ?? DBNull.Value);
    public DbConnection CreateConnection(string connectionString)
        => new SqlConnection(connectionString);
}

public interface ISqlDialectFactory
{
    ISqlDialect Get(string connectionType);
}

public sealed class SqlDialectFactory : ISqlDialectFactory
{
    private readonly ISqlDialect _sqlServer = new SqlServerDialect();
    private readonly ISqlDialect _postgres = new PostgresDialect();
    private readonly ISqlDialect _dataverse = new DataverseTdsDialect();

    public ISqlDialect Get(string connectionType) =>
        connectionType?.ToLowerInvariant() switch
        {
            "sqlserver" => _sqlServer,
            "postgres" => _postgres,
            "dataverse" => _dataverse,
            _ => throw new InvalidOperationException($"Unknown connection_type '{connectionType}'.")
        };
}
