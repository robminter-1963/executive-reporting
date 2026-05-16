using Microsoft.JSInterop;

namespace TleReportingDashboard.Web.Services;

// Single seam for the browser-download interop. Previously every export
// handler called `JS.InvokeVoidAsync("downloadFile", fileName,
// Convert.ToBase64String(bytes), mimeType)` and hard-coded the MIME
// string at the call site — ~14 sites, three opportunities to typo the
// openxmlformats string. Now: `await JS.DownloadAsync(fileName, bytes);`
// — MIME is inferred from extension via MimeTypes.For.
public static class JSRuntimeExtensions
{
    public static ValueTask DownloadAsync(this IJSRuntime js, string fileName, byte[] bytes)
        => js.InvokeVoidAsync("downloadFile", fileName,
                              Convert.ToBase64String(bytes),
                              MimeTypes.For(fileName));
}
