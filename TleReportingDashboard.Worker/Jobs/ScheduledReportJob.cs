using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;
using TleReportingDashboard.Web.Services;
using TleReportingDashboard.Web.Services.QueryPipeline;

namespace TleReportingDashboard.Worker.Jobs;

public sealed class ScheduledReportJob
{
    private const int MaxConsecutiveFailuresBeforeDeactivation = 3;

    private readonly string _connectionString;
    private readonly IQueryPipeline _queryPipeline;
    private readonly IExportService _exportService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notifications;
    private readonly ICompanyRegistry _companies;
    private readonly ITeamSourceService _teams;
    private readonly ISchemaConfigStore _schemaConfigStore;
    private readonly IAdminService _admins;
    private readonly ILogger<ScheduledReportJob> _logger;

    public ScheduledReportJob(
        IConfiguration configuration,
        IQueryPipeline queryPipeline,
        IExportService exportService,
        IEmailService emailService,
        INotificationService notifications,
        ICompanyRegistry companies,
        ITeamSourceService teams,
        ISchemaConfigStore schemaConfigStore,
        IAdminService admins,
        ILogger<ScheduledReportJob> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _queryPipeline = queryPipeline ?? throw new ArgumentNullException(nameof(queryPipeline));
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _companies = companies ?? throw new ArgumentNullException(nameof(companies));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _schemaConfigStore = schemaConfigStore ?? throw new ArgumentNullException(nameof(schemaConfigStore));
        _admins = admins ?? throw new ArgumentNullException(nameof(admins));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Per-recipient outcome captured during a fan-out so the run can
    // be summarized in last_run_status afterwards. Status is one of
    // "Success", "Skipped", "Failed".
    private sealed record RecipientOutcome(
        string Identifier,  // email when known; ext_id when no app user mapped
        string Status,
        int? RowCount,
        string? Detail);

    // Backward-compat overload — kept so any recurring job already
    // registered with the 1-arg signature (i.e., serialized in
    // HangFire.Hash before this change) keeps deserializing on its
    // next fire. SchedulerSyncService overwrites every registration
    // to the 2-arg version on the next poll, so this overload only
    // matters for the brief window after deploy.
    public Task ExecuteAsync(Guid scheduleId) =>
        ExecuteAsync(scheduleId, string.Empty);

    // [DisplayName] makes the Hangfire dashboard render the friendly
    // displayName instead of "ScheduledReportJob.ExecuteAsync(...)".
    // {1} is the second argument; SchedulerSyncService passes the
    // saved report's Name when it registers the recurring job, so the
    // dashboard reads "Scheduled run: Pipeline Report" instead of the
    // raw method signature.
    [DisplayName("Scheduled run: {1}")]
    public async Task ExecuteAsync(Guid scheduleId, string displayName)
    {
        _logger.LogInformation(
            "Starting scheduled report execution for ScheduleId={ScheduleId} ({DisplayName})",
            scheduleId, string.IsNullOrWhiteSpace(displayName) ? "no label" : displayName);

        var schedule = await GetScheduleAsync(scheduleId);
        if (schedule is null)
        {
            _logger.LogWarning("Schedule {ScheduleId} not found — skipping execution", scheduleId);
            return;
        }

        if (!schedule.IsActive)
        {
            _logger.LogInformation("Schedule {ScheduleId} is inactive — skipping execution", scheduleId);
            return;
        }

        try
        {
            var savedReport = await GetSavedReportAsync(schedule.ReportId);
            if (savedReport is null)
            {
                throw new InvalidOperationException(
                    $"SavedReport {schedule.ReportId} referenced by schedule {scheduleId} was not found.");
            }

            var subject = await PrefixSubjectWithCompanyAsync(savedReport.CompanyId, schedule.Subject);

            // Branch on Kind. Distribution = single email to a shared
            // mailbox with no per-recipient scoping. Individual = fan
            // out to every Team Builder member, scope-resolved per
            // recipient via IQueryScopeResolver. Each path returns the
            // same RecipientOutcome list shape so the post-run summary
            // / deactivation logic is shared.
            var outcomes = schedule.Kind switch
            {
                ScheduleKind.Individual =>
                    await RunIndividualAsync(scheduleId, schedule, savedReport, subject),
                _ =>
                    await RunDistributionAsync(scheduleId, schedule, savedReport, subject),
            };

            // Aggregate. Auto-deactivation only counts a run as failed
            // when *no* recipient succeeded — a single bad mailbox in a
            // 50-person fan-out shouldn't deactivate everyone else's
            // report. Empty outcomes also count as "not a failure"
            // because the Individual path silent-skips members with
            // no rows; a schedule firing on a quiet day where nobody
            // had data isn't broken, it's just quiet.
            var anySucceeded = outcomes.Any(o => o.Status == "Success");
            var noAttempts = outcomes.Count == 0;
            var failures = (anySucceeded || noAttempts) ? 0 : schedule.ConsecutiveFailures + 1;
            var isActive = schedule.IsActive && failures < MaxConsecutiveFailuresBeforeDeactivation;
            var headline = BuildHeadline(outcomes);

            if (!anySucceeded && failures >= MaxConsecutiveFailuresBeforeDeactivation)
            {
                _logger.LogWarning(
                    "Schedule {ScheduleId} auto-deactivated after {Failures} consecutive whole-run failures",
                    scheduleId, failures);

                await SendFailureNotificationAsync(schedule, failures);
                try
                {
                    await _notifications.CreateAsync(
                        userEmail:         schedule.OwnerEmail,
                        kind:              NotificationKinds.ScheduleFailed,
                        title:             "Scheduled report deactivated",
                        body:              $"\"{schedule.Subject}\" was deactivated after {failures} consecutive failures. Last status: {headline}",
                        linkUrl:           $"/viewer/{schedule.ReportId}",
                        relatedEntityType: "schedule",
                        relatedEntityId:   scheduleId.ToString());
                }
                catch (Exception nex)
                {
                    _logger.LogWarning(nex,
                        "Schedule deactivation notification failed for ScheduleId={ScheduleId}",
                        scheduleId);
                }
            }

            await UpdateScheduleStatusAsync(scheduleId, headline, failures, isActive);

            _logger.LogInformation(
                "Scheduled report completed: ScheduleId={ScheduleId}, Report={ReportName}, Outcome={Headline}",
                scheduleId, savedReport.Name, headline);
        }
        catch (Exception ex)
        {
            // Outer-shell failure (saved report missing, the kind
            // dispatch itself threw, etc.) — escalates the whole run
            // as a failure regardless of fan-out semantics.
            _logger.LogError(ex,
                "Scheduled report failed at outer shell: ScheduleId={ScheduleId}, Attempt={Attempt}",
                scheduleId, schedule.ConsecutiveFailures + 1);

            var failures = schedule.ConsecutiveFailures + 1;
            var isActive = schedule.IsActive && failures < MaxConsecutiveFailuresBeforeDeactivation;
            await UpdateScheduleStatusAsync(scheduleId, $"Failed: {ex.Message}", failures, isActive);
        }
    }

    // ── Distribution: one email to a shared mailbox ──

    // The legacy "owner-email only" path is folded in here: a row
    // with Kind=Distribution and DistEmail null is read as a pre-
    // migration schedule and falls through to schedule.OwnerEmail.
    // Drop the OwnerEmail fallback once every row has been re-saved
    // through the new dialog.
    private async Task<List<RecipientOutcome>> RunDistributionAsync(
        Guid scheduleId, ReportSchedule schedule, SavedReport savedReport, string subject)
    {
        var target = !string.IsNullOrWhiteSpace(schedule.DistEmail)
            ? schedule.DistEmail!
            : schedule.OwnerEmail;
        if (string.IsNullOrWhiteSpace(target))
        {
            return new List<RecipientOutcome>
            {
                new("(no recipient)", "Failed", null, "Schedule has neither dist_email nor owner_email set.")
            };
        }

        try
        {
            var request = BuildQueryRequest(savedReport);
            var response = await _queryPipeline.ExecuteAsync(request, Array.Empty<string>());

            var (bytes, fileName) = FormatAttachment(
                BuildExportData(response, savedReport.ColumnState),
                savedReport.Name, schedule.AttachmentFormat);
            var html = BuildEmailBody(savedReport.Name, response.TotalCount, schedule.IncludePreview, response.Sql);

            await _emailService.SendReportEmailAsync(target, subject, html, bytes, fileName);

            // Owner notification only when the schedule has a real
            // owner. A pure dist-list address has no app user behind
            // it, so the bell-icon notification has nowhere to land.
            await TryNotifyOwnerAsync(scheduleId, schedule, savedReport, response.TotalCount, target);

            return new List<RecipientOutcome>
            {
                new(target, "Success", response.TotalCount, null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Distribution send failed for ScheduleId={ScheduleId} target={Target}",
                scheduleId, target);
            return new List<RecipientOutcome>
            {
                new(target, "Failed", null, ex.Message)
            };
        }
    }

    // ── Individual: per-team-member personalized fan-out ──

    private async Task<List<RecipientOutcome>> RunIndividualAsync(
        Guid scheduleId, ReportSchedule schedule, SavedReport savedReport, string subject)
    {
        var outcomes = new List<RecipientOutcome>();

        if (schedule.TeamConnectionId is not Guid teamConnId || schedule.TeamId is not int teamId)
        {
            outcomes.Add(new("(no team)", "Failed", null,
                "Schedule is Individual but team_id / team_connection_id are not set."));
            return outcomes;
        }

        // Fetch the full members roster for the connection and filter
        // client-side to this team — same pattern the Team Builder
        // preview uses and avoids splicing a WHERE into the admin's
        // members SQL.
        List<TeamMemberRecord> members;
        try
        {
            var all = await _teams.QueryMembersAsync(teamConnId);
            members = all.Where(m => m.TeamId == teamId).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Couldn't load team members for ScheduleId={ScheduleId} team={TeamId}",
                scheduleId, teamId);
            outcomes.Add(new($"team {teamId}", "Failed", null,
                $"Team members SQL failed: {ex.Message}"));
            return outcomes;
        }

        if (members.Count == 0)
        {
            outcomes.Add(new($"team {teamId}", "Failed", null,
                $"Team {teamId} returned no members on connection {teamConnId}."));
            return outcomes;
        }

        // User Emails SQL is the authoritative source for member→email
        // — Individual schedules deliberately don't consult RPT_users
        // (those rows are for dashboard-login scope, a different
        // concern). If user_emails_sql isn't configured for this
        // connection, the run can't fan out, so fail the whole
        // schedule with a clear surfaced reason instead of silently
        // skipping every recipient.
        Dictionary<string, string> emailByExtId =
            new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var emailRows = await _teams.QueryUserEmailsAsync(teamConnId);
            foreach (var row in emailRows)
                emailByExtId[row.MemberExtId] = row.Email;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "User Emails SQL failed for ScheduleId={ScheduleId} connection={Conn}",
                scheduleId, teamConnId);
            outcomes.Add(new($"team {teamId}", "Failed", null,
                $"User Emails SQL failed: {ex.Message}"));
            return outcomes;
        }

