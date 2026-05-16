using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// Thin helpers for the ConfigDB ADO.NET pattern that every RPT_*
// service repeats hundreds of times. Centralizes:
//
//   * Opening a connection: `await using var conn = await
//     ConfigDb.OpenAsync(_connStr, ct);`
//   * Building a command: `await using var cmd = conn.Cmd(sql);`
//   * Binding nullable parameters without the `(object?)x ?? DBNull.Value`
//     dance: `cmd.AddParam("@email", email);`
//
// These are intentionally tiny — no IDbExecutor abstraction, no
// generic mapper. Just removes the boilerplate without hiding the SQL.
public static class ConfigDb
{
    // Opens and awaits a new SqlConnection. Caller owns the lifetime
    // (`await using`) — this helper exists only to collapse the
    // ubiquitous `new SqlConnection + OpenAsync` two-liner.
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // Builds a SqlCommand on this connection. Optional transaction lets
    // multi-statement units of work share one tx without the caller
    // re-wiring the parameters list. Returned command is uninitialized
    // beyond SQL + connection + tx — caller binds parameters via AddParam.
    public static SqlCommand Cmd(this SqlConnection conn, string sql, SqlTransaction? tx = null) =>
        new(sql, conn, tx);

    // Adds a parameter with automatic null coalesce to DBNull. Replaces
    // the ~400-site `cmd.Parameters.Add(new SqlParameter("@x",
    // (object?)x ?? DBNull.Value))` pattern with one method call.
    // Returns the added SqlParameter so the rare caller that needs to
    // tweak SqlDbType / Size after the fact still can.
    public static SqlParameter AddParam(this SqlCommand cmd, string name, object? value) =>
        cmd.Parameters.Add(new SqlParameter(name, value ?? DBNull.Value));

    // Adds a parameter with an explicit DbType + size. Used where the
    // implicit type inference burned us before (cache poisoning from
    // ad-hoc parameter shapes — see UserManagementService for context).
    // Same null-coalesce semantics as the simple overload.
    public static SqlParameter AddParam(this SqlCommand cmd, string name,
                                         System.Data.SqlDbType type, int size, object? value)
    {
        var p = new SqlParameter(name, type, size) { Value = value ?? DBNull.Value };
        return cmd.Parameters.Add(p);
    }
}
