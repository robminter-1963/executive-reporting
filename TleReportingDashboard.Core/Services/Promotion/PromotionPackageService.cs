using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services.Promotion;

public sealed class PromotionPackageService : IPromotionPackageService
{
    private readonly ISchemaConfigStore _schemaStore;
    private readonly ICompanyConnectionAdminService _connections;
    private readonly ICompanyRegistry _companies;
    private readonly ICompanyAdminService _companyAdmin;
    private readonly IReportService _reports;
    private readonly IGridTemplateService _gridTemplates;
    private readonly ILibrarySectionService _librarySections;
    private readonly IMasterDashboardService _dashboards;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PromotionPackageService> _logger;

    public PromotionPackageService(
        ISchemaConfigStore schemaStore,
        ICompanyConnectionAdminService connections,
        ICompanyRegistry companies,
        ICompanyAdminService companyAdmin,
        IReportService reports,
        IGridTemplateService gridTemplates,
        ILibrarySectionService librarySections,
        IMasterDashboardService dashboards,
        IConfiguration configuration,
        ILogger<PromotionPackageService> logger)
    {
        _schemaStore = schemaStore;
        _connections = connections;
        _companies = companies;
        _companyAdmin = companyAdmin;
        _reports = reports;
        _gridTemplates = gridTemplates;
        _librarySections = librarySections;
        _dashboards = dashboards;
        _configuration = configuration;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════
    // Export
    // ════════════════════════════════════════════════════════════════════

    public async Task<byte[]> ExportAsync(
        PromotionExportRequest request,
        string? exportedBy,
        string? notes,
        CancellationToken ct = default)
    {
        var pkg = new PromotionPackage
        {
            SourceEnvironment = _configuration["Environment:Label"] ?? "UNKNOWN",
            ExportedAtUtc = DateTime.UtcNow,
            ExportedBy = exportedBy,
            Notes = notes
        };

        // Company label + connection lookups travel with the package so the
        // importer (whose GUIDs differ) can map by name. Resolve everything
        // up front into dictionaries the section builders below reuse.
        var allCompanies = await _companies.GetActiveAsync(ct);
        var companyById = allCompanies.ToDictionary(c => c.Id);

        await BuildSchemaConfigSectionAsync(pkg, request, companyById, ct);

        var companyIds = request.CompanyIds.Distinct().ToList();

        // connectionId → (name, companyId) for every in-scope company.
        // Reports / templates carry connection ids; this turns those ids
        // into the (name, company) pair the importer resolves against.
        var connectionById = new Dictionary<Guid, (string Name, Guid CompanyId)>();
        foreach (var cid in companyIds)
            foreach (var c in await _connections.GetByCompanyAsync(cid, ct))
                connectionById[c.Id] = (c.Name, cid);

        if (request.IncludeCompanies)
        {
            var records = (await _companyAdmin.GetAllAsync(ct))
                .Where(c => companyIds.Contains(c.Id));
            foreach (var c in records)
            {
                pkg.Companies.Add(new PromotionPackage.CompanyEntry
                {
                    SourceId = c.Id,
                    Code = c.Code,
                    Name = c.Name,
                    WebsiteUrl = c.WebsiteUrl,
                    DisplayOrder = c.DisplayOrder,
                    IsHidden = c.IsHidden
                });
            }
        }

        // Reports first when requested — the grid-template section only
        // bundles templates that exported reports actually reference, so the
        // report set has to be resolved before we know which templates matter.
        var exportedReportsByCompany = new Dictionary<Guid, List<SavedReport>>();
        if (request.IncludeReports || request.IncludeDashboards || request.IncludeLibraryAndGridTaxonomy)
        {
            var all = await _reports.GetAllReportsAsync();
            foreach (var cid in companyIds)
                exportedReportsByCompany[cid] = all.Where(r => r.CompanyId == cid).ToList();
        }

        if (request.IncludeLibraryAndGridTaxonomy)
        {
            foreach (var cid in companyIds)
            {
                foreach (var sec in await _librarySections.GetSectionsAsync(cid))
                {
                    pkg.LibrarySections.Add(new PromotionPackage.LibrarySectionEntry
                    {
                        SourceId = sec.Id,
                        SourceCompanyId = cid,
                        Name = sec.Name,
                        SortOrder = sec.SortOrder
                    });
                }
            }

            // Grid templates referenced by exported reports — fetched by id so
            // a report's linked template is guaranteed present in the target.
            var referencedTemplateIds = exportedReportsByCompany.Values
                .SelectMany(rs => rs)
                .Where(r => r.GridTemplateId.HasValue)
                .Select(r => r.GridTemplateId!.Value)
                .Distinct();
            foreach (var tid in referencedTemplateIds)
            {
                var t = await _gridTemplates.GetTemplateAsync(tid);
                if (t is null) continue;
                Guid? connCompany = null;
                string? connName = null;
                if (t.ConnectionId is Guid cid && connectionById.TryGetValue(cid, out var info))
                {
                    connName = info.Name;
                    connCompany = info.CompanyId;
                }
                pkg.GridTemplates.Add(new PromotionPackage.GridTemplateEntry
                {
                    SourceId = t.Id,
                    Name = t.Name,
                    Description = t.Description,
                    IsShared = t.IsShared,
                    FieldIds = t.FieldIds,
                    ColumnState = t.ColumnState,
                    SourceConnectionId = t.ConnectionId,
                    SourceConnectionName = connName,
                    SourceCompanyId = connCompany
                });
            }
        }

        if (request.IncludeReports)
        {
            foreach (var cid in companyIds)
            {
                foreach (var r in exportedReportsByCompany.GetValueOrDefault(cid) ?? new())
                {
                    string? connName = r.ConnectionId is Guid rc && connectionById.TryGetValue(rc, out var ci)
                        ? ci.Name : null;
                    pkg.Reports.Add(new PromotionPackage.ReportEntry
                    {
                        SourceId = r.Id,
                        Name = r.Name,
                        InternalName = r.InternalName,
                        Category = r.Category,
                        SourceCompanyId = cid,
                        SourceCompanyName = companyById.GetValueOrDefault(cid)?.Name,
                        SourceConnectionId = r.ConnectionId,
                        SourceConnectionName = connName,
                        SourceGridTemplateId = r.GridTemplateId,
                        SourceLibrarySectionId = r.LibrarySectionId,
                        FieldIds = r.FieldIds,
                        Filters = r.Filters,
                        Aggregations = r.Aggregations,
                        ColumnState = r.ColumnState,
                        PrimaryTable = r.PrimaryTable
                    });
                }
            }
        }

        if (request.IncludeDashboards)
        {
            foreach (var cid in companyIds)
                await BuildDashboardEntryAsync(pkg, cid, companyById, ct);
        }

        var json = JsonSerializer.Serialize(pkg, AppJson.Indented);
        _logger.LogInformation(
            "Promotion package exported from {Env} by {User}: {Schemas} schemas, {Companies} companies, {Sections} sections, {Templates} templates, {Reports} reports, {Dashboards} dashboards",
            pkg.SourceEnvironment, exportedBy ?? "unknown",
            pkg.SchemaConfigs.Count, pkg.Companies.Count, pkg.LibrarySections.Count,
            pkg.GridTemplates.Count, pkg.Reports.Count, pkg.Dashboards.Count);
        return Encoding.UTF8.GetBytes(json);
    }

    private async Task BuildSchemaConfigSectionAsync(
        PromotionPackage pkg,
        PromotionExportRequest request,
        IReadOnlyDictionary<Guid, CompanySummary> companyById,
        CancellationToken ct)
    {
        foreach (var connId in request.SchemaConfigConnectionIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            // Scan companies to find the connection's owning one — the admin
            // service only lists per-company, mirroring the original export.
            CompanyConnectionRecord? conn = null;
            foreach (var (companyId, _) in companyById)
            {
                var inCompany = await _connections.GetByCompanyAsync(companyId, ct);
                conn = inCompany.FirstOrDefault(c => c.Id == connId);
                if (conn is not null) break;
            }
            if (conn is null)
            {
                _logger.LogWarning("Export skipped unknown connection id {ConnId}", connId);
                continue;
            }

            var schema = _schemaStore.GetForConnection(connId);
            if (schema.Fields.Count == 0 && schema.Joins.Count == 0)
            {
                _logger.LogInformation(
                    "Export skipped empty schema for connection {ConnId} ({Name})", connId, conn.Name);
                continue;
            }

            pkg.SchemaConfigs.Add(new PromotionPackage.SchemaConfigEntry
            {
                SourceConnectionName = conn.Name,
                SourceCompanyName = companyById.GetValueOrDefault(conn.CompanyId)?.Name ?? "(unknown)",
                Schema = schema
            });
        }
    }

    private async Task BuildDashboardEntryAsync(
        PromotionPackage pkg,
        Guid companyId,
        IReadOnlyDictionary<Guid, CompanySummary> companyById,
        CancellationToken ct)
    {
        var tabs = await _dashboards.GetTabsAsync(companyId);
        if (tabs.Count == 0) return;

        // report id → (internalName, name) so tiles travel with a name-based
        // fallback link in case the report set was imported in a prior run.
        var reportNames = (await _reports.GetAllReportsAsync())
            .Where(r => r.CompanyId == companyId)
            .ToDictionary(r => r.Id, r => (r.InternalName, r.Name));

        var entry = new PromotionPackage.DashboardEntry
        {
            SourceCompanyId = companyId,
            SourceCompanyName = companyById.GetValueOrDefault(companyId)?.Name ?? "(unknown)"
        };

        foreach (var tab in tabs.OrderBy(t => t.SortOrder))
        {
            var sections = await _dashboards.GetSectionsAsync(tab.Id);
            var sectionLabelById = sections.ToDictionary(s => s.Id, s => s.Label);
            var tiles = await _dashboards.GetTilesAsync(companyId, tab.Id);

            var tabEntry = new PromotionPackage.TabEntry
            {
                Label = tab.Label,
                SortOrder = tab.SortOrder,
                TitleAlign = tab.TitleAlign,
                Sections = sections.OrderBy(s => s.SortOrder).Select(s => new PromotionPackage.SectionEntry
                {
                    Label = s.Label,
                    SortOrder = s.SortOrder,
                    TitleAlign = s.TitleAlign,
                    Collapsed = s.Collapsed
                }).ToList()
            };

            foreach (var tile in tiles.OrderBy(t => t.SortOrder))
            {
                var names = reportNames.GetValueOrDefault(tile.ReportId);
                tabEntry.Tiles.Add(new PromotionPackage.TileEntry
                {
                    SourceReportId = tile.ReportId,
                    ReportInternalName = names.InternalName,
                    ReportName = names.Name ?? tile.ReportName,
                    SortOrder = tile.SortOrder,
                    ColSpan = tile.ColSpan,
                    Height = tile.Height,
                    SectionLabel = tile.SectionId is int sid ? sectionLabelById.GetValueOrDefault(sid) : null
                });
            }

            entry.Tabs.Add(tabEntry);
        }

        pkg.Dashboards.Add(entry);
    }

    // ════════════════════════════════════════════════════════════════════
    // Parse
    // ════════════════════════════════════════════════════════════════════

    public PromotionPackage Parse(byte[] packageBytes)
    {
        if (packageBytes is null || packageBytes.Length == 0)
            throw new InvalidOperationException("Package is empty.");

        PromotionPackage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PromotionPackage>(packageBytes, AppJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Package isn't valid JSON: {ex.Message}", ex);
        }
        if (parsed is null)
            throw new InvalidOperationException("Package deserialized to null.");

        if (parsed.PackageVersion != PromotionPackage.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Package version {parsed.PackageVersion} isn't supported by this build " +
                $"(expected {PromotionPackage.CurrentVersion}). Update the importing app or re-export from a matching staging build.");
        }

        // Source-environment allowlist. Empty config = accept anything.
        // Production's appsettings should pin this to "STAGING" so a
        // bundle exported from prod (sneakernet'd back) can't reapply.
        var allowedCsv = _configuration["Promotion:AllowedSourceEnvironments"];
        if (!string.IsNullOrWhiteSpace(allowedCsv))
        {
            var allowed = allowedCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!allowed.Contains(parsed.SourceEnvironment))
            {
                throw new InvalidOperationException(
                    $"Package source environment '{parsed.SourceEnvironment}' isn't in the allowed list for this instance ({allowedCsv}).");
            }
        }