        if (emailByExtId.Count == 0)
        {
            outcomes.Add(new($"team {teamId}", "Failed", null,
                "User Emails SQL is not configured (or returned no rows) for this connection. " +
                "Configure it in Admin → Team Builder → User Emails SQL."));
            return outcomes;
        }

        // Resolve the team's row at runtime so we can derive its
        // team_type and the corresponding owner column. Both feed the
        // forced direct-column scope below so each recipient's report
        // is filtered to "rows where {team_type's column} = {ext_id}"
        // — independent of any RPT_users role / scope-rule. Self-scope
        // is forced for Individual schedules regardless of what the
        // dashboard role would say for the same person.
        TeamRecord? team;
        string? ownerColumn;
        try
        {
            var teams = await _teams.QueryTeamsAsync(teamConnId);
            team = teams.FirstOrDefault(t => t.TeamId == teamId);
            if (team is null)
            {
                outcomes.Add(new($"team {teamId}", "Failed", null,
                    $"Team {teamId} not returned by Teams SQL on connection {teamConnId}."));
                return outcomes;
            }
            var typeColumns = await _teams.GetTypeColumnsAsync(teamConnId);
            if (string.IsNullOrWhiteSpace(team.TeamType)
                || !typeColumns.TryGetValue(team.TeamType, out ownerColumn)
                || string.IsNullOrWhiteSpace(ownerColumn))
            {
                outcomes.Add(new($"team {teamId}", "Failed", null,
                    $"No owner-column mapped for team type '{team.TeamType ?? "(none)"}'. " +
                    "Add it in Admin → Team Builder → Team type → owner column."));
                return outcomes;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Team type → column lookup failed for ScheduleId={ScheduleId} team={TeamId}",
                scheduleId, teamId);
            outcomes.Add(new($"team {teamId}", "Failed", null,
                $"Team type/column lookup failed: {ex.Message}"));
            return outcomes;
        }

