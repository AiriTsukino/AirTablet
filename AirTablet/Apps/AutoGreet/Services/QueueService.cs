using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class QueueService : IDisposable
{
    private readonly Configuration config;
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private readonly GreetingService greetings;
    private readonly MacroEngine macroEngine;
    private readonly DetectionService detection;
    private readonly EmoteResumeService emoteResume;
    private readonly PendingEmoteQueueService pendingEmotes;
    private readonly DiagnosticLogService logs;
    private CancellationTokenSource cts = new();
    private Task? worker;
    private bool workerForceMode;
    private readonly object sync = new();

    public QueueService(Configuration config, VenueService venues, PersistenceService persistence, GreetingService greetings, MacroEngine macroEngine, DetectionService detection, EmoteResumeService emoteResume, PendingEmoteQueueService pendingEmotes, DiagnosticLogService logs)
    {
        this.config = config;
        this.venues = venues;
        this.persistence = persistence;
        this.greetings = greetings;
        this.macroEngine = macroEngine;
        this.detection = detection;
        this.emoteResume = emoteResume;
        this.pendingEmotes = pendingEmotes;
        this.logs = logs;
    }

    public IReadOnlyList<QueueEntry> Entries => venues.ActiveVenueOrNull?.Queue ?? [];
    public bool IsRunning => worker is { IsCompleted: false };

    public void EnqueueEligibleUngreeted(bool forceStart = false)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var queueCountBefore = venue.Queue.Count;
        foreach (var key in venue.Session.Ungreeted.ToArray())
        {
            if (venue.Blacklist.Contains(key.ToString())) continue;
            if (VenueService.ContainsKey(venue.Session.Greeted, key) || VenueService.ContainsKey(venue.Session.Skipped, key)) continue;
            if (!IsVisitorPresent(venue, key)) continue;
            Enqueue(key, forceStart, deferSave: true);
        }

        if (venue.Queue.Count != queueCountBefore)
            persistence.RequestSave();

        if ((forceStart || config.AutoGreetEnabled) && venue.Queue.Any(q => q.Status == QueueEntryStatus.Waiting))
            EnsureWorker(forceStart);
    }

    public void Enqueue(VisitorKey key, bool forceStart = false, bool? allowDetachedCustomGreeting = null, bool deferSave = false)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (venue.Blacklist.Contains(key.ToString())) return;

        var detachedCustomGreeting = allowDetachedCustomGreeting == true;
        if (!detachedCustomGreeting && !IsVisitorPresent(venue, key))
            return;

        var existingActiveEntry = venue.Queue.FirstOrDefault(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Waiting or QueueEntryStatus.Running) && q.CustomRegionRouteId == Guid.Empty);
        if (existingActiveEntry is not null)
        {
            var existingDetachedCustomGreeting = allowDetachedCustomGreeting ?? detection.IsUsingCustomGreetingAreaFor(key);
            if (existingDetachedCustomGreeting && !existingActiveEntry.AllowDetachedCustomGreeting)
            {
                existingActiveEntry.AllowDetachedCustomGreeting = true;
                existingActiveEntry.StatusText = existingActiveEntry.Status == QueueEntryStatus.Waiting ? "Queued custom venue greeting" : existingActiveEntry.StatusText;
                logs.Info("Custom venue greeting upgraded", $"Existing main active macro queue entry for {key.Display} was marked as a custom-region greeting, so leaving the region before send will not cancel it.");
                SaveQueueChange(deferSave);
            }

            if (forceStart) EnsureWorker(true);
            return;
        }

        // Remove old history rows for this visitor before creating a fresh queue item.
        venue.Queue.RemoveAll(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Completed or QueueEntryStatus.Failed or QueueEntryStatus.Cancelled) && q.CustomRegionRouteId == Guid.Empty);

        if (VenueService.ContainsKey(venue.Session.Greeted, key) || VenueService.ContainsKey(venue.Session.Skipped, key)) return;

        var macro = greetings.PickMacro(key);
        if (macro is null) return;

        var newDetachedCustomGreeting = allowDetachedCustomGreeting ?? detection.IsUsingCustomGreetingAreaFor(key);
        if (newDetachedCustomGreeting)
            logs.Info("Custom venue greeting queued", $"Queued main active macro '{macro.Name}' for {key.Display} from a custom greeting region. It will still send if the visitor leaves the region before the queue reaches them.");

        venue.Queue.Add(new QueueEntry
        {
            Visitor = key,
            GreetingProfileId = venue.ActiveGreetingProfileId,
            Category = macro.Category,
            AllowDetachedCustomGreeting = newDetachedCustomGreeting,
            StatusText = newDetachedCustomGreeting ? "Queued custom venue greeting" : "Queued"
        });
        SaveQueueChange(deferSave);
        EnsureWorker(forceStart);
    }

    private static bool IsVisitorPresent(VenueProfile venue, VisitorKey key)
    {
        return venue.Session.NightlyVisitors.Any(v =>
            v.Present && string.Equals(v.Key.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    public void EnqueueCustomRegionMacro(VisitorKey key, Guid routeId, Guid macroId, bool deferSave = false)
    {
        if (!config.AutoGreetEnabled) return;

        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (venue.Blacklist.Contains(key.ToString())) return;
        if (routeId == Guid.Empty || macroId == Guid.Empty) return;

        if (HasCustomRegionGreeting(venue, routeId, key))
            return;

        if (venue.Queue.Any(q => q.CustomRegionRouteId == routeId && string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Waiting or QueueEntryStatus.Running)))
            return;

        var macro = greetings.PickMacroById(macroId);
        if (macro is null)
        {
            logs.Warning("Custom region macro missing", $"A custom region route for {venue.Name} points at a missing or disabled macro. Open Settings > Venues and select an enabled macro for the route.");
            return;
        }

        logs.Info("Custom region macro queued", $"Queued custom region macro '{macro.Name}' for {key.Display}. Route: {routeId}.");

        venue.Queue.Add(new QueueEntry
        {
            Visitor = key,
            GreetingProfileId = venue.ActiveGreetingProfileId,
            Category = macro.Category,
            MacroOverrideId = macro.Id,
            CustomRegionRouteId = routeId,
            StatusText = "Queued custom region macro",
        });
        SaveQueueChange(deferSave);
        EnsureWorker(false);
    }

    public void Cancel(VisitorKey key, string reason, bool deferSave = false)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;

        foreach (var entry in venue.Queue.Where(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Waiting or QueueEntryStatus.Running)))
        {
            if (entry.CustomRegionRouteId != Guid.Empty && IsNonBlockingCustomRegionCancelReason(reason))
            {
                logs.Info("Custom region macro kept queued", $"Kept custom region macro queued for {key.Display}. Reason ignored for custom-region sends: {reason}. Route: {entry.CustomRegionRouteId}.");
                continue;
            }

            if (entry.CustomRegionRouteId == Guid.Empty && entry.AllowDetachedCustomGreeting && reason.Equals("Visitor left", StringComparison.OrdinalIgnoreCase))
            {
                logs.Info("Custom venue greeting kept queued", $"Kept main active macro queued for {key.Display}. Visitor left the custom region before sending, but the entry was already captured.");
                continue;
            }

            entry.Status = QueueEntryStatus.Cancelled;
            entry.StatusText = reason;
            logs.Info("Queue entry cancelled", $"Queue entry for {key.Display} was cancelled. Reason: {reason}. Route: {(entry.CustomRegionRouteId == Guid.Empty ? "None" : entry.CustomRegionRouteId.ToString())}.");
        }
        SaveQueueChange(deferSave);
    }

    private void SaveQueueChange(bool deferSave)
    {
        if (deferSave)
            persistence.RequestSave();
        else
            persistence.SaveNow();
    }


    private static bool IsNonBlockingCustomRegionCancelReason(string reason)
        => reason.Equals("Visitor left", StringComparison.OrdinalIgnoreCase)
           || reason.Equals("Skipped", StringComparison.OrdinalIgnoreCase)
           || reason.Equals("Manually marked greeted", StringComparison.OrdinalIgnoreCase);

    public void EnsureWorker(bool forceStart = false)
    {
        lock (sync)
        {
            workerForceMode |= forceStart;
            if (worker is { IsCompleted: false }) return;
            cts.Dispose();
            cts = new CancellationTokenSource();
            worker = Task.Run(() => ProcessAsync(cts.Token));
        }
    }

    private bool ShouldProcessQueue()
    {
        lock (sync) return workerForceMode || config.AutoGreetEnabled;
    }

    private void ClearForceModeIfAppropriate()
    {
        lock (sync)
        {
            if (!config.AutoGreetEnabled) workerForceMode = false;
        }
    }

    private void StopQueueAfterMacroSyntaxError()
    {
        lock (sync)
        {
            workerForceMode = false;
            config.AutoGreetEnabled = false;
        }
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && ShouldProcessQueue())
            {
                if (config.AutoGreetEnabled) EnqueueEligibleUngreeted(false);

                var venue = venues.ActiveVenueOrNull;
                if (venue is null)
                {
                    ClearForceModeIfAppropriate();
                    return;
                }

                var entry = venue.Queue.FirstOrDefault(q => q.Status == QueueEntryStatus.Waiting);
                if (entry is null)
                {
                    await TryRunPendingResumeWhenIdleAsync(venue, token).ConfigureAwait(false);

                    if (emoteResume.HasPendingResume)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        continue;
                    }

                    ClearForceModeIfAppropriate();
                    return;
                }

                if (venue.Blacklist.Contains(entry.Visitor.ToString()))
                {
                    entry.Status = QueueEntryStatus.Cancelled;
                    entry.StatusText = "Blacklisted";
                    persistence.SaveNow();
                    continue;
                }

                if (entry.CustomRegionRouteId == Guid.Empty && (VenueService.ContainsKey(venue.Session.Greeted, entry.Visitor) || VenueService.ContainsKey(venue.Session.Skipped, entry.Visitor)))
                {
                    entry.Status = QueueEntryStatus.Cancelled;
                    entry.StatusText = "Visitor was already greeted or skipped";
                    persistence.SaveNow();
                    continue;
                }

                var isCustomRegionMacro = entry.CustomRegionRouteId != Guid.Empty;
                var isDetachedCustomVenueGreeting = !isCustomRegionMacro && entry.AllowDetachedCustomGreeting;
                entry.Status = QueueEntryStatus.Running;
                entry.StatusText = isCustomRegionMacro
                    ? "Starting custom region macro"
                    : isDetachedCustomVenueGreeting
                        ? "Starting custom venue greeting"
                        : "Starting";
                persistence.SaveNow();

                try
                {
                    var startDelaySeconds = isCustomRegionMacro ? 0.1 : isDetachedCustomVenueGreeting ? Math.Min(config.GreetingStartDelaySeconds, 0.5f) : config.GreetingStartDelaySeconds;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, startDelaySeconds)), token).ConfigureAwait(false);

                    if (venue.Blacklist.Contains(entry.Visitor.ToString()))
                        throw new OperationCanceledException("Visitor was blacklisted before greeting started.", token);

                    // If the user manually marked/skipped/cancelled a normal venue greeting during the start delay,
                    // do not continue running the macro on a stale queue entry. Custom-region macros are one-shot
                    // messages and should still send even if the visitor leaves the region before the queue reaches them.
                    if (entry.Status != QueueEntryStatus.Running)
                    {
                        if (!isCustomRegionMacro && !isDetachedCustomVenueGreeting)
                            throw new OperationCanceledException("Queue entry was cancelled before greeting started.", token);

                        logs.Info(isCustomRegionMacro ? "Custom region macro resumed" : "Custom venue greeting resumed",
                            $"Queue entry for {entry.Visitor.Display} was restored after a non-blocking cancel/update before sending. Route: {(entry.CustomRegionRouteId == Guid.Empty ? "Main custom venue" : entry.CustomRegionRouteId.ToString())}.");
                        entry.Status = QueueEntryStatus.Running;
                        entry.StatusText = isCustomRegionMacro ? "Starting custom region macro" : "Starting custom venue greeting";
                        persistence.SaveNow();
                    }

                    if (!isCustomRegionMacro && (VenueService.ContainsKey(venue.Session.Greeted, entry.Visitor) || VenueService.ContainsKey(venue.Session.Skipped, entry.Visitor)))
                        throw new OperationCanceledException("Visitor was already greeted or skipped before greeting started.", token);

                    var macro = entry.MacroOverrideId != Guid.Empty
                        ? greetings.PickMacroById(entry.MacroOverrideId)
                        : greetings.PickMacro(entry.Visitor);

                    if (macro is null)
                        throw new InvalidOperationException("No enabled macro for visitor.");

                    var hasTell = greetings.MacroHasTell(macro);
                    var markVisitorGreeted = !isCustomRegionMacro;

                    if (isCustomRegionMacro)
                    {
                        logs.Info("Custom region macro starting", $"Starting custom region macro '{macro.Name}' for {entry.Visitor.Display}. Route: {entry.CustomRegionRouteId}. Target checks are skipped for custom region macros so /tell <playername> can message players outside targeting range.");
                    }
                    else if (isDetachedCustomVenueGreeting)
                    {
                        logs.Info("Custom venue greeting starting", $"Starting main active macro '{macro.Name}' for {entry.Visitor.Display} from a custom greeting region. Presence checks are skipped after queue capture so short region crossings still send.");
                    }
                    else
                    {
                        logs.Info("Queue macro starting", $"Starting macro '{macro.Name}' for {entry.Visitor.Display}.");
                    }

                    Func<bool> stillPresent = isCustomRegionMacro || isDetachedCustomVenueGreeting
                        ? () => true
                        : () => detection.IsPlayerVisible(entry.Visitor);

                    var macroResult = await macroEngine.ExecuteAsync(entry.Visitor, macro, stillPresent, token, markVisitorGreeted).ConfigureAwait(false);
                    if (macroResult.HasEmote)
                        await QueueConfiguredEmoteResumeAfterMacroAsync(macro.Name, entry.Visitor, macroResult).ConfigureAwait(false);

                    if (!hasTell)
                        greetings.ConfirmNonTellMacroCompleted(entry.Visitor, markVisitorGreeted);

                    // RaptureShellModule sends native chat commands directly. Some Dalamud builds do not echo
                    // plugin-originated outgoing tells back through IChatGui.ChatMessage, so successful command
                    // execution is the confirmation signal here.
                    if (hasTell)
                    {
                        entry.StatusText = "Tell command sent";
                        logs.Info("Tell command sent", $"Macro '{macro.Name}' sent a tell command for {entry.Visitor.Display}.");
                        persistence.SaveNow();
                    }

                    if (entry.CustomRegionRouteId != Guid.Empty)
                    {
                        MarkCustomRegionGreeting(venue, entry.CustomRegionRouteId, entry.Visitor);
                        logs.Info("Custom region macro completed", $"Completed custom region macro '{macro.Name}' for {entry.Visitor.Display}. Route: {entry.CustomRegionRouteId}.");
                    }
                    else if (isDetachedCustomVenueGreeting)
                    {
                        logs.Info("Custom venue greeting completed", $"Completed main active macro '{macro.Name}' for {entry.Visitor.Display} from a custom greeting region.");
                    }
                    else
                    {
                        logs.Info("Queue macro completed", $"Completed macro '{macro.Name}' for {entry.Visitor.Display}.");
                    }

                    entry.Status = QueueEntryStatus.Completed;
                    entry.StatusText = entry.CustomRegionRouteId == Guid.Empty
                        ? isDetachedCustomVenueGreeting
                            ? hasTell ? "Completed custom venue greeting - tell sent" : "Completed custom venue greeting"
                            : hasTell ? "Completed - tell sent" : "Completed"
                        : hasTell ? "Completed custom region macro - tell sent" : "Completed custom region macro";
                }
                catch (MacroSyntaxException ex)
                {
                    entry.Status = QueueEntryStatus.Failed;
                    entry.StatusText = "Macro syntax error - AutoGreet paused. Open the Log tab.";
                    StopQueueAfterMacroSyntaxError();
                    logs.Warning("AutoGreet paused", $"Queue processing stopped because {ex.Message} Fix the macro syntax shown in the Log tab, then turn AutoGreet back on.");
                    DalamudServices.Log.Error(ex, "AutoGreet queue stopped because a macro had unsupported syntax.");
                    return;
                }
                catch (OperationCanceledException ex)
                {
                    entry.Status = QueueEntryStatus.Cancelled;
                    entry.StatusText = "Cancelled";
                    logs.Info("Queue entry cancelled", $"Queue entry for {entry.Visitor.Display} was cancelled while processing. Reason: {ex.Message}. Route: {(entry.CustomRegionRouteId == Guid.Empty ? "None" : entry.CustomRegionRouteId.ToString())}.");
                }
                catch (Exception ex)
                {
                    entry.Status = QueueEntryStatus.Failed;
                    entry.StatusText = ex.Message;
                    logs.Warning("Queue entry failed", $"Queue entry for {entry.Visitor.Display} failed: {ex.Message}");
                    DalamudServices.Log.Error(ex, "AutoGreet queue failed.");
                }
                finally
                {
                    persistence.SaveNow();
                }

                if (!token.IsCancellationRequested && config.QueueDelaySeconds > 0 && venue.Queue.Any(q => q.Status == QueueEntryStatus.Waiting))
                {
                    if (entry.Status == QueueEntryStatus.Completed)
                    {
                        entry.StatusText = $"Completed - waiting {config.QueueDelaySeconds:0.0}s before next visitor";
                        persistence.SaveNow();
                    }

                    await Task.Delay(TimeSpan.FromSeconds(config.QueueDelaySeconds), token).ConfigureAwait(false);
                }
        }
    }

    private async Task QueueConfiguredEmoteResumeAfterMacroAsync(string macroName, VisitorKey visitor, MacroExecutionResult macroResult)
    {
        if (!config.ResumePreviousEmoteEnabled)
            return;

        var reason = macroResult.QueuedTargetedEmote
            ? "the macro contained a targeted emote that is waiting for the visitor to become targetable"
            : "the macro contained an emote";

        logs.Info("Resume emote queued", $"Macro '{macroName}' for {visitor.Display} contained an emote. The configured resume emote will run after the greeting and pending emote queues are clear.");
        await emoteResume.RequestResumeAfterQueueAsync(macroName, visitor, reason, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TryRunPendingResumeWhenIdleAsync(VenueProfile venue, CancellationToken token)
    {
        if (!emoteResume.HasPendingResume)
            return;

        var queueIdle = !venue.Queue.Any(q => q.Status is QueueEntryStatus.Waiting or QueueEntryStatus.Running);
        var emoteQueueIdle = pendingEmotes.PendingCount == 0;

        if (!queueIdle || !emoteQueueIdle)
            return;

        logs.Info("Resume emote running", "Greeting queue and pending emote queue are clear. Running the configured resume emote.");
        await emoteResume.RunPendingResumeIfReadyAsync(queueIdle, emoteQueueIdle, token).ConfigureAwait(false);
    }

    private static bool HasCustomRegionGreeting(VenueProfile venue, Guid routeId, VisitorKey key)
    {
        return venue.Session.CustomRegionGreetings.TryGetValue(routeId, out var greeted)
               && VenueService.ContainsKey(greeted, key);
    }

    private static void MarkCustomRegionGreeting(VenueProfile venue, Guid routeId, VisitorKey key)
    {
        if (!venue.Session.CustomRegionGreetings.TryGetValue(routeId, out var greeted))
        {
            greeted = [];
            venue.Session.CustomRegionGreetings[routeId] = greeted;
        }

        if (!VenueService.ContainsKey(greeted, key))
            greeted.Add(key);
    }

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }
}