        return parsed;
    }

    // ════════════════════════════════════════════════════════════════════
    // Import — schema configs (per-connection mapping, unchanged)
    // ════════════════════════════════════════════════════════════════════

    public async Task<ImportResult> ImportSchemaConfigAsync(
        PromotionPackage.SchemaConfigEntry entry,
        Guid targetConnectionId,
        string? importedBy,
        CancellationToken ct = default)
    {
        if (entry?.Schema is null)
            return new ImportResult(false, "Entry has no schema payload.");

        // Round-trip through JSON so the saved instance doesn't share
        // memory with the package's copy — same defensive copy the
        // schema store's Clone path uses.
        var json = JsonSerializer.Serialize(entry.Schema, AppJson.Indented);
        var copy = JsonSerializer.Deserialize<SchemaConfig>(json, AppJson.Indented) ?? new SchemaConfig();

        try
        {
            await _schemaStore.SaveAsync(
                copy,
                targetConnectionId,
                importedBy is null
                    ? $"promotion-import:{entry.SourceConnectionName}"
                    : $"promotion-import:{entry.SourceConnectionName} (by {importedBy})");
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Schema config import failed for target {TargetId}", targetConnectionId);
            return new ImportResult(false, $"Database error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema config import failed for target {TargetId}", targetConnectionId);
            return new ImportResult(false, ex.Message);
        }

        return new ImportResult(true,
            $"Imported schema with {copy.Fields.Count} fields, {copy.Joins.Count} joins.");
    }

    // ════════════════════════════════════════════════════════════════════
    // Import — company-scoped (companies, sections, templates, reports,
    // dashboards). One pass; cross-references resolve through the maps.
    // ════════════════════════════════════════════════════════════════════

    public async Task<PromotionImportReport> ImportCompanyScopedAsync(
        PromotionPackage package,
        IReadOnlyList<CompanyImportMapping> companyMappings,
        string? importedBy,
        CancellationToken ct = default)
    {
        var messages = new List<string>();
        int companiesCreated = 0, companiesMatched = 0, sectionCount = 0,
            templateCount = 0, reportCount = 0, tabCount = 0, tileCount = 0;

        // Resolve the company map first: sourceCompanyId → targetCompanyId.
        // Everything downstream is gated on a company having a resolved target.
        var existingCompanies = await _companyAdmin.GetAllAsync(ct);
        var targetBySource = new Dictionary<Guid, Guid>();
        var ownerEmail = importedBy ?? "promotion-import";

        foreach (var map in companyMappings)
        {
            if (map.TargetCompanyId is Guid target)
            {
                targetBySource[map.SourceCompanyId] = target;
                companiesMatched++;
                continue;
            }
            if (!map.CreateIfMissing) continue;

            var entry = package.Companies.FirstOrDefault(c => c.SourceId == map.SourceCompanyId);
            if (entry is null)
            {
                messages.Add($"✗ Can't create company for source {map.SourceCompanyId} — package didn't include its definition.");
                continue;
            }
            // Re-match by code in case it already exists under a different
            // GUID (idempotent re-import).
            var byCode = existingCompanies.FirstOrDefault(c =>
                string.Equals(c.Code, entry.Code, StringComparison.OrdinalIgnoreCase));
            if (byCode is not null)
            {
                targetBySource[map.SourceCompanyId] = byCode.Id;
                companiesMatched++;
                messages.Add($"• Company '{entry.Name}' matched existing by code '{entry.Code}'.");
                continue;
            }
            try
            {
                var created = await _companyAdmin.CreateAsync(entry.Code, entry.Name, entry.WebsiteUrl, importedBy, ct);
                targetBySource[map.SourceCompanyId] = created.Id;
                companiesCreated++;
                messages.Add($"✓ Created company '{entry.Name}' ({entry.Code}).");
            }
            catch (Exception ex)
            {
                messages.Add($"✗ Failed to create company '{entry.Name}': {ex.Message}");
            }
        }

        // Per target company, cache its connections + library sections so we
        // resolve names without re-querying inside the report loop.
        var connectionsByCompany = new Dictionary<Guid, List<CompanyConnectionRecord>>();
        async Task<List<CompanyConnectionRecord>> ConnsAsync(Guid companyId)
        {
            if (!connectionsByCompany.TryGetValue(companyId, out var list))
            {
                list = await _connections.GetByCompanyAsync(companyId, ct);
                connectionsByCompany[companyId] = list;
            }
            return list;
        }

        // sourceConnectionId → targetConnectionId, resolved by name within the
        // mapped target company. Connections themselves never travel.
        var connTargetBySource = new Dictionary<Guid, Guid>();
        async Task<Guid?> ResolveConnAsync(Guid sourceCompanyId, Guid? sourceConnId, string? sourceConnName)
        {
            if (!targetBySource.TryGetValue(sourceCompanyId, out var targetCompany))
                return null;
            var conns = await ConnsAsync(targetCompany);
            if (sourceConnId is Guid sc && connTargetBySource.TryGetValue(sc, out var cached))
                return cached;
            CompanyConnectionRecord? match = null;
            if (!string.IsNullOrWhiteSpace(sourceConnName))
                match = conns.FirstOrDefault(c => string.Equals(c.Name, sourceConnName, StringComparison.OrdinalIgnoreCase));
            match ??= conns.FirstOrDefault(c => c.IsDefault) ?? conns.FirstOrDefault();
            if (match is null) return null;
            if (sourceConnId is Guid s) connTargetBySource[s] = match.Id;
            return match.Id;
        }

        // ── Library sections: match-or-create by (target company, name) ──
        var sectionTargetBySource = new Dictionary<Guid, Guid>();
        foreach (var sec in package.LibrarySections)
        {
            if (!targetBySource.TryGetValue(sec.SourceCompanyId, out var targetCompany)) continue;
            try
            {
                var existing = await _librarySections.GetSectionsAsync(targetCompany);
                var match = existing.FirstOrDefault(s =>
                    string.Equals(s.Name, sec.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    sectionTargetBySource[sec.SourceId] = match.Id;
                }
                else
                {
                    var created = await _librarySections.CreateSectionAsync(targetCompany, sec.Name, sec.SortOrder);
                    sectionTargetBySource[sec.SourceId] = created.Id;
                    sectionCount++;
                    messages.Add($"✓ Library section '{sec.Name}' created.");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"✗ Library section '{sec.Name}' failed: {ex.Message}");
            }
        }

        // ── Grid templates: match-or-create by (name, resolved connection) ──
        var templateTargetBySource = new Dictionary<Guid, Guid>();
        foreach (var tmpl in package.GridTemplates)
        {
            if (tmpl.SourceCompanyId is not Guid srcCompany ||
                !targetBySource.ContainsKey(srcCompany))
            {
                // Template's connection company isn't in the import scope —
                // reports referencing it will fall back to no template.
                continue;
            }
            var targetConnId = await ResolveConnAsync(srcCompany, tmpl.SourceConnectionId, tmpl.SourceConnectionName);
            try
            {
                var existing = await _gridTemplates.GetTemplatesAsync(ownerEmail, targetConnId);
                var match = existing.FirstOrDefault(t =>
                    string.Equals(t.Name, tmpl.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    templateTargetBySource[tmpl.SourceId] = match.Id;
                }
                else
                {
                    var created = await _gridTemplates.SaveTemplateAsync(new GridTemplate
                    {
                        Name = tmpl.Name,
                        Description = tmpl.Description,
                        OwnerId = ownerEmail,
                        OwnerEmail = ownerEmail,
                        IsShared = tmpl.IsShared,
                        FieldIds = tmpl.FieldIds,
                        ColumnState = tmpl.ColumnState,
                        ConnectionId = targetConnId
                    });
                    templateTargetBySource[tmpl.SourceId] = created.Id;
                    templateCount++;
                    messages.Add($"✓ Grid template '{tmpl.Name}' created.");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"✗ Grid template '{tmpl.Name}' failed: {ex.Message}");
            }
        }

        // ── Reports: create-if-absent by (target company, internalName||name).
        // Owner reassigned to the importer. Present reports are left untouched
        // (a no-op re-import) so promotion never clobbers a hand-edited prod
        // report; the message says so. ──
        var reportTargetBySource = new Dictionary<Guid, Guid>();
        var reportTargetByInternalName = new Dictionary<(Guid Company, string Key), Guid>();
        var allTargetReports = await _reports.GetAllReportsAsync();
        foreach (var rep in package.Reports)
        {
            if (!targetBySource.TryGetValue(rep.SourceCompanyId, out var targetCompany))
            {
                messages.Add($"• Report '{rep.Name}' skipped — its company isn't mapped.");
                continue;
            }
            var key = (rep.InternalName ?? rep.Name)?.Trim() ?? rep.Name;
            var existing = allTargetReports.FirstOrDefault(r =>
                r.CompanyId == targetCompany &&
                string.Equals((r.InternalName ?? r.Name)?.Trim(), key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                reportTargetBySource[rep.SourceId] = existing.Id;
                reportTargetByInternalName[(targetCompany, key.ToLowerInvariant())] = existing.Id;
                messages.Add($"• Report '{rep.Name}' already present — left unchanged.");
                continue;
            }

            var targetConnId = await ResolveConnAsync(rep.SourceCompanyId, rep.SourceConnectionId, rep.SourceConnectionName);
            if (targetConnId is null)
            {
                messages.Add($"✗ Report '{rep.Name}' skipped — no matching connection '{rep.SourceConnectionName}' in target company.");
                continue;
            }

            Guid? gridTemplateId = rep.SourceGridTemplateId is Guid gt &&
                templateTargetBySource.TryGetValue(gt, out var mappedT) ? mappedT : null;
            Guid? librarySectionId = rep.SourceLibrarySectionId is Guid ls &&
                sectionTargetBySource.TryGetValue(ls, out var mappedS) ? mappedS : null;

            try
            {
                var saved = await _reports.SaveReportAsync(new SavedReport
                {
                    Name = rep.Name,
                    InternalName = rep.InternalName,
                    Category = rep.Category,
                    LibrarySectionId = librarySectionId,
                    OwnerId = ownerEmail,
                    OwnerEmail = ownerEmail,
                    CompanyId = targetCompany,
                    FieldIds = rep.FieldIds,
                    Filters = rep.Filters,
                    Aggregations = rep.Aggregations,
                    ColumnState = rep.ColumnState,
                    GridTemplateId = gridTemplateId,
                    ConnectionId = targetConnId,
                    PrimaryTable = rep.PrimaryTable
                });
                reportTargetBySource[rep.SourceId] = saved.Id;
                reportTargetByInternalName[(targetCompany, key.ToLowerInvariant())] = saved.Id;
                reportCount++;
                messages.Add($"✓ Report '{rep.Name}' imported.");
            }
            catch (Exception ex)
            {
                messages.Add($"✗ Report '{rep.Name}' failed: {ex.Message}");
            }
        }

        // ── Dashboards: additive rebuild. Tabs/sections matched by label,
        // tiles deduped by (tab, report) in the service. Tiles resolve their
        // report through the source-id map, then the internal-name map. ──
        foreach (var dash in package.Dashboards)
        {
            if (!targetBySource.TryGetValue(dash.SourceCompanyId, out var targetCompany))
            {
                messages.Add($"• Dashboard for '{dash.SourceCompanyName}' skipped — company not mapped.");
                continue;
            }

            // Refresh target reports for name-based tile resolution (reports
            // imported just above are now persisted).
            var targetReports = (await _reports.GetAllReportsAsync())
                .Where(r => r.CompanyId == targetCompany).ToList();

            try
            {
                var existingTabs = await _dashboards.GetTabsAsync(targetCompany);
                foreach (var tabEntry in dash.Tabs.OrderBy(t => t.SortOrder))
                {
                    var tab = existingTabs.FirstOrDefault(t =>
                        string.Equals(t.Label, tabEntry.Label, StringComparison.OrdinalIgnoreCase));
                    if (tab is null)
                    {
                        tab = await _dashboards.AddTabAsync(targetCompany, tabEntry.Label, importedBy);
                        existingTabs.Add(tab);
                        tabCount++;
                        if (!string.Equals(tabEntry.TitleAlign, "left", StringComparison.OrdinalIgnoreCase))
                        {
                            tab.TitleAlign = tabEntry.TitleAlign;
                            await _dashboards.UpdateTabAsync(tab, importedBy);
                        }
                    }

                    // Sections under this tab — match-or-create by label.
                    var existingSections = await _dashboards.GetSectionsAsync(tab.Id);
                    var sectionIdByLabel = existingSections.ToDictionary(
                        s => s.Label, s => s.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (var secEntry in tabEntry.Sections.OrderBy(s => s.SortOrder))
                    {
                        if (sectionIdByLabel.ContainsKey(secEntry.Label)) continue;
                        var created = await _dashboards.AddSectionAsync(tab.Id, secEntry.Label, importedBy);
                        sectionIdByLabel[secEntry.Label] = created.Id;
                        if (!string.Equals(secEntry.TitleAlign, "left", StringComparison.OrdinalIgnoreCase))
                            await _dashboards.SetSectionAlignAsync(created.Id, secEntry.TitleAlign, importedBy);
                        if (secEntry.Collapsed)
                            await _dashboards.SetSectionCollapsedAsync(created.Id, true, importedBy);
                    }

                    foreach (var tileEntry in tabEntry.Tiles.OrderBy(t => t.SortOrder))
                    {
                        var reportId = ResolveTileReport(tileEntry, targetCompany,
                            reportTargetBySource, reportTargetByInternalName, targetReports);
                        if (reportId is null)
                        {
                            messages.Add($"• Tile for report '{tileEntry.ReportName ?? tileEntry.ReportInternalName}' skipped — report not in target.");
                            continue;
                        }
                        int? sectionId = tileEntry.SectionLabel is { } lbl &&
                            sectionIdByLabel.TryGetValue(lbl, out var sid) ? sid : null;
                        await _dashboards.AddTileAsync(targetCompany, tab.Id, reportId.Value, importedBy, tileEntry.ColSpan, sectionId);
                        tileCount++;
                    }
                }
                messages.Add($"✓ Dashboard for '{dash.SourceCompanyName}' applied.");
            }
            catch (Exception ex)
            {
                messages.Add($"✗ Dashboard for '{dash.SourceCompanyName}' failed: {ex.Message}");
            }
        }

        _logger.LogInformation(
            "Company-scoped import by {User}: {Created} companies created, {Matched} matched, {Sections} sections, {Templates} templates, {Reports} reports, {Tabs} tabs, {Tiles} tiles",
            importedBy ?? "unknown", companiesCreated, companiesMatched, sectionCount,
            templateCount, reportCount, tabCount, tileCount);

        return new PromotionImportReport(
            companiesCreated, companiesMatched, sectionCount, templateCount,
            reportCount, tabCount, tileCount, messages);
    }

    private static Guid? ResolveTileReport(
        PromotionPackage.TileEntry tile,
        Guid targetCompany,
        IReadOnlyDictionary<Guid, Guid> bySource,
        IReadOnlyDictionary<(Guid, string), Guid> byInternalName,
        List<SavedReport> targetReports)
    {
        if (bySource.TryGetValue(tile.SourceReportId, out var mapped)) return mapped;

        var key = (tile.ReportInternalName ?? tile.ReportName)?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            if (byInternalName.TryGetValue((targetCompany, key.ToLowerInvariant()), out var byName))
                return byName;
            var match = targetReports.FirstOrDefault(r =>
                string.Equals((r.InternalName ?? r.Name)?.Trim(), key, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Id;
        }
        return null;
    }
}