        // Compute the primary alias the same way QueryScopeResolver
        // does for team-scope, so a bare owner column gets qualified
        // with the right alias when the emitter renders the predicate.
        var (primaryTableName, primaryAliasParsed) = PrimaryTableRef.Parse(savedReport.PrimaryTable);
        var primaryAlias = !string.IsNullOrWhiteSpace(primaryAliasParsed)
            ? primaryAliasParsed!
            : BareTableName(primaryTableName);

        // Find the schema field whose source column matches the
        // team-type's owner column. The Worker uses this field's id
        // to extract owner values from the projected query result and
        // group rows by recipient. Without it we can't identify "who
        // owns this row" in memory, so fail-closed with a clear hint.
        var schema = _schemaConfigStore.GetForConnection(teamConnId);
        var ownerColumnBare = ownerColumn.Contains('.')
            ? ownerColumn[(ownerColumn.IndexOf('.') + 1)..]
            : ownerColumn;
        var ownerField = schema.Fields.FirstOrDefault(f =>
            string.Equals(f.SourceColumn, ownerColumnBare, StringComparison.OrdinalIgnoreCase));
        if (ownerField is null || string.IsNullOrWhiteSpace(ownerField.Id))
        {
            outcomes.Add(new($"team {teamId}", "Failed", null,
                $"No schema field maps to owner column '{ownerColumn}'. " +
                "Add a field whose source column matches in Admin → Schema Builder."));
            return outcomes;
        }

        var baseRequest = BuildQueryRequest(savedReport);

        // The owner field has to be in the report's projection or we
        // can't read its value out of each row dictionary at fan-out
        // time. Fail-closed with a clear pointer rather than silently
        // delivering everyone the full unfiltered set.
        var hasOwnerField = baseRequest.FieldIds is not null
            && baseRequest.FieldIds.Any(id =>
                string.Equals(id, ownerField.Id, StringComparison.OrdinalIgnoreCase));
        if (!hasOwnerField)
        {
            outcomes.Add(new($"team {teamId}", "Failed", null,
                $"Report doesn't include the '{ownerField.Id}' field — needed to identify each row's owner. " +
                "Add it to the report's selected fields and re-save."));
            return outcomes;
        }

