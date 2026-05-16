namespace TleReportingDashboard.Web.Services;

// Single MIME-type map used by both the browser-download path (Export
// menu / Print menu in the Web app) and the email-attachment path (the
// Worker's scheduled-report sender). Previously each side had its own
// inline lookup — the EmailService version was complete, the browser-
// side callsites hard-coded the openxmlformats string repeatedly with
// occasional typos. One file = one source of truth.
public static class MimeTypes
{
    // Returns the standard MIME type for a filename's extension.
    // Unknown extensions fall back to application/octet-stream so the
    // browser still offers "Save as" rather than trying to inline an
    // unknown payload.
    public static string For(string fileNameOrExt)
    {
        var ext = fileNameOrExt.StartsWith('.')
            ? fileNameOrExt.ToLowerInvariant()
            : Path.GetExtension(fileNameOrExt).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls"  => "application/vnd.ms-excel",
            ".csv"  => "text/csv",
            ".pdf"  => "application/pdf",
            ".txt"  => "text/plain",
            ".json" => "application/json",
            ".html" => "text/html",
            _       => "application/octet-stream"
        };
    }
}
