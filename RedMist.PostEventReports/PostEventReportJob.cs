using Microsoft.EntityFrameworkCore;
using MimeKit;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.PostEventReports.Email;
using RedMist.PostEventReports.Sections;
using RedMist.PostEventReports.Suggestions;
using RedMist.TimingCommon.Models;
using System.Net;
using System.Text.Json;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.PostEventReports;

/// <summary>
/// Sends each organization a report on how their event went, once the event is over.
/// </summary>
/// <remarks>
/// <para>
/// Runs as a Kubernetes CronJob and stops the host when it is done. The report is assembled from
/// whatever <see cref="IReportSection"/> implementations are registered - viewership today, more
/// later - so this class contains nothing about what is actually in a report.
/// </para>
/// <para>
/// An event is reported on exactly once, guaranteed by the unique index on
/// <see cref="PostEventReport.EventId"/> as well as by the anti-join that picks candidates.
/// </para>
/// </remarks>
public class PostEventReportJob(
    ILoggerFactory loggerFactory,
    IDbContextFactory<TsContext> contextFactory,
    EmailHelper emailHelper,
    IHostApplicationLifetime lifetime,
    PostEventReportSettings settings,
    IEnumerable<IReportSection> sections,
    SuggestionEngine suggestionEngine,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private const string FROM_EMAIL = "Red Mist <support@redmist.racing>";
    private const string BCC_EMAIL = "brian@bigmissionmotorsports.com";

    private readonly ILogger logger = loggerFactory.CreateLogger<PostEventReportJob>();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly List<IReportSection> sections = [.. sections.OrderBy(s => s.Order)];

    /// <summary>
    /// Sends a single email. The seam that keeps the SMTP client out of the report logic.
    /// </summary>
    protected virtual Task SendEmailAsync(string subject, string bodyHtml, string to, string from, string? bcc)
        => emailHelper.SendEmailAsync(subject, bodyHtml, to, from, bcc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForStartupAsync(stoppingToken);
        await RunAsync(stoppingToken);
    }

    /// <summary>
    /// Runs one pass. Separated from <see cref="ExecuteAsync"/> so the work can be invoked without
    /// the host-startup wait.
    /// </summary>
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation(
                "Post-event reports starting: settle {SettleHours}h, lookback {LookbackDays}d, {Sections} section(s)",
                settings.SettlePeriod.TotalHours, settings.LookbackWindow.TotalDays, sections.Count);

            List<Event> events;
            await using (var lookup = await CreateDbContextWithRetryAsync(stoppingToken))
            {
                events = await LoadCandidateEventsAsync(lookup, stoppingToken);
            }

            if (events.Count == 0)
            {
                logger.LogInformation("No events are ready for a report");
                return;
            }

            logger.LogInformation("Found {Count} event(s) to report on", events.Count);

            var reported = 0;
            foreach (var evt in events)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    // A context per event. Entity Framework does not roll the change tracker back
                    // when a save fails, so a shared context would carry one event's rejected rows
                    // into every subsequent save - turning a single failure into a failure for every
                    // remaining event, and an admin notification for each of them. It also keeps the
                    // bucket rows, which run to thousands per event, from accumulating across the
                    // whole pass in a 512Mi pod.
                    await using var context = await CreateDbContextWithRetryAsync(stoppingToken);
                    await ProcessEventAsync(context, evt, stoppingToken);
                    reported++;
                }
                catch (DbUpdateException ex) when (IsDuplicateReport(ex))
                {
                    // Another run got there first. Nothing to report and nothing wrong.
                    logger.LogInformation(ex, "Event {EventId} was already reported on by a concurrent run", evt.Id);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // One event's report failing must not cost the rest of the run.
                    logger.LogError(ex, "Failed to produce the post-event report for event {EventId}", evt.Id);
                    await SendFailureNotificationAsync(
                        $"Post-event report failed for event {evt.Id} ('{evt.Name}').\n\nException: {ex}");
                }
            }

            logger.LogInformation("Post-event reports completed: {Reported} event(s) processed", reported);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during post-event report processing");

            // A BackgroundService that throws stops the host, but the process still exits 0, so
            // Kubernetes would record a run that died as Success - no backoffLimit retry and nothing
            // to alert on.
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// The events that have finished and have not been reported on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the social compose job, private and name-hidden events are <b>included</b>. That job
    /// excludes them because it publishes to Facebook; nothing here is published. This report goes to
    /// that event's own organization, and a private event's numbers matter to its organizer more
    /// rather than less - they are the only signal saying whether the access code circulated as
    /// intended.
    /// </para>
    /// <para>
    /// <c>IsLive</c> is a belt-and-braces guard, not the end marker: the orchestrator recomputes it
    /// every ten seconds from relay heartbeats, so it means "a relay is talking right now". With a
    /// settle period of a day it is nearly redundant, but it is what stops a report going out for an
    /// event that was picked back up days later and is mid-session.
    /// </para>
    /// <para>
    /// There is deliberately no <c>IsArchived</c> filter. Archiving moves lap logs to cold storage
    /// and does not touch viewer sessions, and it happens a day after the end date - which can beat
    /// this job to an event. Filtering on it would silently drop those reports.
    /// </para>
    /// </remarks>
    internal async Task<List<Event>> LoadCandidateEventsAsync(TsContext context, CancellationToken stoppingToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var settled = now - settings.SettlePeriod;
        var earliest = now - settings.LookbackWindow;

        return await context.Events
            .AsNoTracking()
            .Where(e => !e.IsDeleted && !e.IsSimulation && !e.IsLive
                        && e.EndDate <= settled && e.EndDate >= earliest)
            .Where(e => !context.PostEventReports.Any(r => r.EventId == e.Id))
            .OrderBy(e => e.EndDate)
            .Take(settings.MaxEventsPerRun)
            .ToListAsync(stoppingToken);
    }

    /// <summary>
    /// Builds and sends one event's report.
    /// </summary>
    /// <remarks>
    /// The report row is written before the first email goes out, and that ordering is deliberate. A
    /// pod killed part way through a send then leaves some recipients with a report and some without,
    /// which is a support question. Writing the row afterwards would instead re-send to everyone who
    /// already had it, and this is a report people forward.
    /// </remarks>
    private async Task ProcessEventAsync(TsContext context, Event evt, CancellationToken stoppingToken)
    {
        var organization = await context.Organizations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == evt.OrganizationId, stoppingToken);
        if (organization == null)
        {
            logger.LogWarning("Organization {OrganizationId} not found for event {EventId}; skipping",
                evt.OrganizationId, evt.Id);
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var racingSessions = await LoadRacingSessionsAsync(context, evt, now, stoppingToken);
        var reportContext = new ReportContext(context, evt, organization, racingSessions, now);

        var report = new PostEventReport
        {
            EventId = evt.Id,
            OrganizationId = evt.OrganizationId,
            GeneratedUtc = now,
            State = PostEventReportState.Pending,
        };
        reportContext.Report = report;

        var built = new List<ReportSectionResult>();
        foreach (var section in sections)
        {
            try
            {
                if (await section.BuildAsync(reportContext, stoppingToken) is { } result)
                {
                    built.Add(result);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A section that throws costs its own block and nothing else. The rest of the report
                // is still worth sending, and the admin is told which section is broken.
                logger.LogError(ex, "Section {Code} failed for event {EventId}", section.Code, evt.Id);
                await SendFailureNotificationAsync(
                    $"Post-event report section '{section.Code}' failed for event {evt.Id} ('{evt.Name}').\n\nException: {ex}");
            }
        }

        report.SectionsJson = JsonSerializer.Serialize(built.Select(s => s.Code).ToList());

        if (built.Count == 0)
        {
            // Nothing to say. The row is still written so the event is never revisited, and so a
            // later reader can tell the difference between "no report" and "not reached yet".
            report.State = PostEventReportState.NoContent;
            context.PostEventReports.Add(report);
            await context.SaveChangesAsync(stoppingToken);
            logger.LogWarning("Event {EventId} ('{Name}') produced no report content; nothing sent", evt.Id, evt.Name);
            return;
        }

        var suggestions = await suggestionEngine.EvaluateAsync(reportContext, stoppingToken);
        report.SuggestionsJson = JsonSerializer.Serialize(suggestions);

        var optedOut = await context.OrganizationReportSettings.AsNoTracking()
            .AnyAsync(s => s.OrganizationId == evt.OrganizationId && !s.SendPostEventReport, stoppingToken);
        if (optedOut)
        {
            report.State = PostEventReportState.Suppressed;
            context.PostEventReports.Add(report);
            await context.SaveChangesAsync(stoppingToken);
            logger.LogInformation("Organization {OrganizationId} has opted out; report for event {EventId} suppressed",
                evt.OrganizationId, evt.Id);
            return;
        }

        var recipients = await ResolveRecipientsAsync(context, evt, stoppingToken);

        if (settings.DryRunRecipient is { Length: > 0 })
        {
            // A rehearsal must change nothing. Writing the row would mark the event as reported, and
            // the candidate query excludes any event that has a row at all - so the dry run intended
            // to check the formatting would quietly ensure no organizer ever received the real thing.
            await SendDryRunAsync(evt, organization, built, suggestions, recipients, stoppingToken);
            return;
        }

        context.PostEventReports.Add(report);
        await context.SaveChangesAsync(stoppingToken);

        if (recipients.Count == 0)
        {
            // The row is kept regardless: the numbers are computed and are worth having even though
            // nobody is listed to receive them.
            report.State = PostEventReportState.NoRecipients;
            await context.SaveChangesAsync(stoppingToken);
            logger.LogWarning("No usable recipients for organization {OrganizationId}; report for event {EventId} not sent",
                evt.OrganizationId, evt.Id);
            return;
        }

        var html = PostEventReportRenderer.Render(evt, organization, built, suggestions);
        var subject = $"Red Mist Post-Event Report - {evt.Name}";

        var sent = 0;
        var failed = 0;
        for (var i = 0; i < recipients.Count; i++)
        {
            var recipient = recipients[i];
            try
            {
                // The admin copy rides on the first send only. The sponsor report never hits this
                // because it has one recipient; a six-person organization would otherwise put six
                // identical copies in the admin inbox every night of a race weekend.
                await SendEmailAsync(subject, html, recipient, FROM_EMAIL, i == 0 ? BCC_EMAIL : null);
                sent++;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                failed++;
                logger.LogError(ex, "Failed to send the post-event report for event {EventId} to {Recipient}",
                    evt.Id, recipient);
            }
        }

        report.RecipientCount = sent;
        report.SendFailureCount = failed;
        report.State = failed == 0 ? PostEventReportState.Sent
            : sent == 0 ? PostEventReportState.Failed
            : PostEventReportState.PartiallySent;
        await context.SaveChangesAsync(stoppingToken);

        logger.LogInformation("Post-event report for event {EventId} ('{Name}'): {Sent} sent, {Failed} failed",
            evt.Id, evt.Name, sent, failed);
    }

    /// <summary>
    /// Sends the report to the dry-run address instead of the organization, and records nothing.
    /// </summary>
    private async Task SendDryRunAsync(Event evt, Organization organization, List<ReportSectionResult> built,
        List<ReportSuggestion> suggestions, List<string> recipients, CancellationToken stoppingToken)
    {
        var html = PostEventReportRenderer.Render(evt, organization, built, suggestions);
        var intended = recipients.Count == 0 ? "nobody" : string.Join(", ", recipients);

        try
        {
            await SendEmailAsync($"[DRY RUN -> {intended}] Red Mist Post-Event Report - {evt.Name}",
                html, settings.DryRunRecipient!, FROM_EMAIL, bcc: null);
            logger.LogInformation("Dry run: event {EventId} ('{Name}') would have gone to {Intended}; nothing recorded",
                evt.Id, evt.Name, intended);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Dry run send failed for event {EventId}", evt.Id);
        }
    }

    /// <summary>
    /// Whether a save failed because this event already has a report.
    /// </summary>
    /// <remarks>
    /// The unique index on EventId is what makes "reported exactly once" true when two runs overlap -
    /// which the candidate query's anti-join cannot prevent on its own, and which a manually created
    /// Job alongside the scheduled one can produce despite concurrencyPolicy: Forbid.
    /// </remarks>
    private static bool IsDuplicateReport(DbUpdateException ex)
        => ex.InnerException?.GetType().Name == "PostgresException"
           && (ex.InnerException.Data["SqlState"] as string) == "23505";

    private async Task<List<SessionWindow>> LoadRacingSessionsAsync(TsContext context, Event evt, DateTime now,
        CancellationToken stoppingToken)
    {
        var sessions = await context.Sessions.AsNoTracking()
            .Where(s => s.EventId == evt.Id)
            .ToListAsync(stoppingToken);

        // The fallback end for a session that never recorded one: the last viewer activity seen for
        // the event, or failing that the event's own end date.
        var lastActivity = await context.EventViewerSessions.AsNoTracking()
            .Where(s => s.EventId == evt.Id)
            .Select(s => (DateTime?)(s.EndUtc ?? s.StartUtc))
            .MaxAsync(stoppingToken);

        var fallback = lastActivity ?? DateTime.SpecifyKind(evt.EndDate, DateTimeKind.Utc);
        if (fallback > now)
        {
            fallback = now;
        }

        return SessionWindowResolver.Resolve(sessions, fallback);
    }

    /// <summary>
    /// The email addresses an organization's report goes to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapping's username is the Keycloak <c>preferred_username</c>, which in this system is an
    /// email address - but nothing enforces that, and <see cref="EmailHelper"/> parses the recipient
    /// with a method that throws. One badly provisioned volunteer account would otherwise take down
    /// the whole event's report rather than its own copy.
    /// </para>
    /// <para>
    /// Compared case-insensitively for the same reason
    /// <see cref="OrganizationMembership.IsMemberAsync"/> is: a Keycloak username that differs in case
    /// from its mapping row is the same person.
    /// </para>
    /// </remarks>
    internal async Task<List<string>> ResolveRecipientsAsync(TsContext context, Event evt, CancellationToken stoppingToken)
    {
        var usernames = await context.UserOrganizationMappings.AsNoTracking()
            .Where(u => u.OrganizationId == evt.OrganizationId)
            .Select(u => u.Username)
            .ToListAsync(stoppingToken);

        var usable = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var username in usernames)
        {
            if (ParseAddress(username) is not { } address)
            {
                rejected.Add(string.IsNullOrWhiteSpace(username) ? "(blank)" : username);
                continue;
            }

            // The parsed address, not the raw mapping text: a mapping stored in display-name form
            // parses to the same address as the bare one, and deduping on the raw strings would send
            // that person two copies.
            if (seen.Add(address))
            {
                usable.Add(address);
            }
        }

        if (rejected.Count > 0)
        {
            logger.LogWarning("Organization {OrganizationId} has {Count} mapping(s) that are not email addresses: {Rejected}",
                evt.OrganizationId, rejected.Count, string.Join(", ", rejected));
        }

        if (usable.Count > settings.MaxRecipientsPerEvent)
        {
            logger.LogWarning("Organization {OrganizationId} has {Count} recipients, capping at {Cap}",
                evt.OrganizationId, usable.Count, settings.MaxRecipientsPerEvent);
            usable = [.. usable.Take(settings.MaxRecipientsPerEvent)];
        }

        return usable;
    }

    /// <summary>
    /// A mapping's username as a usable email address, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// MailKit's parser is lenient and accepts a bare local part with no domain at all, which is
    /// exactly the shape a non-email username takes, so the domain is checked separately. Requiring a
    /// dot in it excludes nothing a person actually receives mail at, and excludes the machine
    /// account names that would otherwise be handed to the mail server as recipients.
    /// </remarks>
    internal static string? ParseAddress(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || !MailboxAddress.TryParse(username, out var parsed))
        {
            return null;
        }

        var address = parsed.Address;
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return null;
        }

        var domain = address[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') ? address : null;
    }

    private async Task SendFailureNotificationAsync(string message)
    {
        try
        {
            var body = $"<html><body><pre>{WebUtility.HtmlEncode(message)}</pre></body></html>";
            await SendEmailAsync("Red Mist Post-Event Report Failure", body, BCC_EMAIL, FROM_EMAIL, bcc: null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send the post-event report failure notification");
        }
    }

    /// <summary>Waits for the host application to signal that it has fully started.</summary>
    private async Task WaitForStartupAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource();
        await using var reg = stoppingToken.Register(() => tcs.TrySetCanceled(stoppingToken));
        lifetime.ApplicationStarted.Register(() => tcs.TrySetResult());
        await tcs.Task;
        // Additional delay for K8s DNS/networking to fully stabilize
        await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken);
        logger.LogInformation("Host started, beginning post-event report job");
    }

    /// <summary>
    /// Creates a DbContext with retry logic for transient connection failures, on an independent
    /// timeout so host shutdown does not cancel it mid-connect.
    /// </summary>
    /// <remarks>
    /// The result of CanConnectAsync is checked rather than discarded: it returns false for an
    /// unreachable database instead of throwing, so ignoring it would hand back a context on the
    /// first attempt and leave the first real query to fail outside this loop, making the retries
    /// dead code precisely when they are needed.
    /// </remarks>
    private async Task<TsContext> CreateDbContextWithRetryAsync(CancellationToken stoppingToken)
    {
        const int maxRetries = 5;
        for (var attempt = 1; ; attempt++)
        {
            stoppingToken.ThrowIfCancellationRequested();
            TsContext? context = null;
            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                context = await contextFactory.CreateDbContextAsync(connectTimeout.Token);
                if (!await context.Database.CanConnectAsync(connectTimeout.Token))
                {
                    throw new InvalidOperationException("The database is not reachable.");
                }

                return context;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                if (context is not null)
                {
                    await context.DisposeAsync();
                }

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(ex, "Database connection attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}s",
                    attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, clock, stoppingToken);
            }
        }
    }
}
