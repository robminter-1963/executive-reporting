using Cronos;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Services;

namespace TleReportingDashboard.Worker.Jobs;

// Reconciles the RPT_report_schedules table with Hangfire's recurring-job
// registry. Polls every PollIntervalSeconds and:
//   * AddOrUpdate("schedule-{id}", j => j.ExecuteAsync(id), cron) for every
//     active schedule row.
//   * RemoveIfExists("schedule-{id}") for any previously-registered job whose
//     row is now inactive or has been deleted.
//
// Why polling instead of having the Web side push directly into Hangfire:
//   * Web doesn't reference Hangfire at all — keeping that boundary clean.
//   * Worker is the only process that owns the BackgroundJobServer; pushing
//     from Web would require coordinating across processes.
//   * Polling tolerates DB hiccups and Worker restarts without coordination —
//     on every poll we re-derive the desired state from the table, so a missed
//     Web write or a Worker restart self-heals on the next tick.
//
// Cost is one cheap query per minute (SELECT id, cron_expression, is_active),
// which is rounding error compared to actually running the jobs.
public sealed class SchedulerSyncService : BackgroundService
{
    private const int DefaultPollIntervalSeconds = 60;
    private const string JobIdPrefix = "schedule-";
    // Same time zone the Web app uses for "today" semantics. Cron expressions
    // stored in RPT_report_schedules are interpreted in this zone.
    private const string CronTimeZoneId = "Pacific Standard Time";
    // Default catch-up window: 60 minutes. Overridable via
    // Scheduler:RestartCatchUpMinutes in appsettings. Set to 0 to disable
    // catch-up entirely (worker restart never re-fires anything). Capped
    // at 24h because catching up on a fire from "yesterday" would surprise
    // users far more than skipping it.
    private const int DefaultRestartCatchUpMinutes = 60;
    private const int MaxRestartCatchUpMinutes = 24 * 60;

    private readonly IConfiguration _configuration;
    private readonly IRecurringJobManager _jobs;
    private readonly ILogger<SchedulerSyncService> _logger;
    private readonly TimeSpan _pollInterval;
    // Worker-restart catch-up window. If a schedule's cron fired within
    // this window AND the schedule's last_run_at hasn't covered that fire,
    // we manually trigger the job on the first sync after worker start.
    // Beyond the window, the missed fire is dropped (deliberate — better
    // than re-emailing recipients hours later). Only consulted on the
    // first sync after process boot, not on subsequent polls. Configurable
    // via Scheduler:RestartCatchUpMinutes.
    private readonly TimeSpan _restartCatchUpWindow;
    // Set true after the first SyncOnceAsync completes. Catch-up logic
    // runs only on the first sync — once the worker is up and Hangfire
    // is ticking normally, catch-up is unnecessary (and risks duplicate
    // fires across normal poll cycles).
    private bool _firstSyncCompleted;

    public SchedulerSyncService(
        IConfiguration configuration,
        IRecurringJobManager jobs,
        ILogger<SchedulerSyncService> logger)
    {
        _configuration = configuration;
        _jobs = jobs;
        _logger = logger;
        var seconds = configuration.GetValue<int?>("Scheduler:PollIntervalSeconds")
                      ?? DefaultPollIntervalSeconds;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(15, seconds));

        // Catch-up window for missed fires after a worker restart. 0
        // disables catch-up entirely; values are clamped to [0, 24h]
        // because firing a "missed" run from yesterday surprises users
        // worse than just skipping it.
        var catchUpMinutes = configuration.GetValue<int?>("Scheduler:RestartCatchUpMinutes")
                             ?? DefaultRestartCatchUpMinutes;
        catchUpMinutes = Math.Clamp(catchUpMinutes, 0, MaxRestartCatchUpMinutes);
        _restartCatchUpWindow = TimeSpan.FromMinutes(catchUpMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SchedulerSyncService started — polling every {Interval}s", _pollInterval.TotalSeconds);
        // Initial sync runs immediately so a freshly-restarted worker
        // catches up without waiting one full poll interval.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SchedulerSyncService poll failed — will retry next interval.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (TaskCanceledException) { /* stopping */ }
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        var connStr = _configuration.GetConnectionString("ConfigDb");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _logger.LogWarning("SchedulerSyncService: ConfigDb connection string missing — skipping poll.");
            return;
        }

