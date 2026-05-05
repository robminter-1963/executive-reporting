namespace TleReportingDashboard.Web.Configuration;

public sealed class SchemaConfig
{
    public SchemaSettings Settings { get; init; } = new();
    public List<JoinDefinition> Joins { get; init; } = [];
    public List<FieldDefinition> Fields { get; init; } = [];
    public List<LookupDefinition> Lookups { get; init; } = [];
    public List<CustomFilterDefinition> CustomFilters { get; init; } = [];
    // Admin-curated list of field domain names (e.g. Loan, Borrower, Dates).
    // Used to drive the Domain dropdown in the Field editor. Seeded on first
    // load from distinct field.Domain values when this list is empty, so
    // existing deployments upgrade transparently.
    public List<string> Domains { get; init; } = [];
}
