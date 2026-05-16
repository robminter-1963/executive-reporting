using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// Shared "read a column that might be NULL or missing" helpers for
// SqlDataReader. Replaces three local copies of the same logic that
// drifted across services (GetNullableString / TryGetOptionalString /
// TryGetString — same body, three names) and consolidates the
// IndexOutOfRangeException swallow that lets us tolerate a pre-migration
// schema (column not in the SELECT result set).
//
// Conventions:
//   * Opt* methods accept a column NAME — they swallow IndexOutOfRange
//     so a missing column returns null instead of throwing. Use them
//     when the SELECT references a column that may not exist yet on
//     pre-migration environments (defensive read).
//   * Callers that always SELECT the column by name can still use
//     reader.IsDBNull(reader.GetOrdinal(name)) directly — these helpers
//     just trade two lines for one with an explicit null contract.
public static class SqlReaderExtensions
{
    public static string? OptString(this SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : r.GetString(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    public static Guid? OptGuid(this SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : r.GetGuid(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    public static int? OptInt(this SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : r.GetInt32(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    public static DateTime? OptDate(this SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : r.GetDateTime(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    public static bool? OptBool(this SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : r.GetBoolean(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    public static byte[]? OptBytes(this SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : (byte[])r.GetValue(ord);
        }
        catch (IndexOutOfRangeException) { return null; }
    }
}
