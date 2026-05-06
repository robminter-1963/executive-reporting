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

    // Per-report SQL-mode calculated columns. Each carries a raw SQL
    // expression that QueryBuilder appends to the SELECT as
    // "<expr> AS <Key>", plus the schema join ids the expression depends
    // on (pulled into the FROM/JOIN chain so referenced aliases resolve).
    // Null/empty = no SQL calcs — formula-mode calcs evaluate client-side
    // and don't appear here. Same trust model as FieldConfig.SqlExpression:
    // admin-authored, embedded as-is.
    public List<TableCalcSqlSelector>? TableCalcSqlColumns { get; set; }

    // When true, QueryBuilder skips its fallback "ORDER BY first selected
    // field" emission. Used when the admin explicitly chose "(None)" in
    // the Sort By dropdown — the SQL stays unsorted (Postgres-friendly;
    // OFFSET on SQL Server still requires ORDER BY, so the dialect path
    // re-enables the fallback for that DB to keep paging working).
    // Defaults to false so existing callers and pre-multi-sort reports
    // keep getting a deterministic ORDER BY.
    public bool DisableDefaultSort { get; set; }

    // When true, QueryBuilder emits `SELECT DISTINCT` instead of `SELECT`.
    // Useful when the report's join chain produces row-multiplication
    // (e.g. a one-to-many LEFT JOIN where the admin only cares about the
    // parent's distinct rows). Defaults to false — most reports want the
    // raw row counts. Toggled per-report from the Report Builder; persisted
    // in ColumnState alongside other table-view options.
    public bool Distinct { get; set; }

    // Primary-key columns of the report's primary table, in PK ordinal
    // order. When SortField is null/empty, QueryBuilder uses these as a
    // behind-the-scenes ORDER BY so OFFSET pagination stays deterministic
    // even when the admin picked "(None)" in the Sort By dropdown.
    // Raw column names (not alias-qualified) — QueryBuilder qualifies
    // with the primary table's alias at emission. Empty/null = no
    // auto-fallback (legacy behavior: ORDER BY first selected field).
    public List<string>? FallbackSortColumns { get; set; }
}

// One SQL-mode calc column carried on the request. Key is the column
// alias used in the SELECT (also the row-dict key on the way back).
// SqlExpression is appended as-is between the alias and the next column.
// JoinIds reference JoinDefinition.Id strings on the connection schema.
public sealed class TableCalcSqlSelector
{
    public required string Key { get; init; }
    public required string SqlExpression { get; init; }
    public List<string>? JoinIds { get; init; }
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
    // Self-scope (scope_rule = 'self'): match rows by owner column = user.
    public string? OwnerFieldId { get; set; }
    public string? ExternalUserId { get; set; }

    // Direct-column scope: like self-scope, but bypasses the schema's
    // field-id catalog and consumes a raw owner-column reference instead.
    // Used by the scheduled-report Worker on Individual schedules — the
    // Worker derives the column from Team Builder's team_type → owner
    // column map and the value(s) from the team's roster, without ever
    // touching RPT_users / RPT_user_connection_logins / role resolution.
    // Wins over OwnerFieldId when both are set.
    //
    //   * OwnerColumn — raw identifier (e.g. "PROCESSOR_USERID") or a
    //                   pre-qualified alias.column ("BORR.LOAN_NO").
    //                   Auto-prefixed with PrimaryAlias when unqualified
    //                   to mirror the team-scope behavior.
    //   * PrimaryAlias — used to qualify a bare column. Caller supplies
    //                   from PrimaryTableRef.Parse so the alias matches
    //                   what the emitter actually uses in FROM.
    //   * ExternalUserId — single-value form, emits "col = @p".
    //   * ExternalUserIds — multi-value form, emits "col IN (@p0, @p1, …)".
    //                   Wins over ExternalUserId. Used by the Worker's
    //                   "fetch once, group by owner" Individual flow so
    //                   one round-trip can pull every team member's
    //                   rows in a single pass.
    public string? OwnerColumn { get; set; }
    public string? PrimaryAlias { get; set; }
    public IReadOnlyList<string>? ExternalUserIds { get; set; }

    // Team-scope (scope_rule = 'team'): match rows by ANY of the user's
    // teams. Mutually exclusive with the self-scope pair above — a role
    // has one scope_rule at a time, so only one of the two branches is
    // populated on any given request.
    public TeamScopingInfo? TeamScope { get; set; }

    public bool ForceNoMatch { get; set; }
    // Human-readable explanation of why scoping resolved this way —
    // surfaced in the "Show query" debug dialog so admins can see why a
    // tile returned zero rows without chasing server logs. Only set by the
    // resolver when a decision is interesting (typically alongside
    // ForceNoMatch = true).
    public string? Reason { get; set; }
}

// Team-scope predicate inputs. The emitter produces one OR'd EXISTS per
// entry in Teams, wrapping MembersSql as a subquery and filtering by
// team_id + the entry's owner column on the primary table alias.
public sealed class TeamScopingInfo
{
    // Free-form admin-written SELECT returning (team_id, member_ext_id).
    // No literals leak from here into the emitter — the SQL body is
    // whatever the connection's RPT_team_sources.members_sql holds.
    public string MembersSql { get; set; } = string.Empty;

    // Primary-table alias (or bare table name when no alias is set) used
    // to qualify the owner column in the predicate, e.g. "C" in "C.PROC_USERID".
    public string PrimaryAlias { get; set; } = string.Empty;

    public IReadOnlyList<TeamScopeEntry> Teams { get; set; } = Array.Empty<TeamScopeEntry>();
}

// One (user-team, owner-column) pair the emitter expands into a single
// EXISTS clause. OwnerColumn is a raw identifier on the primary table
// (e.g. "PROCESSOR_USERID"), qualified at emission with PrimaryAlias.
public sealed record TeamScopeEntry(int TeamId, string OwnerColumn);
