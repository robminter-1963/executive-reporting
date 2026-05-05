namespace TleReportingDashboard.Web.Services.Promotion;

// Builds and consumes promotion packages — the staging→production
// transport mechanism. Two-instance model: Staging exports a JSON
// package; Production imports it.
//
// Why a file-based bundle instead of a live DB-to-DB push: keeps the
// two environments isolated (no prod credentials on staging), gives
// admins a reviewable artifact, and lets the import preview show
// exactly what will change before anything writes.
public interface IPromotionPackageService
{
    // Serializes the requested schema configs into a single package
    // tagged with this instance's environment label. Returns the JSON
    // bytes ready to stream as a file download.
    Task<byte[]> ExportAsync(
        IReadOnlyList<Guid> schemaConfigConnectionIds,
        string? exportedBy,
        string? notes,
        CancellationToken ct = default);

    // Parses a package's bytes without applying anything. Used by the
    // import UI to render a preview (entry counts, source env, who
    // exported, when) before the admin confirms.
    PromotionPackage Parse(byte[] packageBytes);

    // Applies one schema-config entry against a target connection in
    // this instance's ConfigDb. Each entry is imported in isolation so
    // a single failed mapping doesn't roll back the rest. Returns a
    // human-readable result the UI can show in a per-entry status row.
    Task<ImportResult> ImportSchemaConfigAsync(
        PromotionPackage.SchemaConfigEntry entry,
        Guid targetConnectionId,
        string? importedBy,
        CancellationToken ct = default);
}

public sealed record ImportResult(bool Success, string Message);
