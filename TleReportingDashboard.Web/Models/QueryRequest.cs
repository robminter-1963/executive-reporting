namespace TleReportingDashboard.Web.Models;

public class QueryRequest
{
    public List<string> FieldIds { get; set; } = new();
    public Dictionary<string, object?> Filters { get; set; } = new();
    public Dictionary<string, string>? Aggregations { get; set; } // field ID → aggregation function
    public string? SortField { get; set; }
    public string? SortDirection { get; set; }
    // Optional second-level sort applied after the primary SortField. Used
    // by the DetailViewer so rows cluster by the group-by field first and
    // then fall into the report's configured default sort within each
    // group. Both builder paths (QueryBuilder + SqlEmitter) append this to
    // the ORDER BY when populated.
    public string? SecondarySortField { get; set; }
    public string? SecondarySortDirection { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public const int MaxPageSize = 50000;
    public List<string>? CustomFilterIds { get; set; } // IDs of active custom filters from schema_config.json
    public string? DateFieldId { get; set; } // which date field to filter on
    public string? DateOperatorId { get; set; } // relative date operator ID
    public DateTime? DateFrom { get; set; } // custom range start
    public DateTime? DateTo { get; set; } // custom range end
    // RPT_company_connections.id to query against. When null, the query
    // falls back to the current company's is_default connection — a
    // transitional safety net while existing reports are being backfilled.
    // After the migration finishes and reports are re-saved via the editor,
    // this is always populated.
    public Guid? ConnectionId { get; set; }

    // Overrides the schema's default primary table for this report. Null
    // means "use the schema's Settings.PrimaryTable". Useful when different
    // reports against the same connection start from different roots
    // (e.g. one report rooted at salesforce.lead, another at
    // salesforce.opportunity). Takes precedence over the schema setting.
    public string? PrimaryTable { get; set; }

    // Row-level scoping — when set, QueryBuilder injects
    // `<OwnerFieldColumn> = @__scope_user` into the WHERE clause. Null
    // means "no self-scope" (admin, or role.scope_rule = 'all').
    // Populated upstream by the page (or a resolver) after checking the
    // user's role + looking up the primary table's owner_field_id and
    // the user's external_user_id for the connection. Enforcement is
    // conservative: if the role is self-scoped AND either OwnerFieldId
    // or ExternalUserId is missing, callers should pass a sentinel that
    // forces zero rows (see QueryScopingInfo.ForceNoMatch).
    public QueryScopingInfo? Scoping { get; set; }

    // Ordered list of field ids to use in GROUP BY when the selected fields
    // contain any aggregate expressions. Null → auto-derive from all selected
    // non-aggregate fields. Populated by the Group By chip bar when the user
    // customizes ordering or omits a field.
    public List<string>? GroupByFieldIds { get; set; }

    // Ad-hoc filters assembled by the Report Builder's Advanced Filters
    // panel. The root is a FilterGroup that recursively contains clauses
    // and nested sub-groups, so expressions like
    //   (A OR B) AND (C OR D) AND (E AND (F OR G))
    // can all be represented. Combined with every other filter source
    // (Filters dictionary, CustomFilterIds, DateFieldId/DateOperatorId)
    // via AND at emission time. Null = no advanced filters — legacy
    // reports and any execution outside the Report Builder (worker /
    // detail view) just leave this null and the emitter treats it as a
    // no-op. Feature-flagged on the UI side via Features:AdvancedFilters.
    public FilterGroup? AdvancedFilters { get; set; }
}

// Row-level scoping payload. Carried on QueryRequest.Scoping; resolved
// upstream by the page or a helper (given signed-in user + the
// report's connection / primary table).
//
//   * ForceNoMatch = true: emitter injects `1 = 0` — user is self-scoped
//                   but the DB can't identify their rows (no owner_field_id
//                   on the primary, or no external_user_id on the user
//                   for the connection). Fail-safe to zero rows.
//   * OwnerFieldId + ExternalUserId both set: emitter resolves the owner
//                   field to its source column and emits
//                   `<owner_col> = @__scope_user`.
//   * null (i.e. no Scoping on the request): no self-scope; runs as
//                   today. Admins always pass null.
public sealed class QueryScopingInfo
{
    public string? OwnerFieldId { get; set; }
    public string? ExternalUserId { get; set; }
    public bool ForceNoMatch { get; set; }
}
