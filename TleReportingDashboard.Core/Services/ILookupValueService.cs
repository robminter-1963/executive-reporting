namespace TleReportingDashboard.Web.Services;

// Fetches the value list for a LookupType (admin-authored SELECT in
// the Schema Builder Lookup Types tab). Used by HeaderFilters to
// populate a chip multi-select for fields that bind to a LookupType
// via FieldDefinition.LookupTypeId.
//
// Why a dedicated service rather than reusing CodeSetService: CodeSet
// is Empower-specific (hardwired to SET_CODESETS, default connection,
// CODEID-keyed). LookupTypes are per-connection and run admin-authored
// SELECTs against arbitrary tables — different cache shape, different
// connection-resolution path.
public interface ILookupValueService
{
    // Runs the LookupType's SelectSql against the connection's data DB
    // and returns (value, display) pairs. Unknown lookupTypeId returns
    // an empty list. Failures (broken SQL, missing table, connection
    // down) are logged and swallowed; the picker just renders empty
    // rather than crashing.
    Task<List<CodeSetValue>> GetFilterValuesAsync(
        Guid connectionId,
        string lookupTypeId,
        CancellationToken ct = default);
}