        // Pull the desired state from the DB. LEFT JOIN saved_reports +
        // companies so we can format the friendly label as
        // "<Company> - <Schedule Subject>" — Hangfire's [DisplayName]
        // attribute on ScheduledReportJob.ExecuteAsync renders {1} (the
        // displayName argument) in place of the raw method signature, so
        // renaming a schedule's subject in the dialog reflects in the
        // dashboard on the next sync poll. Falls back to the saved
        // report's name when subject is somehow blank, then to
        // "(unnamed schedule)" if both are missing — defensive only;
        // the dialog enforces non-empty subject at save time.
        var desired = new Dictionary<Guid, ScheduleRow>();
        await using (var conn = new SqlConnection(connStr))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
                SELECT s.id,
                       s.cron_expression,
                       COALESCE(NULLIF(LTRIM(RTRIM(s.subject)), ''), r.name, '(unnamed schedule)') AS label,
                       c.name AS company_name,
                       s.last_run_at
                  FROM EMPOWER.RPT_report_schedules s
             LEFT JOIN EMPOWER.RPT_saved_reports r ON r.id = s.report_id
             LEFT JOIN EMPOWER.RPT_companies   c ON c.id = r.company_id
                 WHERE s.is_active = 1;", conn);
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetGuid(0);
                    var cron = r.GetString(1);
                    var label = r.IsDBNull(2) ? "(unnamed schedule)" : r.GetString(2);
                    var companyName = r.IsDBNull(3) ? null : r.GetString(3);
                    var lastRunAt = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4);
                    var displayName = string.IsNullOrWhiteSpace(companyName)
                        ? label
                        : $"{companyName} - {label}";
                    if (!string.IsNullOrWhiteSpace(cron))
                        desired[id] = new ScheduleRow(cron.Trim(), displayName, lastRunAt);
                }
            }
            catch (SqlException ex) when (ex.IsObjectMissing())
            {
                _logger.LogDebug(ex, "RPT_report_schedules missing — skipping poll.");
                return;
            }
        }

        // Read what Hangfire currently has registered. The recurring-job
        // record set lives in the Hangfire schema (HangFire.Hash + Set);
        // GetRecurringJobs hands us the parsed view.
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        using (var hangfireConn = JobStorage.Current.GetConnection())
        {
            foreach (var job in hangfireConn.GetRecurringJobs())
            {
                if (job.Id.StartsWith(JobIdPrefix, StringComparison.Ordinal))
                    currentIds.Add(job.Id);
            }
        }

        // Add or update every active row. Cadence comparison is cheap on
        // Hangfire's side — AddOrUpdate is idempotent when the cron + job
        // method haven't changed.
        var keepIds = new HashSet<string>(StringComparer.Ordinal);
        TimeZoneInfo? tz = null;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(CronTimeZoneId); }
        catch { /* fall back to UTC silently */ }

        foreach (var (id, row) in desired)
        {
            var jobId = JobIdPrefix + id.ToString("N");
            keepIds.Add(jobId);
            try
            {
                // Pass the "<Company> - <Report Name>" label as the
                // displayName arg so the Hangfire dashboard renders
                // "Scheduled run: <Company> - <Report Name>" (driven by
                // [DisplayName("Scheduled run: {1}")] on ExecuteAsync)
                // instead of the raw method signature.
                _jobs.AddOrUpdate<ScheduledReportJob>(
                    jobId,
                    job => job.ExecuteAsync(id, row.DisplayName),
                    row.Cron,
                    new RecurringJobOptions
                    {
                        TimeZone = tz ?? TimeZoneInfo.Utc,
                        // Critical for restart safety. Hangfire's default
                        // (Relaxed) re-fires any cron tick that's between
                        // the job's stored LastExecution and "now" whenever
                        // AddOrUpdate is called — which happens every time
                        // SchedulerSyncService re-registers the schedule on
                        // worker startup. Result: bouncing the worker after
                        // 6pm caused the schedule to fire AGAIN the moment
                        // Hangfire ticked. Ignorable says "skip missed
                        // fires, only schedule the next future cron tick" —
                        // the right tradeoff for scheduled reports, where
                        // a duplicate email is worse than a skipped one
                        // (today's run can wait for tomorrow's window).
                        MisfireHandling = MisfireHandlingMode.Ignorable,
                    });
            }
            catch (Exception ex)
            {
                // Bad cron expression on a single row shouldn't break the
                // whole sync. Log it and move on; admin sees a stale
                // last_run_status when they view the schedule editor.
                _logger.LogWarning(ex,
                    "Failed to register recurring job for ScheduleId={ScheduleId} (cron='{Cron}')",
                    id, row.Cron);
            }
        }

        // Anything Hangfire currently has that we DIDN'T just add belongs
        // to a deleted/inactive schedule — remove it.
        var stale = currentIds.Except(keepIds).ToList();
        foreach (var jobId in stale)
        {
            try { _jobs.RemoveIfExists(jobId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove stale recurring job {JobId}", jobId);
            }
        }

        // Restart catch-up. Runs only on the FIRST sync after the worker
        // booted. With MisfireHandlingMode.Ignorable, Hangfire never
        // auto-refires missed ticks (good — prevents the "restart at
        // 7:08 PM re-fires the 6 PM run" duplicate). But a brief outage
        // (worker bounced 5 min before a fire, back up 5 min after)
        // shouldn't lose the run. This window-bounded catch-up bridges
        // the two: if a cron tick fell inside the catch-up window AND
        // last_run_at doesn't already cover it, we manually Trigger.
        // Subsequent polls skip this — Hangfire is ticking and any
        // future fires happen on schedule.
        if (!_firstSyncCompleted)
        {
            if (_restartCatchUpWindow > TimeSpan.Zero)
                await RunCatchUpPassAsync(desired, tz ?? TimeZoneInfo.Utc, ct);
            else
                _logger.LogInformation(
                    "Restart catch-up disabled (Scheduler:RestartCatchUpMinutes = 0).");
            _firstSyncCompleted = true;
        }

        _logger.LogDebug(
            "Scheduler sync complete: {Active} active, {Removed} removed.",
            keepIds.Count, stale.Count);
    }

    private Task RunCatchUpPassAsync(
        Dictionary<Guid, ScheduleRow> desired, TimeZoneInfo tz, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var windowStart = now - _restartCatchUpWindow;
        var firedCount = 0;

        foreach (var (id, row) in desired)
        {
            if (ct.IsCancellationRequested) break;
            CronExpression cron;
            try { cron = CronExpression.Parse(row.Cron); }
            catch
            {
                // Bad cron — already logged by the AddOrUpdate pass above.
                continue;
            }

            // Most recent occurrence in the catch-up window (inclusive on
            // both ends so a fire-time exactly at windowStart still
            // counts). Cronos returns occurrences in UTC when given UTC
            // bounds; we hand it the cron's timezone so DST is handled.
            DateTime? lastOccurrence = null;
            foreach (var occ in cron.GetOccurrences(windowStart, now, tz,
                                                    fromInclusive: true, toInclusive: true))
            {
                lastOccurrence = occ; // sequence is ascending; keep the latest
            }
            if (lastOccurrence is null) continue;

            // last_run_at is local-time per project convention (SQL Server
            // datetimes are local). Compare against the occurrence
            // converted to local time so we're comparing apples to apples.
            var occLocal = TimeZoneInfo.ConvertTimeFromUtc(lastOccurrence.Value, tz);
            if (row.LastRunAt.HasValue && row.LastRunAt.Value >= occLocal)
            {
                // Schedule already ran at-or-after the most recent
                // expected fire — nothing to catch up.
                continue;
            }

            try
            {
                var jobId = JobIdPrefix + id.ToString("N");
                _jobs.Trigger(jobId);
                firedCount++;
                _logger.LogInformation(
                    "Restart catch-up: triggered ScheduleId={ScheduleId} " +
                    "(missed fire at {Occurrence}, last_run_at={LastRun})",
                    id, occLocal, row.LastRunAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Restart catch-up: failed to trigger ScheduleId={ScheduleId}", id);
            }
        }

        if (firedCount > 0)
            _logger.LogInformation("Restart catch-up complete: {Count} schedule(s) triggered.", firedCount);
        return Task.CompletedTask;
    }

    // Lightweight tuple — what the SELECT pulls per active schedule.
    // Cron drives the recurring-job cadence; DisplayName is forwarded
    // as the [DisplayName] argument so the Hangfire dashboard reads
    // "<Company> - <Report Name>" (or just the report name when no
    // company is linked). LastRunAt is consulted by the first-sync
    // catch-up pass — if it's older than the most recent past cron
    // occurrence inside the catch-up window, we manually trigger.
    private sealed record ScheduleRow(string Cron, string DisplayName, DateTime? LastRunAt);
}
