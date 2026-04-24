using System.Text.RegularExpressions;

namespace TleReportingDashboard.Web.Services;

// Parses and formats the compound "schema.table [AS] alias" string stored in
// SavedReport.PrimaryTable and carried through QueryRequest.PrimaryTable.
//
// The emitter uses the raw stored form in the FROM clause, but JoinResolver
// needs to know both parts so fields whose SourceTable matches either the
// table name OR the alias are treated as "on the primary table" and don't
// trigger a JOIN lookup.
//
// Existing reports store a bare table name (no alias). Parse returns Alias =
// null in that case — no behavior change.
public static partial class PrimaryTableRef
{
    // table is [schema.]ident-with-optional-brackets; alias is a plain SQL
    // identifier. "AS" is optional (both "T AS A" and "T A" are valid SQL).
    [GeneratedRegex(
        @"^\s*(?<table>[^\s]+)(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_][A-Za-z0-9_]*))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrimaryTableRegex();

    // Splits the stored string into (table, alias). Returns (empty, null) for
    // null/whitespace input. If the string doesn't match the expected shape,
    // falls back to the whole string as the table and null alias — the emitter
    // will then pass it through as a raw FROM target, which is the prior
    // behavior for reports saved before this feature shipped.
    public static (string Table, string? Alias) Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (string.Empty, null);
        var match = PrimaryTableRegex().Match(raw);
        if (!match.Success) return (raw.Trim(), null);
        var table = match.Groups["table"].Value;
        var alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : null;
        return (table, alias);
    }

    // Joins the two parts back into a single string suitable for storage and
    // for the FROM clause. Blank alias drops the "AS" so it's a no-op for
    // legacy reports.
    public static string Format(string table, string? alias)
    {
        if (string.IsNullOrWhiteSpace(table)) return string.Empty;
        return string.IsNullOrWhiteSpace(alias) ? table.Trim() : $"{table.Trim()} AS {alias.Trim()}";
    }

    // Regex for validating the alias field in the UI and service layer before
    // persistence. Conservative on purpose: same shape the emitter's other
    // safe-identifier guards use.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled)]
    public static partial Regex AliasRegex();

    // Table form is broader (allows "[SCHEMA].[TABLE]", "schema.table", etc.).
    // Rejects whitespace and SQL meta characters so admin-gated entries can't
    // smuggle a semicolon in.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\.\[\]]*$", RegexOptions.Compiled)]
    public static partial Regex TableRegex();
}
