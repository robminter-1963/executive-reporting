using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Read-only Dataverse Metadata API client used by SchemaBuilderService to
// surface entities and attributes the same way INFORMATION_SCHEMA does for
// SQL Server / Postgres. Auth is Entra OAuth client_credentials; tokens are
// cached per-connection for the duration of their reported lifetime so a
// burst of metadata calls during schema setup doesn't hit the token endpoint
// every request.
public class DataverseSchemaClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DataverseSchemaClient> _logger;

    // Process-wide cache. Tokens last ~1 hour by default; we refresh
    // proactively 60 seconds before expiry to avoid mid-flight 401s.
    private static readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset ExpiresAt)> _tokenCache = new();

    public DataverseSchemaClient(IHttpClientFactory httpClientFactory, ILogger<DataverseSchemaClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public record DvEntity(string LogicalName, string DisplayName, string SchemaName, string EntitySetName);
    public record DvAttribute(string LogicalName, string DisplayName, string AttributeType, bool IsPrimaryId, bool IsRequired);
    // Many-to-one (lookup) relationship — the queried entity holds an FK
    // to ReferencedEntity. SchemaName is the canonical relationship id
    // (e.g. "contact_account_parentcustomerid"); ReferencingAttribute is
    // the FK column on the local entity (e.g. "parentcustomerid");
    // ReferencedAttribute is the parent's primary key (e.g. "accountid").
    public record DvManyToOne(string SchemaName, string ReferencingEntity, string ReferencingAttribute,
                              string ReferencedEntity, string ReferencedAttribute);
    // One-to-many (the inverse view) — the queried entity is referenced
    // BY child rows in ReferencingEntity. Useful for "what links to me"
    // discovery; the actual join still emits the same shape as the
    // many-to-one direction (child.fk = parent.pk), just with the roles
    // flipped at the call site.
    public record DvOneToMany(string SchemaName, string ReferencingEntity, string ReferencingAttribute,
                              string ReferencedEntity, string ReferencedAttribute);

    public async Task<List<DvEntity>> GetEntitiesAsync(CompanyConnectionRecord r, CancellationToken ct = default)
    {
        // EntityDefinitions returns every entity in the org — system + custom.
        // We intentionally don't filter to IsCustomizable since admins build
        // reports against system entities (account, contact, opportunity)
        // far more often than custom ones; pre-filtering here would force
        // us to add a "show system entities" toggle later. Sorted client-side
        // by LogicalName so the picker reads alphabetically.
        var url = $"{r.DvEnvironmentUrl!.TrimEnd('/')}/api/data/v9.2/EntityDefinitions"
                + "?$select=LogicalName,SchemaName,EntitySetName,DisplayName";

        var entities = new List<DvEntity>();
        await foreach (var elem in PageAsync(r, url, ct))
        {
            entities.Add(new DvEntity(
                LogicalName: elem.GetProperty("LogicalName").GetString() ?? string.Empty,
                DisplayName: ExtractLocalizedLabel(elem) ?? string.Empty,
                SchemaName: elem.TryGetProperty("SchemaName", out var sn) ? sn.GetString() ?? string.Empty : string.Empty,
                EntitySetName: elem.TryGetProperty("EntitySetName", out var en) ? en.GetString() ?? string.Empty : string.Empty));
        }
        return entities
            .Where(e => !string.IsNullOrEmpty(e.LogicalName))
            .OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<DvAttribute>> GetAttributesAsync(CompanyConnectionRecord r, string entityLogicalName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            return new List<DvAttribute>();

        // AttributeOf is non-null on virtual / shadow attributes (e.g. the
        // "name" half of a lookup pair). Filtering them out leaves the
        // canonical column the reader actually selects.
        var url = $"{r.DvEnvironmentUrl!.TrimEnd('/')}/api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entityLogicalName)}')/Attributes"
                + "?$select=LogicalName,SchemaName,AttributeType,DisplayName,IsPrimaryId,RequiredLevel"
                + "&$filter=AttributeOf eq null";

        var attributes = new List<DvAttribute>();
        await foreach (var elem in PageAsync(r, url, ct))
        {
            var required = false;
            if (elem.TryGetProperty("RequiredLevel", out var rl)
                && rl.ValueKind == JsonValueKind.Object
                && rl.TryGetProperty("Value", out var rlv))
            {
                // RequiredLevel.Value is a string enum: "None" / "Recommended"
                // / "ApplicationRequired" / "SystemRequired". Treat anything
                // beyond None as "this column always has a value" — drives
                // the schema's IsNullable flag.
                var level = rlv.GetString();
                required = !string.Equals(level, "None", StringComparison.OrdinalIgnoreCase);
            }

            attributes.Add(new DvAttribute(
                LogicalName: elem.GetProperty("LogicalName").GetString() ?? string.Empty,
                DisplayName: ExtractLocalizedLabel(elem) ?? string.Empty,
                AttributeType: elem.TryGetProperty("AttributeType", out var at) ? at.GetString() ?? string.Empty : string.Empty,
                IsPrimaryId: elem.TryGetProperty("IsPrimaryId", out var pid) && pid.ValueKind == JsonValueKind.True,
                IsRequired: required));
        }
        return attributes
            .Where(a => !string.IsNullOrEmpty(a.LogicalName))
            .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Many-to-one relationships an entity participates in as the CHILD
    // (i.e. the entity holds the FK). This is the "I have a lookup to X"
    // view — the most common driver of joins authored in Schema Builder.
    public async Task<List<DvManyToOne>> GetManyToOneRelationshipsAsync(
        CompanyConnectionRecord r, string entityLogicalName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            return new List<DvManyToOne>();

        var url = $"{r.DvEnvironmentUrl!.TrimEnd('/')}/api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entityLogicalName)}')/ManyToOneRelationships"
                + "?$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedEntity,ReferencedAttribute";

        var rels = new List<DvManyToOne>();
        await foreach (var elem in PageAsync(r, url, ct))
        {
            rels.Add(new DvManyToOne(
                SchemaName: elem.TryGetProperty("SchemaName", out var sn) ? sn.GetString() ?? string.Empty : string.Empty,
                ReferencingEntity: elem.TryGetProperty("ReferencingEntity", out var rge) ? rge.GetString() ?? string.Empty : string.Empty,
                ReferencingAttribute: elem.TryGetProperty("ReferencingAttribute", out var rga) ? rga.GetString() ?? string.Empty : string.Empty,
                ReferencedEntity: elem.TryGetProperty("ReferencedEntity", out var rde) ? rde.GetString() ?? string.Empty : string.Empty,
                ReferencedAttribute: elem.TryGetProperty("ReferencedAttribute", out var rda) ? rda.GetString() ?? string.Empty : string.Empty));
        }
        return rels
            .Where(x => !string.IsNullOrEmpty(x.ReferencingAttribute) && !string.IsNullOrEmpty(x.ReferencedEntity))
            .OrderBy(x => x.ReferencedEntity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ReferencingAttribute, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // One-to-many relationships an entity participates in as the PARENT
    // (i.e. children hold the FK back to this entity). The inverse of
    // ManyToOne — same join shape, but the call site picks which entity
    // to alias as the source.
    public async Task<List<DvOneToMany>> GetOneToManyRelationshipsAsync(
        CompanyConnectionRecord r, string entityLogicalName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            return new List<DvOneToMany>();

        var url = $"{r.DvEnvironmentUrl!.TrimEnd('/')}/api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entityLogicalName)}')/OneToManyRelationships"
                + "?$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedEntity,ReferencedAttribute";

        var rels = new List<DvOneToMany>();
        await foreach (var elem in PageAsync(r, url, ct))
        {
            rels.Add(new DvOneToMany(
                SchemaName: elem.TryGetProperty("SchemaName", out var sn) ? sn.GetString() ?? string.Empty : string.Empty,
                ReferencingEntity: elem.TryGetProperty("ReferencingEntity", out var rge) ? rge.GetString() ?? string.Empty : string.Empty,
                ReferencingAttribute: elem.TryGetProperty("ReferencingAttribute", out var rga) ? rga.GetString() ?? string.Empty : string.Empty,
                ReferencedEntity: elem.TryGetProperty("ReferencedEntity", out var rde) ? rde.GetString() ?? string.Empty : string.Empty,
                ReferencedAttribute: elem.TryGetProperty("ReferencedAttribute", out var rda) ? rda.GetString() ?? string.Empty : string.Empty));
        }
        return rels
            .Where(x => !string.IsNullOrEmpty(x.ReferencingEntity) && !string.IsNullOrEmpty(x.ReferencedAttribute))
            .OrderBy(x => x.ReferencingEntity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ReferencingAttribute, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Internals ─────────────────────────────────────────────────────

    // Yields the elements of every "value" array across one or more
    // OData pages, following @odata.nextLink until exhausted. EntityDefinitions
    // and Attributes both return paged collections; without following
    // nextLink, larger orgs would silently truncate at the default page cap
    // (5000 rows).
    private async IAsyncEnumerable<JsonElement> PageAsync(
        CompanyConnectionRecord r, string firstUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var token = await GetTokenAsync(r, ct);
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var url = firstUrl;
        while (!string.IsNullOrEmpty(url))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // OData-MaxVersion: 4.0 is required by Dataverse; without it
            // the server returns the metadata in v3 shape and our
            // property names don't line up.
            req.Headers.Add("OData-MaxVersion", "4.0");
            req.Headers.Add("OData-Version", "4.0");

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Dataverse metadata call failed ({(int)resp.StatusCode}): {(body.Length > 500 ? body[..500] + "…" : body)}");
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in arr.EnumerateArray())
                {
                    // Clone so the iterator's element survives the
                    // outer JsonDocument's disposal at the bottom of
                    // this loop iteration.
                    yield return elem.Clone();
                }
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
    }

    private async Task<string> GetTokenAsync(CompanyConnectionRecord r, CancellationToken ct)
    {
        if (_tokenCache.TryGetValue(r.Id, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            return cached.Token;

        var envUrl   = (r.DvEnvironmentUrl ?? string.Empty).Trim().TrimEnd('/');
        var tenant   = (r.DvTenantId      ?? string.Empty).Trim();
        var clientId = (r.DvClientId      ?? string.Empty).Trim();
        var secret   = (r.DvClientSecret  ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(envUrl) || string.IsNullOrEmpty(tenant)
            || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException(
                "Dataverse connection is missing Environment URL, Tenant ID, Client ID, or Client Secret.");
        }

        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id",     clientId),
            new KeyValuePair<string, string>("client_secret", secret),
            new KeyValuePair<string, string>("scope",         $"{envUrl}/.default"),
            new KeyValuePair<string, string>("grant_type",    "client_credentials"),
        });
        using var resp = await http.PostAsync(
            $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Token endpoint returned {(int)resp.StatusCode}: {(body.Length > 500 ? body[..500] + "…" : body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var ei) ? ei : 3600;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Dataverse token endpoint returned no access_token.");

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        _tokenCache[r.Id] = (token, expiresAt);
        return token;
    }

    // Dataverse returns labels as a localized object:
    // { "LocalizedLabels":[{"Label":"Account","LanguageCode":1033}], ... }
    // Pulls the first localized label, or empty if none. Falls back to
    // the entity/attribute logical name at the call site if this returns
    // null.
    private static string? ExtractLocalizedLabel(JsonElement elem)
    {
        if (!elem.TryGetProperty("DisplayName", out var dn) || dn.ValueKind != JsonValueKind.Object)
            return null;
        if (!dn.TryGetProperty("LocalizedLabels", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var ll in arr.EnumerateArray())
        {
            if (ll.TryGetProperty("Label", out var lbl) && lbl.ValueKind == JsonValueKind.String)
            {
                var s = lbl.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    // Maps the Dataverse AttributeType enum to the project's field DataType
    // strings (the same values SchemaBuilderService.MapDbTypeToFieldDataType
    // emits for SQL Server / Postgres types). Unknown types fall back to
    // "text" — admins can override on the field detail panel later.
    public static string MapAttributeTypeToFieldDataType(string attributeType) => attributeType.ToLowerInvariant() switch
    {
        "string" or "memo" or "uniqueidentifier" or "lookup" or "owner" or "customer" or "entityname" => "text",
        "integer" or "bigint" => "integer",
        "money" => "currency",
        "decimal" or "double" => "percent",
        "datetime" => "date",
        "boolean" => "boolean",
        "picklist" or "state" or "status" or "multiselectpicklist" => "text",
        _ => "text"
    };
}