        // Single-pass scope: WHERE owner_column IN (team's ext_ids).
        // Pulls every team member's rows in one round trip; we group
        // in memory below. Owner values that aren't on this team
        // (orphan loans assigned outside the team) are excluded by
        // the predicate, so they don't even need to be filtered out
        // client-side.
        var teamExtIds = members
            .Select(m => m.MemberExtId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        baseRequest.Scoping = new QueryScopingInfo
        {
            OwnerColumn = ownerColumn,
            PrimaryAlias = primaryAlias,
            ExternalUserIds = teamExtIds,
            Reason = $"Multi-scope for team {teamId} ({team.TeamType}) — {teamExtIds.Count} possible owners."
        };

        _logger.LogInformation(
            "Individual fan-out plan: ScheduleId={ScheduleId} team={TeamId} type={TeamType} ownerColumn='{OwnerColumn}' ownerFieldId='{OwnerFieldId}' primaryAlias='{PrimaryAlias}' members={Count}",
            scheduleId, teamId, team.TeamType ?? "(null)", ownerColumn, ownerField.Id, primaryAlias, members.Count);

        QueryResponse response;
        try
        {
            response = await _queryPipeline.ExecuteAsync(baseRequest, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Single-pass query failed for ScheduleId={ScheduleId}", scheduleId);

            // Debug: emit the failing SQL into a notification email to the
            // schedule owner so they can see the exact query the pipeline
            // tried. QueryExecutionException carries Sql + Parameters
            // alongside the inner message; non-QEE exceptions only have
            // the message. Temporary; remove when the team-scope SQL gap
            // is fully diagnosed.
            var debugSql = ex is TleReportingDashboard.Web.Services.QueryPipeline.QueryExecutionException qex
                ? qex.Sql
                : null;
            await TrySendDebugFailureEmailAsync(scheduleId, schedule, $"team {teamId}", ex.Message, debugSql);

            outcomes.Add(new($"team {teamId}", "Failed", null,
                $"Query failed: {ex.Message}"));
            return outcomes;
        }

        _logger.LogInformation(
            "Single-pass query returned {Rows} row(s) for ScheduleId={ScheduleId}",
            response.TotalCount, scheduleId);

        // Fan-out branches per schedule.TeamFanout. Members keeps the
        // legacy per-owner slice. Manager sends the entire team's roll-up
        // (or unfiltered, when the manager has all-access) to the team's
        // manager email. Both runs Members + Manager and concatenates
        // outcomes — the manager appears alongside the per-owner rows in
        // the run summary.
        var fanout = schedule.TeamFanout;

        if (fanout == TeamFanout.Members || fanout == TeamFanout.Both)
        {
            await SendToMembersAsync(scheduleId, schedule, savedReport, subject,
                                     response, ownerField, emailByExtId, outcomes);
        }

        if (fanout == TeamFanout.Manager || fanout == TeamFanout.Both)
        {
            await SendToManagerAsync(scheduleId, schedule, savedReport, subject,
                                     team, baseRequest, response, emailByExtId, outcomes);
        }

        return outcomes;
    }

    // Members fan-out — group the team-IN result by owner, send each
    // owner their own slice. Extracted so RunIndividualAsync can share
    // it with the Both branch without duplicating the loop.
    private async Task SendToMembersAsync(
        Guid scheduleId,
        ReportSchedule schedule,
        SavedReport savedReport,
        string subject,
        QueryResponse response,
        TleReportingDashboard.Web.Configuration.FieldDefinition ownerField,
        Dictionary<string, string> emailByExtId,
        List<RecipientOutcome> outcomes)
    {
        // Group rows by the owner field's value. Rows with a null /
        // blank owner (shouldn't happen given the IN predicate, but
        // defensive) are dropped.
        var rowsByOwner = response.Rows
            .GroupBy(r =>
                r.TryGetValue(ownerField.Id, out var v) ? v?.ToString()?.Trim() : null,
                StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (rowsByOwner.Count == 0)
        {
            // Nobody on the team owns any rows in this run — quiet
            // success, not a failure. BuildHeadline picks up the
            // empty-outcomes case and writes the "no recipients had
            // data this run" status.
            return;
        }

        foreach (var (extId, ownerRows) in rowsByOwner)
        {
            if (!emailByExtId.TryGetValue(extId, out var recipientEmail)
                || string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogWarning(
                    "Owner ext_id={ExtId} on ScheduleId={ScheduleId} has {Rows} row(s) but no email in user_emails_sql",
                    extId, scheduleId, ownerRows.Count);
                outcomes.Add(new(extId, "Skipped", ownerRows.Count,
                    $"Has {ownerRows.Count} row(s) but no email returned by User Emails SQL."));
                continue;
            }

            try
            {
                // Build a partial response containing just this
                // owner's slice. Reuses the columns/metadata from the
                // single-pass response so the export formatting
                // (column order, hidden columns, etc.) is identical.
                var partial = new QueryResponse
                {
                    Columns = response.Columns,
                    Rows = ownerRows,
                    TotalCount = ownerRows.Count
                };

                var (bytes, fileName) = FormatAttachment(
                    BuildExportData(partial, savedReport.ColumnState),
                    savedReport.Name, schedule.AttachmentFormat);
                var html = BuildEmailBody(savedReport.Name, partial.TotalCount, schedule.IncludePreview, response.Sql);

                await _emailService.SendReportEmailAsync(recipientEmail, subject, html, bytes, fileName);

                outcomes.Add(new(recipientEmail, "Success", partial.TotalCount, null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Send failed for ScheduleId={ScheduleId} ext_id={ExtId} email={Email}",
                    scheduleId, extId, recipientEmail);
                outcomes.Add(new(recipientEmail, "Failed", null, ex.Message));
            }
        }
    }

    // Manager fan-out — send the full team-IN result (roll-up) to the
    // team's manager email. If the manager is a global admin, re-run the
    // query without the team-IN scope so they receive the unfiltered
    // report instead. Outcomes are appended to the shared list so a Both
    // run surfaces manager + members together in last_run_status.
    private async Task SendToManagerAsync(
        Guid scheduleId,
        ReportSchedule schedule,
        SavedReport savedReport,
        string subject,
        TeamRecord team,
        QueryRequest scopedRequest,
        QueryResponse scopedResponse,
        Dictionary<string, string> emailByExtId,
        List<RecipientOutcome> outcomes)
    {
        if (string.IsNullOrWhiteSpace(team.ManagerExtId))
        {
            outcomes.Add(new($"team {team.TeamId} manager", "Failed", null,
                $"Team {team.TeamId} has no manager_ext_id set in Teams SQL."));
            return;
        }

        if (!emailByExtId.TryGetValue(team.ManagerExtId, out var managerEmail)
            || string.IsNullOrWhiteSpace(managerEmail))
        {
            outcomes.Add(new(team.ManagerExtId, "Failed", null,
                $"Manager ext_id '{team.ManagerExtId}' has no email returned by User Emails SQL."));
            return;
        }

        // All-access bypass: a global-admin manager gets the unfiltered
        // report. Re-run with the same request shape but no team-IN
        // scope so cross-team rows aren't lost. Non-admin managers
        // receive the same scopedResponse the Members branch already
        // computed — no second round trip.
        QueryResponse outboundResponse;
        try
        {
            if (_admins.IsAdmin(managerEmail))
            {
                _logger.LogInformation(
                    "Manager fan-out: ScheduleId={ScheduleId} manager={Email} has all-access — sending unfiltered.",
                    scheduleId, managerEmail);
                var unscoped = new QueryRequest
                {
                    FieldIds = scopedRequest.FieldIds,
                    Filters = scopedRequest.Filters,
                    CustomFilterIds = scopedRequest.CustomFilterIds,
                    AdvancedFilters = scopedRequest.AdvancedFilters,
                    SortField = scopedRequest.SortField,
                    SortDirection = scopedRequest.SortDirection,
                    SecondarySortField = scopedRequest.SecondarySortField,
                    SecondarySortDirection = scopedRequest.SecondarySortDirection,
                    DisableDefaultSort = scopedRequest.DisableDefaultSort,
                    FallbackSortColumns = scopedRequest.FallbackSortColumns,
                    Distinct = scopedRequest.Distinct,
                    Page = scopedRequest.Page,
                    PageSize = scopedRequest.PageSize,
                    ConnectionId = scopedRequest.ConnectionId,
                    PrimaryTable = scopedRequest.PrimaryTable,
                    GroupByFieldIds = scopedRequest.GroupByFieldIds,
                    TableCalcSqlColumns = scopedRequest.TableCalcSqlColumns,
                    Scoping = null
                };
                outboundResponse = await _queryPipeline.ExecuteAsync(unscoped, Array.Empty<string>());
            }
            else
            {
                outboundResponse = scopedResponse;
            }

            var (bytes, fileName) = FormatAttachment(
                BuildExportData(outboundResponse, savedReport.ColumnState),
                savedReport.Name, schedule.AttachmentFormat);
            var html = BuildEmailBody(savedReport.Name, outboundResponse.TotalCount, schedule.IncludePreview, outboundResponse.Sql);

            await _emailService.SendReportEmailAsync(managerEmail, subject, html, bytes, fileName);

            outcomes.Add(new(managerEmail, "Success", outboundResponse.TotalCount, "manager"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Manager send failed for ScheduleId={ScheduleId} manager_ext_id={ExtId} email={Email}",
                scheduleId, team.ManagerExtId, managerEmail);
            outcomes.Add(new(managerEmail, "Failed", null, ex.Message));
        }
    }

    // Strip schema prefix and brackets from a "schema.table" reference
    // so the returned string can qualify a column in a WHERE clause.
    // Mirrors QueryScopeResolver.BareTableName so the fallback alias
    // behavior matches between the live and Worker paths.
    private static string BareTableName(string? rawTable)
    {
        if (string.IsNullOrWhiteSpace(rawTable)) return string.Empty;
        var dot = rawTable.LastIndexOf('.');
        var name = dot >= 0 ? rawTable[(dot + 1)..] : rawTable;
        return name.Trim('[', ']', ' ');
    }

    // ── Helpers ──

    // Compact one-line summary for last_run_status. Stays under the
    // 500-char column width even with many recipients — long detail
    // lists are truncated by UpdateScheduleStatusAsync. Silent skips
    // (team members with no rows) are intentionally absent from
    // outcomes by the time we build the headline, so they don't
    // pollute the status. Surfaced skips ("has rows but no email")
    // get their own clause separate from "failed" so admins can tell
    // a config gap from a real delivery failure.
    private static string BuildHeadline(IReadOnlyList<RecipientOutcome> outcomes)
    {
        if (outcomes.Count == 0)
            return "Ran — no recipients had data this run.";

        var ok = outcomes.Count(o => o.Status == "Success");
        var skipped = outcomes.Where(o => o.Status == "Skipped").ToList();
        var failed = outcomes.Where(o => o.Status == "Failed").ToList();

        var parts = new List<string> { $"{ok} sent" };
        if (skipped.Count > 0)
            parts.Add($"{skipped.Count} with rows but no email: "
                      + string.Join("; ", skipped.Take(3).Select(o => o.Identifier)));
        if (failed.Count > 0)
            parts.Add($"{failed.Count} failed: "
                      + string.Join("; ", failed.Take(3).Select(o => $"{o.Identifier} — {o.Detail}")));
        return string.Join(". ", parts);
    }

    private async Task TryNotifyOwnerAsync(
        Guid scheduleId, ReportSchedule schedule, SavedReport savedReport, int rowCount, string sentTo)
    {
        if (string.IsNullOrWhiteSpace(schedule.OwnerEmail)) return;
        try
        {
            await _notifications.CreateAsync(
                userEmail:         schedule.OwnerEmail,
                kind:              NotificationKinds.ScheduleRan,
                title:             $"\"{savedReport.Name}\" scheduled report ran",
                body:              $"Sent to {sentTo} with {rowCount:N0} rows.",
                linkUrl:           $"/viewer/{savedReport.Id}",
                relatedEntityType: "schedule",
                relatedEntityId:   scheduleId.ToString());
        }
        catch (Exception nex)
        {
            _logger.LogWarning(nex,
                "Owner notification failed for ScheduleId={ScheduleId}", scheduleId);
        }
    }

    // ── ADO.NET data access ──

    private async Task<ReportSchedule?> GetScheduleAsync(Guid scheduleId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // The kind / team / dist columns are read defensively via CASE
        // guards on COL_LENGTH so a Worker built against the new schema
        // can still run against a ConfigDB that hasn't applied the
        // 2026-05-05_14-30 migration yet — the row falls through to the
        // legacy owner-email distribution path in that case. Once every
        // env has the migration this can collapse to a plain SELECT of
        // the columns by name.
        await using var cmd = new SqlCommand(@"
            SELECT id, report_id, owner_id, owner_email, cron_expression, subject,
                   attachment_format, include_preview, is_active, last_run_at, consecutive_failures,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_report_schedules','kind') IS NULL
                        THEN CAST(NULL AS NVARCHAR(20)) ELSE kind END AS kind,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_report_schedules','team_id') IS NULL
                        THEN CAST(NULL AS INT) ELSE team_id END AS team_id,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_report_schedules','team_connection_id') IS NULL
                        THEN CAST(NULL AS UNIQUEIDENTIFIER) ELSE team_connection_id END AS team_connection_id,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_report_schedules','dist_email') IS NULL
                        THEN CAST(NULL AS NVARCHAR(255)) ELSE dist_email END AS dist_email,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_report_schedules','team_fanout') IS NULL
                        THEN CAST(NULL AS NVARCHAR(20)) ELSE team_fanout END AS team_fanout
              FROM EMPOWER.RPT_report_schedules
             WHERE id = @Id;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        // Resolve Kind from the persisted lowercase string ('individual'
        // / 'distribution'). Null / unknown values fall back to
        // Distribution so a pre-migration env reads as the legacy
        // single-recipient path.
        var kindRaw = r.IsDBNull(11) ? null : r.GetString(11);
        var kind = string.Equals(kindRaw, "individual", StringComparison.OrdinalIgnoreCase)
            ? ScheduleKind.Individual
            : ScheduleKind.Distribution;
        // Resolve TeamFanout from the persisted lowercase string. Null /
        // unknown falls back to Members so a pre-2026-05-09_17-00 env
        // reads as the legacy per-member fan-out behavior. Without this
        // mapping the Worker would always send to members regardless of
        // what the dialog persisted — exactly the bug the Manager-only
        // path was reporting.
        var fanoutRaw = r.IsDBNull(15) ? null : r.GetString(15);
        var fanout = fanoutRaw?.ToLowerInvariant() switch
        {
            "manager" => TeamFanout.Manager,
            "both"    => TeamFanout.Both,
            _         => TeamFanout.Members
        };
        return new ReportSchedule
        {
            Id = r.GetGuid(0),
            ReportId = r.GetGuid(1),
            OwnerId = r.GetString(2),
            OwnerEmail = r.GetString(3),
            CronExpression = r.GetString(4),
            Subject = r.GetString(5),
            AttachmentFormat = r.GetString(6),
            IncludePreview = r.GetBoolean(7),
            IsActive = r.GetBoolean(8),
            LastRunAt = r.IsDBNull(9) ? null : r.GetDateTime(9),
            ConsecutiveFailures = r.IsDBNull(10) ? 0 : r.GetInt32(10),
            Kind = kind,
            TeamId = r.IsDBNull(12) ? null : r.GetInt32(12),
            TeamConnectionId = r.IsDBNull(13) ? null : r.GetGuid(13),
            DistEmail = r.IsDBNull(14) ? null : r.GetString(14),
            TeamFanout = fanout
        };
    }

    private async Task<SavedReport?> GetSavedReportAsync(Guid reportId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // primary_table MUST be in the SELECT — SqlEmitter throws
        // "Primary Table is required" if QueryRequest.PrimaryTable is
        // null. Without this column the saved report's value is lost
        // even when set correctly via the Builder UI.
        await using var cmd = new SqlCommand(
            "SELECT id, name, owner_id, owner_email, field_ids, filters, aggregations, column_state, connection_id, primary_table, company_id FROM EMPOWER.RPT_saved_reports WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", reportId));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new SavedReport
        {
            Id = r.GetGuid(0),
            Name = r.GetString(1),
            OwnerId = r.GetString(2),
            OwnerEmail = r.GetString(3),
            FieldIds = r.GetString(4),
            Filters = r.IsDBNull(5) ? null : r.GetString(5),
            Aggregations = r.IsDBNull(6) ? null : r.GetString(6),
            ColumnState = r.IsDBNull(7) ? null : r.GetString(7),
            ConnectionId = r.IsDBNull(8) ? null : r.GetGuid(8),
            PrimaryTable = r.IsDBNull(9) ? null : r.GetString(9),
            CompanyId = r.IsDBNull(10) ? Guid.Empty : r.GetGuid(10)
        };
    }

    // Hard cap matches the post-migration column width (NVARCHAR(500)).
    // Acts as a safety net for environments that haven't run the
    // 2026-05-04 widen migration yet — without this, a long error
    // message blows up the UPDATE with "String or binary data would
    // be truncated" (MSSQL refuses to silently truncate by default).
    // Picking the smaller of the post-migration width and any caller-
    // supplied long status keeps writes safe regardless of the row
    // schema actually deployed.
    private const int LastRunStatusMaxLength = 500;

    private async Task UpdateScheduleStatusAsync(Guid scheduleId, string status, int consecutiveFailures, bool isActive)
    {
        // Truncate defensively; the column is NVARCHAR(500) post-
        // migration. If the env still has the old NVARCHAR(50) and a
        // long string lands here, we'd still fail — but that case is
        // caught by the migration that ships in this same change.
        var safeStatus = status?.Length > LastRunStatusMaxLength
            ? status[..LastRunStatusMaxLength]
            : status;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // SYSDATETIME() (local time) per the project's "SQL Server
        // datetimes are local" convention — using SYSUTCDATETIME() here
        // was causing the admin Schedules tab to render last-run times
        // shifted by the local→UTC offset (a 2pm PDT fire showed as
        // 21:00 / 22:00 in the UI). Rest of the table is local too;
        // staying consistent avoids any UTC→local conversion logic.
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_report_schedules
            SET last_run_at = SYSDATETIME(),
                last_run_status = @Status,
                consecutive_failures = @Failures,
                is_active = @IsActive
            WHERE id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Status", (object?)safeStatus ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Failures", consecutiveFailures));
        cmd.Parameters.Add(new SqlParameter("@IsActive", isActive));
        cmd.Parameters.Add(new SqlParameter("@Id", scheduleId));
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ──

    private async Task<string> PrefixSubjectWithCompanyAsync(Guid companyId, string subject)
    {
        if (companyId == Guid.Empty) return subject;
        try
        {
            var company = await _companies.GetByIdAsync(companyId);
            return string.IsNullOrWhiteSpace(company?.Name)
                ? subject
                : $"{company.Name} - {subject}";
        }
        catch (Exception ex)
        {
            // Company resolution shouldn't break the email — log + fall
            // through to the unprefixed subject so the run still delivers.
            _logger.LogWarning(ex,
                "Couldn't resolve company {CompanyId} for subject prefix; sending bare subject",
                companyId);
            return subject;
        }
    }

    private static QueryRequest BuildQueryRequest(SavedReport savedReport)
    {
        // Empty-fields validation is Worker-specific — interactive
        // surfaces show an empty grid and let the user fix it. The
        // Worker fails the run instead so the schedule's last_run_status
        // surfaces the misconfiguration.
        if (string.IsNullOrWhiteSpace(savedReport.FieldIds))
        {
            throw new InvalidOperationException(
                $"SavedReport {savedReport.Id} has no field configuration (FieldIds is empty).");
        }

        // QueryRequestFactory captures every saved-report knob in one
        // place — field_ids, filters (JsonElement-safe), aggregations,
        // primary_table, connection_id, plus all column_state knobs
        // (Distinct, Sort, CustomFilterIds, AdvancedFilters, TableCalcs).
        // Adding a new saved knob is a one-line change in the factory;
        // every consumer (Viewer / Master Dashboard tile / Detail Viewer /
        // this Worker) picks it up automatically.
        var request = QueryRequestFactory.FromSavedReport(savedReport, QueryRequest.MaxPageSize);

        if (request.FieldIds is null || request.FieldIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"SavedReport {savedReport.Id} has an empty field list — cannot generate report.");
        }

        return request;
    }

    // Mirrors what a user sees in the viewer: drops columns hidden in the saved
    // report's ColumnState so scheduled exports don't leak fields the owner has
    // hidden. Rows are left untouched — the export loop iterates Columns, so
    // dropped fields never get emitted.
    private static QueryResponse BuildExportData(QueryResponse source, string? columnStateJson)
    {
        if (string.IsNullOrWhiteSpace(columnStateJson)) return source;
        List<string>? hiddenList = null;
        try
        {
            using var doc = JsonDocument.Parse(columnStateJson);
            if (doc.RootElement.TryGetProperty("HiddenColumns", out var hc)
                && hc.ValueKind == JsonValueKind.Array)
            {
                hiddenList = JsonSerializer.Deserialize<List<string>>(hc.GetRawText());
            }
        }
        catch { return source; }

        if (hiddenList is null || hiddenList.Count == 0) return source;
        var hidden = new HashSet<string>(hiddenList, StringComparer.OrdinalIgnoreCase);
        var visibleColumns = source.Columns.Where(c => !hidden.Contains(c.FieldId)).ToList();
        if (visibleColumns.Count == source.Columns.Count) return source;
        return new QueryResponse
        {
            Columns = visibleColumns,
            Rows = source.Rows,
            TotalCount = source.TotalCount
        };
    }

    private (byte[] Bytes, string FileName) FormatAttachment(
        QueryResponse data, string reportName, string format)
    {
        var safeReportName = SanitizeFileName(reportName);
        // Local time matches the schedule's perceived run time and the
        // last_run_at column. UTC stamps in the filename made
        // recipients see a "21:00:05" suffix on a 2pm PDT delivery.
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = _exportService.ExportToCsv(data);
            return (bytes, $"{safeReportName}_{timestamp}.csv");
        }

        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = _exportService.ExportToPdf(data, reportName);
            return (bytes, $"{safeReportName}_{timestamp}.pdf");
        }

        var excelBytes = _exportService.ExportToExcel(data, reportName);
        return (excelBytes, $"{safeReportName}_{timestamp}.xlsx");
    }

