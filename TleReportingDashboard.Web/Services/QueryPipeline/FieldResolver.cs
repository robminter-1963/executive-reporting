using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

public static class FieldResolver
{
    public static List<FieldDefinition> ResolveFields(
        IReadOnlyList<string> fieldIds,
        IReadOnlyList<FieldDefinition> schemaFields)
    {
        if (fieldIds is null || fieldIds.Count == 0)
            throw new ArgumentException("At least one field ID must be specified.");

        // First-wins dedupe — see SchemaService.GetFieldConfigsAsync.
        var lookup = schemaFields
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var unknowns = fieldIds.Where(id => !lookup.ContainsKey(id)).Distinct().ToList();
        if (unknowns.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown field IDs: {string.Join(", ", unknowns)}. " +
                "All field IDs must exist in the schema configuration.");
        }

        // Deduplicate while preserving order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<FieldDefinition>();

        foreach (var id in fieldIds)
        {
            if (seen.Add(id))
                resolved.Add(lookup[id]);
        }

        return resolved;
    }
}
