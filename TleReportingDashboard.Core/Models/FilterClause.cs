namespace TleReportingDashboard.Web.Models;

// Ad-hoc filter row submitted by the Report Builder's Advanced Filters
// panel. Each clause is ANDed with the rest when the query runs. Stored
// on the saved report as an array under column_state.AdvancedFilters so
// the existing `filters` JSON stays untouched (backward compat: legacy
// reports without advanced filters keep working).
//
// Lifecycle:
//   1. User builds the row in the UI (field picker + operator + values).
//   2. ReportBuilder serializes the row into this shape and sends it in
//      QueryRequest.AdvancedFilters when the user hits Run.
//   3. QueryBuilder appends one WHERE fragment per clause, parameterized.
public sealed class FilterClause
{
    // Field id from the schema catalog. Must resolve to a FieldConfig.
    public string FieldId { get; set; } = string.Empty;

    // Operator key (see FilterOperators). Determines SQL shape + how
    // Values is interpreted.
    public string Op { get; set; } = string.Empty;

    // Raw string values. Typed by the operator + field data type:
    //   - date/datetime operators: "yyyy-MM-dd" strings (user-picked).
    //   - relative preset: single entry with the preset id ("yesterday", …).
    //   - numeric operators: parsed to decimal.
    //   - text operators: taken as-is.
    //   - in_list / not_in_list: one entry per value.
    // Kept as strings so JSON round-trip is loss-free; the emitter
    // validates + parses per-operator.
    public List<string> Values { get; set; } = new();
}

// Canonical operator keys. Kept as string constants (not an enum) so
// JSON serialization is obvious and stable across releases.
public static class FilterOperators
{
    // `Equals` intentionally shadows object.Equals — we're a static class
    // and never instantiate, and the name is the right canonical one for
    // this operator. `new` silences the shadowing warning without
    // affecting behavior.
    // Shared
    public new const string Equals   = "equals";
    public const string NotEquals    = "not_equals";
    public const string IsNull       = "is_null";
    public const string IsNotNull    = "is_not_null";

    // Date / datetime
    public const string Before       = "before";
    public const string After        = "after";
    public const string OnOrBefore   = "on_or_before";
    public const string OnOrAfter    = "on_or_after";
    public const string Between      = "between";
    public const string Relative     = "relative";

    // Text
    public const string Contains     = "contains";
    public const string StartsWith   = "starts_with";
    public const string EndsWith     = "ends_with";
    public const string InList       = "in_list";
    public const string NotInList    = "not_in_list";

    // Numeric
    public const string Lt           = "lt";
    public const string Lte          = "lte";
    public const string Gt           = "gt";
    public const string Gte          = "gte";

    // Boolean
    public const string IsTrue       = "is_true";
    public const string IsFalse      = "is_false";
}

// One entry inside a FilterGroup. Exactly one of Clause / Group is set —
// Clause when this is a leaf filter row, Group when it's a nested sub-
// group. Kept as a mixed flat list (rather than two separate arrays) so
// the user-visible ordering of rows vs sub-groups round-trips exactly.
public sealed class FilterItem
{
    public FilterClause? Clause { get; set; }
    public FilterGroup? Group { get; set; }
}

// Logical-operator constants for FilterGroup.Op. Kept as strings (not an
// enum) for stable JSON serialization. Case-insensitive on read.
public static class FilterGroupOps
{
    public const string And = "AND";
    public const string Or  = "OR";
}

// A group of filter items combined with a single logical operator. The
// root of a report's advanced filters is always a FilterGroup (default
// op = AND). Groups can nest to any depth — each sub-group gets its own
// parentheses at emission time, so expressions like
// (A OR B) AND (C AND D) AND (E OR (F AND G)) are all representable.
public sealed class FilterGroup
{
    // "AND" or "OR" — matched via FilterGroupOps constants. Case-
    // insensitive on read; emitted in upper-case.
    public string Op { get; set; } = FilterGroupOps.And;
    public List<FilterItem> Items { get; set; } = new();
}

// Relative-date preset keys for the `relative` operator. Values[0] on the
// FilterClause carries one of these strings.
//
// Weeks start on Monday for ThisWeek / LastWeek — the ISO 8601 / EU
// convention. Quarters are calendar quarters (Q1 = Jan–Mar). Adding new
// presets is back-compat: the SQL emitter's switch has a `_` default
// that resolves to Yesterday, so an unknown key on an old build won't
// crash, just degrades to yesterday.
public static class RelativeDatePresets
{
    public const string Today       = "today";
    public const string Yesterday   = "yesterday";
    public const string Last7Days   = "last_7_days";
    public const string Last30Days  = "last_30_days";
    public const string Last90Days  = "last_90_days";
    public const string ThisWeek    = "this_week";
    public const string LastWeek    = "last_week";
    public const string Mtd         = "mtd";          // month-to-date
    public const string ThisMonth   = "this_month";   // full calendar month
    public const string LastMonth   = "last_month";
    public const string ThisQuarter = "this_quarter";
    public const string LastQuarter = "last_quarter";
    public const string Ytd         = "ytd";
    public const string LastYear    = "last_year";
}
