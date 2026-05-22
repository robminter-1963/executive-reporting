namespace TleReportingDashboard.Web.Services;

// Per-connection saved root tables + aliases. Feeds the Report Builder's
// Primary Table dropdown so users don't have to re-type custom roots each time.
public interface ICustomPrimaryTableService
{
    Task<List<CustomPrimaryTableRecord>> GetByConnectionAsync(Guid connectionId, CancellationToken ct = default);

    // Adds an entry if the (connection, table, alias) combination isn't
    // already present. Idempotent: returns the existing row when duplicate.
    // alias is optional — pass null/empty to save a bare table root.
    // isPrimary flags the row as a suggested primary in the builder's
    // dropdown; isDefaultPrimary marks it as the auto-pick for new reports
    // on this connection (at most one per connection).
    Task<CustomPrimaryTableRecord> AddAsync(
        Guid connectionId, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        string? createdById, string? createdByEmail,
        CancellationToken ct = default,
        // Optional typing — null leaves the row unclassified. Trailing
        // optional params keep the existing call sites compiling without
        // changes; new call sites pass these through from the dialog.
        string? tableType = null,
        string? primaryColumn = null,
        string? additionalKeyColumns = null,
        // Optional free-text description shown in the admin tab and editable
        // from the add/edit dialog. Null/blank persists as NULL.
        string? description = null);

    // Replaces the table name / alias / flags on an existing entry. The
    // service clears any prior default on the same connection when the
    // incoming row is marked as default — mirrors the filtered unique index
    // at the DB layer, but handles the "swap" gracefully instead of hitting
    // the constraint.
    Task UpdateAsync(
        Guid id, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        CancellationToken ct = default,
        string? tableType = null,
        string? primaryColumn = null,
        string? additionalKeyColumns = null,
        string? description = null);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // ── Role-scoped owner fields ────────────────────────────────────────
    // Each primary table can map specific roles to specific owner columns
    // (Loan Officer → loan_officer_id; Processor → processor_id; etc.).
    // A self-scoped query injects the column for the signed-in user's role.
    // A role with no entry on the primary resolves to ForceNoMatch (the
    // query returns zero rows) — no default fallback, explicit is safer.

    Task<IReadOnlyDictionary<Guid, string>> GetRoleOwnerFieldsAsync(Guid primaryTableId, CancellationToken ct = default);
    Task SetRoleOwnerAsync(Guid primaryTableId, Guid roleId, string ownerFieldId, CancellationToken ct = default);
    Task ClearRoleOwnerAsync(Guid primaryTableId, Guid roleId, CancellationToken ct = default);
    // Convenience for the resolver: returns null when no mapping exists.
    Task<string?> ResolveOwnerFieldForRoleAsync(Guid primaryTableId, Guid roleId, CancellationToken ct = default);
}

public sealed class CustomPrimaryTableRecord
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    // Eligible as a report primary. Rendered starred in the picker and
    // grouped under "Suggested primaries". Off-catalog (unflagged) rows
    // still appear under "Other tables" so nothing is ever fully hidden.
    public bool IsPrimary { get; set; }
    // Default pick for new reports on this connection. Exactly one row
    // per connection can carry this; enforced by filtered unique index.
    public bool IsDefaultPrimary { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedById { get; set; }
    public string? CreatedByEmail { get; set; }

    // Optional free-text note explaining what this alias is for. Shown in
    // the Admin → Table Aliases tab and editable from the add/edit dialog.
    // Up to 500 chars. Null/blank = no description.
    public string? Description { get; set; }

    // ── Entity classification (TT-1 / TT-2) ────────────────────────────
    // Optional. One of TableTypes.* values, or null when the admin hasn't
    // classified the table yet. Stored open-ended so future LOS adapters
    // can introduce new type names without a schema migration.
    public string? TableType { get; set; }

    // The column that identifies the entity itself (e.g. "LNKEY" on an
    // Empower loan table). First component of the table's compound key.
    public string? PrimaryColumn { get; set; }

    // Comma-separated extra key columns required to identify a single row
    // alongside PrimaryColumn. Examples by TableType:
    //   LoanSingle:       (empty)         — PrimaryColumn alone is unique.
    //   LoanMultiple:     "IDX"
    //   BorrowerSingle:   "WHICHBORR"
    //   BorrowerMultiple: "WHICHBORR,IDX"
    // Stored as a string for simple persistence; consumers parse to a list
    // via AdditionalKeyColumnsList.
    public string? AdditionalKeyColumns { get; set; }

    // Convenience: the comma-separated AdditionalKeyColumns parsed into a
    // trimmed list. Empty list when null/blank. Read-only.
    public IReadOnlyList<string> AdditionalKeyColumnsList =>
        string.IsNullOrWhiteSpace(AdditionalKeyColumns)
            ? Array.Empty<string>()
            : AdditionalKeyColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
}

/// <summary>
/// Recommended values for <see cref="CustomPrimaryTableRecord.TableType"/>.
/// Open-ended at the DB layer — the column accepts any string, and future
/// adapters can register their own type names without a schema migration.
/// These four cover the Empower convention initially:
///   LoanSingle       — one row per loan, keyed by a single column (LNKEY).
///   LoanMultiple     — many rows per loan, indexed (LNKEY + IDX).
///   BorrowerSingle   — one row per borrower per loan (LNKEY + WHICHBORR).
///   BorrowerMultiple — many rows per borrower, indexed (LNKEY + WHICHBORR + IDX).
/// </summary>
public static class TableTypes
{
    public const string LoanSingle       = "LoanSingle";
    public const string LoanMultiple     = "LoanMultiple";
    public const string BorrowerSingle   = "BorrowerSingle";
    public const string BorrowerMultiple = "BorrowerMultiple";

    /// <summary>
    /// Recommended values for the <c>TableType</c> dropdown, in the order
    /// they should appear in the UI.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        LoanSingle,
        LoanMultiple,
        BorrowerSingle,
        BorrowerMultiple
    };

    /// <summary>
    /// Friendly label for the dropdown UI. Falls back to the raw value when
    /// it isn't one of the well-known constants — keeps the open-ended
    /// extensibility intact.
    /// </summary>
    public static string Display(string? value) => value switch
    {
        null or ""             => "(unknown)",
        LoanSingle             => "Loan — Single",
        LoanMultiple           => "Loan — Multiple",
        BorrowerSingle         => "Borrower — Single",
        BorrowerMultiple       => "Borrower — Multiple",
        _                      => value
    };
}
