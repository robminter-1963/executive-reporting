namespace TleReportingDashboard.Web.Services;

public interface IFieldReferenceService
{
    // Rewrites every stored JSON reference from oldFieldId to newFieldId across
    // saved reports and grid templates (field id strings in arrays, as dictionary
    // keys, and as scalar values inside column_state / dashboard config).
    // Returns the number of rows updated.
    Task<int> RenameAsync(string oldFieldId, string newFieldId);
}
