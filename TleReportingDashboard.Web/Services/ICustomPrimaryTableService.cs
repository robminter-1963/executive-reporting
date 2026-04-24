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
        string? ownerFieldId,
        string? createdById, string? createdByEmail,
        CancellationToken ct = default);

    // Replaces the table name / alias / flags on an existing entry. The
    // service clears any prior default on the same connection when the
    // incoming row is marked as default — mirrors the filtered unique index
    // at the DB layer, but handles the "swap" gracefully instead of hitting
    // the constraint.
    Task UpdateAsync(
        Guid id, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        string? ownerFieldId,
        CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
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
    // SchemaConfig field id whose column identifies who "owns" a row in
    // this primary table. Feeds the row-level-scoping predicate when a
    // self-scoped role runs a report against this primary. NULL = no
    // owner concept, self-scoped reports on this primary return zero rows.
    public string? OwnerFieldId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedById { get; set; }
    public string? CreatedByEmail { get; set; }
}