    private static string BuildEmailBody(string reportName, int rowCount, bool includePreview, string? debugSql = null)
    {
        var html = $"""
            <h2>Scheduled Report: {System.Net.WebUtility.HtmlEncode(reportName)}</h2>
            <p>Your scheduled report has been generated successfully.</p>
            <ul>
                <li><strong>Records:</strong> {rowCount:N0}</li>
                <li><strong>Generated:</strong> {DateTime.Now:MMMM dd, yyyy h:mm tt}</li>
            </ul>
            <p>The report is attached to this email.</p>
            """;

        if (!includePreview)
        {
            html += "<p><em>Preview was not included per your schedule settings.</em></p>";
        }

        // Debug — append the executed SQL so recipients can verify the
        // exact query that produced the attachment. Temporary; remove
        // once the team-scope SQL discrepancy is resolved.
        if (!string.IsNullOrWhiteSpace(debugSql))
        {
            html += $"""
                <hr />
                <h3>Debug: Executed SQL</h3>
                <pre style="background:#F5F5F5;border:1px solid #DADCE0;padding:8px;font-size:11px;white-space:pre-wrap;word-break:break-word;">{System.Net.WebUtility.HtmlEncode(debugSql)}</pre>
                """;
        }

        return html;
    }

    // Debug helper — fires from a failed query catch with the executed
    // SQL captured. Sends to the schedule's owner so the admin running
    // the test can see exactly what the pipeline produced. Errors
    // sending the debug email are swallowed (we don't want to mask the
    // original failure with a secondary email problem).
    private async Task TrySendDebugFailureEmailAsync(
        Guid scheduleId,
        ReportSchedule schedule,
        string recipientContext,
        string errorMessage,
        string? failingSql)
    {
        if (string.IsNullOrWhiteSpace(schedule.OwnerEmail)) return;

        try
        {
            var sqlBlock = string.IsNullOrWhiteSpace(failingSql)
                ? "<p><em>No SQL was captured — the failure happened before the query reached the database.</em></p>"
                : $"""
                    <h3>Failing SQL</h3>
                    <pre style="background:#FFF3E0;border:1px solid #FFB300;padding:8px;font-size:11px;white-space:pre-wrap;word-break:break-word;">{System.Net.WebUtility.HtmlEncode(failingSql)}</pre>
                    """;

            var html = $"""
                <h2>Scheduled Run Failed — Debug</h2>
                <ul>
                    <li><strong>Schedule:</strong> {System.Net.WebUtility.HtmlEncode(schedule.Subject ?? scheduleId.ToString())}</li>
                    <li><strong>Recipient context:</strong> {System.Net.WebUtility.HtmlEncode(recipientContext)}</li>
                    <li><strong>Error:</strong> {System.Net.WebUtility.HtmlEncode(errorMessage)}</li>
                    <li><strong>Time:</strong> {DateTime.Now:MMMM dd, yyyy h:mm tt}</li>
                </ul>
                {sqlBlock}
                <p><em>This is a debug message; no attachment was generated because the query failed.</em></p>
                """;

            await _emailService.SendReportEmailAsync(
                schedule.OwnerEmail,
                $"[DEBUG] Schedule failed: {schedule.Subject ?? scheduleId.ToString()}",
                html,
                attachment: null,
                attachmentFileName: string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to send debug failure email for ScheduleId={ScheduleId}", scheduleId);
        }
    }

    private async Task SendFailureNotificationAsync(ReportSchedule schedule, int failures)
    {
        try
        {
            var html = $"""
                <h2>Scheduled Report Deactivated</h2>
                <p>Your scheduled report <strong>{System.Net.WebUtility.HtmlEncode(schedule.Subject)}</strong> has been
                automatically deactivated after {failures} consecutive failures.</p>
                <p><strong>Last error:</strong> {System.Net.WebUtility.HtmlEncode(schedule.LastRunStatus ?? "Unknown")}</p>
                <p>Please review and fix the issue in the TLE Reporting Dashboard, then re-enable the schedule.</p>
                """;

            await _emailService.SendReportEmailAsync(
                schedule.OwnerEmail,
                $"Schedule Deactivated: {schedule.Subject}",
                html,
                attachment: null,
                attachmentFileName: string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send deactivation notification for ScheduleId={ScheduleId}",
                schedule.Id);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Length > 100 ? sanitized[..100] : sanitized;
    }

}
