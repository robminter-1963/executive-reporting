using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// Named constants for the SQL Server error numbers we react to in
// catch blocks. Originally these were spelled out as magic numbers
// (208, 2601, 2627) at ~13 different sites — easy to typo, hard to
// search, no place to add 547 (FK violation) when the next case
// arrives. One file, one definition.
public static class SqlErrorCodes
{
    // Invalid object name — the named table/view doesn't exist. We
    // typically catch this on the read path of a feature whose
    // migration may not have been applied yet (defensive fallback to
    // "empty/default" instead of surfacing the SQL error).
    public const int ObjectMissing = 208;

    // Unique-index violation. Two flavors, same intent — a UNIQUE
    // constraint was hit. SQL Server distinguishes them historically
    // but we treat them identically.
    public const int UniqueIndexViolation = 2601;
    public const int UniqueConstraintViolation = 2627;

    // FOREIGN KEY violation. Not yet used in catch blocks but listed
    // here so it's the canonical home when it lands.
    public const int ForeignKeyViolation = 547;
}

public static class SqlExceptionExtensions
{
    // True when the exception is a duplicate-key violation under either
    // of SQL Server's two error-code flavors. Use:
    //     catch (SqlException ex) when (ex.IsDuplicateKey()) { ... }
    public static bool IsDuplicateKey(this SqlException ex) =>
        ex.Number is SqlErrorCodes.UniqueIndexViolation
                  or SqlErrorCodes.UniqueConstraintViolation;

    // True when the exception is "table/view doesn't exist." Used by
    // services to fall through to an empty result on a pre-migration
    // env without surfacing the SQL error to the caller.
    public static bool IsObjectMissing(this SqlException ex) =>
        ex.Number == SqlErrorCodes.ObjectMissing;
}
