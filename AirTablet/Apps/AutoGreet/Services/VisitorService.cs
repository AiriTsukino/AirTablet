using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class VisitorService
{
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private readonly QueueService queue;
    private readonly DetectionService detection;
    private readonly Configuration config;
    private readonly SoundService sound;
    private readonly DiagnosticLogService logs;

    public VisitorService(VenueService venues, PersistenceService persistence, QueueService queue, DetectionService detection, Configuration config, SoundService sound, DiagnosticLogService logs)
    {
        this.venues = venues;
        this.persistence = persistence;
        this.queue = queue;
        this.detection = detection;
        this.config = config;
        this.sound = sound;
        this.logs = logs;
    }


    public void OnPlayerPresentOnArrival(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (venue.Blacklist.Contains(key.ToString()))
        {
            EnsureBlacklistedSessionPresence(venue, key, hereWhenArrived: true, countVisit: false);
            if (config.ChatNotificationsEnabled && config.ChatNotificationsForBlacklistedEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} was already in {venue.Name} when you arrived.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var session = venue.Session;
        var wasKnown = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor);
        visitor ??= Visitor.FromKey(key);
        var previousLastSeenUtc = visitor.LastSeenUtc;
        visitor.LastSeenUtc = now;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        if (existing is null)
        {
            existing = new SessionVisitorState
            {
                Key = key,
                EnteredUtc = now,
                LastSeenUtc = now,
                LastSeenBeforeCurrentEntryUtc = previousLastSeenUtc,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
                HereWhenArrived = true,
            };
            session.NightlyVisitors.Add(existing);
        }
        else
        {
            if (!existing.Present)
                existing.LastSeenBeforeCurrentEntryUtc = previousLastSeenUtc;
            existing.Present = true;
            existing.LastSeenUtc = now;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);
            if (!VenueService.ContainsKey(session.Ungreeted, key) && !VenueService.ContainsKey(session.Skipped, key))
                existing.HereWhenArrived = true;
        }

        // People already present when the greeter arrives should not be auto-queued.
        // Put them in the greeted list with a visible tag so shift handoffs do not
        // accidentally greet an already-active crowd.
        if (!VenueService.ContainsKey(session.Ungreeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Greeted, key))
            session.Greeted.Add(key);

        if (config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} was already in {venue.Name} when you arrived.");

        RepairAndRequestSave();
    }

    public void OnPlayerDoorbellEntered(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null)
        {
            NotifyPausedMonitorEntry(key);
            return;
        }
        if (venue.Blacklist.Contains(key.ToString()))
        {
            EnsureBlacklistedSessionPresence(venue, key, hereWhenArrived: false, countVisit: true);
            if (config.DoorbellSoundEnabled)
                sound.PlayDoorbell();

            if (config.ChatNotificationsEnabled && config.ChatNotificationsForBlacklistedEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        var wasKnown = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor);
        visitor ??= Visitor.FromKey(key);
        var previousLastSeenUtc = visitor.LastSeenUtc;
        var wasAway = existing is null || !existing.Present;
        visitor.LastSeenUtc = now;
        if (wasAway)
            visitor.TotalVisitCount++;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        SessionVisitorState state;
        if (existing is null)
        {
            state = new SessionVisitorState
            {
                Key = key,
                EnteredUtc = now,
                LastSeenUtc = now,
                LastSeenBeforeCurrentEntryUtc = previousLastSeenUtc,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
            };
            session.NightlyVisitors.Add(state);
        }
        else
        {
            if (!existing.Present)
                existing.LastSeenBeforeCurrentEntryUtc = previousLastSeenUtc;
            existing.Present = true;
            existing.LastSeenUtc = now;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);
            state = existing;
        }

        if (ShouldQueueGreetingTimer(session, key, visitor, previousLastSeenUtc, wasAway, now))
            QueueGreetingTimer(session, key, state, now, customVenueGreeting: false);

        if (config.DoorbellSoundEnabled)
            sound.PlayDoorbell();

        if (config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");

        RepairAndRequestSave();
    }

    private void NotifyPausedMonitorEntry(VisitorKey key)
    {
        if (!config.ActiveVenueDisabled || !config.MonitorWhenNoVenueSelected) return;

        if (config.DoorbellSoundEnabled)
            sound.PlayDoorbell();

        if (config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered the monitored area.");
    }

    private void NotifyPausedMonitorLeave(VisitorKey key)
    {
        if (!config.ActiveVenueDisabled || !config.MonitorWhenNoVenueSelected) return;

        if (config.ChatNotificationsEnabled && config.LeaveChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} left the monitored area.");
    }

    public void OnPlayerEntered(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null)
        {
            NotifyPausedMonitorEntry(key);
            return;
        }
        if (venue.Blacklist.Contains(key.ToString()))
        {
            EnsureBlacklistedSessionPresence(venue, key, hereWhenArrived: false, countVisit: true);
            if (config.DoorbellSoundEnabled)
                sound.PlayDoorbell();

            if (config.ChatNotificationsEnabled && config.ChatNotificationsForBlacklistedEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        var alreadyPresentFromDoorbell = existing?.Present == true;
        var customVenueGreeting = detection.IsUsingCustomGreetingAreaFor(key);
        if (customVenueGreeting)
            logs.Info("Custom venue greeting detected", $"{key.Display} entered the configured custom greeting region for {venue.Name}. Main active macro eligibility will be queued if this visitor has not already been greeted or skipped this session.");

        var wasKnown = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor);
        visitor ??= Visitor.FromKey(key);
        var previousLastSeenUtc = visitor.LastSeenUtc;
        var wasAway = existing is null || !existing.Present;
        visitor.LastSeenUtc = now;
        if (wasAway)
            visitor.TotalVisitCount++;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        // If this visitor already triggered the configured doorbell area, do not print/play
        // a second entry notification when they later enter a greeting-only region.
        if (!alreadyPresentFromDoorbell)
        {
            if (config.DoorbellSoundEnabled)
                sound.PlayDoorbell();

            if (config.ChatNotificationsEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");
        }

        if (existing is null)
        {
            session.NightlyVisitors.Add(new SessionVisitorState
            {
                Key = key,
                EnteredUtc = now,
                LastSeenUtc = now,
                LastSeenBeforeCurrentEntryUtc = previousLastSeenUtc,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
            });

            if (!VenueService.ContainsKey(session.Greeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Ungreeted, key))
                session.Ungreeted.Add(key);

            if (config.AutoGreetEnabled && !VenueService.ContainsKey(session.Greeted, key) && !VenueService.ContainsKey(session.Skipped, key))
                queue.Enqueue(key, allowDetachedCustomGreeting: customVenueGreeting, deferSave: true);
        }
        else
        {
            if (!existing.Present)
                existing.LastSeenBeforeCurrentEntryUtc = previousLastSeenUtc;
            existing.Present = true;
            existing.LastSeenUtc = now;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);

            if (existing.HereWhenArrived && !VenueService.ContainsKey(session.Skipped, key))
            {
                existing.HereWhenArrived = false;
                VenueService.RemoveKey(session.Greeted, key);
                if (!VenueService.ContainsKey(session.Ungreeted, key))
                    session.Ungreeted.Add(key);

                if (config.AutoGreetEnabled)
                    queue.Enqueue(key, allowDetachedCustomGreeting: customVenueGreeting, deferSave: true);
            }
            else if (!VenueService.ContainsKey(session.Greeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Ungreeted, key))
            {
                session.Ungreeted.Add(key);
                if (config.AutoGreetEnabled)
                    queue.Enqueue(key, allowDetachedCustomGreeting: customVenueGreeting, deferSave: true);
            }
            else if (VenueService.ContainsKey(session.Ungreeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Greeted, key))
            {
                if (config.AutoGreetEnabled)
                    queue.Enqueue(key, allowDetachedCustomGreeting: customVenueGreeting, deferSave: true);
            }
            else if (ShouldQueueGreetingTimer(session, key, visitor, previousLastSeenUtc, wasAway, now))
            {
                QueueGreetingTimer(session, key, existing, now, customVenueGreeting);
            }
            else if (VenueService.ContainsKey(session.Greeted, key))
            {
                LogGreetingTimerStillWaiting(session, key, visitor, previousLastSeenUtc, wasAway, now);
                if (customVenueGreeting)
                    logs.Info("Custom venue greeting skipped", $"{key.Display} entered the custom greeting region, but they are already in the greeted list for this session.");
                VenueService.RemoveKey(session.Greeted, key);
                session.Greeted.Insert(0, key);
            }
        }

        RepairAndRequestSave();
    }


    public void OnPlayerCustomRegionMacroEntered(VisitorKey key, Guid routeId)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (venue.Blacklist.Contains(key.ToString())) return;

        var route = venue.CustomRegionMacroRoutes.FirstOrDefault(r => r.Id == routeId && r.Enabled);
        if (route is null || route.MacroId == Guid.Empty) return;

        var now = DateTimeOffset.UtcNow;
        var session = venue.Session;
        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            visitor = Visitor.FromKey(key);
        var previousLastSeenUtc = visitor.LastSeenUtc;
        visitor.LastSeenUtc = now;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        var state = EnsureSessionVisitor(key);
        if (!state.Present)
            state.LastSeenBeforeCurrentEntryUtc = previousLastSeenUtc;
        state.Present = true;
        state.LastSeenUtc = now;
        state.ReturningThisSession = HasBeenGreetedBefore(visitor);

        if (config.AutoGreetEnabled && (!session.CustomRegionGreetings.TryGetValue(routeId, out var greetedForRoute) || !VenueService.ContainsKey(greetedForRoute, key)))
            queue.EnqueueCustomRegionMacro(key, routeId, route.MacroId, deferSave: true);

        RepairAndRequestSave();
    }

    public void OnPlayerLeft(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null)
        {
            NotifyPausedMonitorLeave(key);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var existing = venue.Session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        if (existing is not null)
        {
            existing.Present = false;
            existing.LastSeenUtc = now;
        }

        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            visitor = Visitor.FromKey(key);
        visitor.LastSeenUtc = now;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        if (config.ChatNotificationsEnabled && config.LeaveChatNotificationsEnabled)
        {
            var prefix = venue.Blacklist.Contains(key.ToString()) ? "" : string.Empty;
            DalamudServices.ChatGui.Print($"[AutoGreet] {prefix}{key.Display} left {venue.Name}.");
        }

        queue.Cancel(key, "Visitor left", deferSave: true);
        RepairAndRequestSave();
    }

    public void MarkGreeted(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var session = venue.Session;
        VenueService.RemoveKey(session.Ungreeted, key);
        VenueService.RemoveKey(session.Skipped, key);
        VenueService.RemoveKey(session.Greeted, key);
        session.Greeted.Insert(0, key);
        queue.Cancel(key, "Manually marked greeted");
        var visitor = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var existingVisitor) ? existingVisitor : Visitor.FromKey(key);
        var now = DateTimeOffset.UtcNow;
        visitor.LastSeenUtc = now;
        visitor.LastGreetedUtc = now;
        visitor.HasBeenGreeted = true;
        venue.LifetimeVisitors[key.ToString()] = visitor;
        var existing = EnsureSessionVisitor(key);
        existing.HereWhenArrived = false;
        existing.ReturningThisSession = false;
        venues.RepairActiveVenueData();
    }

    private void RepairAndRequestSave()
    {
        venues.RepairActiveVenueData(save: false);
        persistence.RequestSave();
    }

    public void Skip(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var session = venue.Session;
        VenueService.RemoveKey(session.Ungreeted, key);
        VenueService.RemoveKey(session.Skipped, key);
        session.Skipped.Add(key);
        queue.Cancel(key, "Skipped");
        EnsureSessionVisitor(key);
        venues.RepairActiveVenueData();
    }

    public void MoveToUngreeted(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var session = venue.Session;
        VenueService.RemoveKey(session.Greeted, key);
        VenueService.RemoveKey(session.Skipped, key);
        if (!VenueService.ContainsKey(session.Ungreeted, key)) session.Ungreeted.Add(key);
        var existing = EnsureSessionVisitor(key);
        existing.HereWhenArrived = false;

        if (config.AutoGreetEnabled && !venue.Blacklist.Contains(key.ToString()))
        {
            if (existing.Present)
            {
                var customVenueGreeting = detection.IsUsingCustomGreetingAreaFor(key);
                logs.Info("Manual ungreeted queued", $"{key.Display} was manually moved to Ungreeted while auto-greet is enabled and is currently present, so they were added to the greeting queue.");
                queue.Enqueue(key, forceStart: true, allowDetachedCustomGreeting: customVenueGreeting);
            }
            else
            {
                logs.Info("Manual ungreeted waiting", $"{key.Display} was manually moved to Ungreeted, but they are not currently present in the venue. They will queue when they re-enter.");
            }
        }

        venues.RepairActiveVenueData();
    }

    public void ToggleBlacklist(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (!venue.Blacklist.Remove(key.ToString())) venue.Blacklist.Add(key.ToString());
        queue.Cancel(key, "Blacklisted");
        VenueService.RemoveKey(venue.Session.Ungreeted, key);
        VenueService.RemoveKey(venue.Session.Greeted, key);
        VenueService.RemoveKey(venue.Session.Skipped, key);
        venues.RepairActiveVenueData();
    }


    public void SetVip(VisitorKey key, bool isVip)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        SetVipTier(key, isVip ? venue.GetDefaultVipTier().Id : Guid.Empty);
    }

    public void SetVipTier(VisitorKey key, Guid tierId)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            visitor = Visitor.FromKey(key);

        var tier = tierId == Guid.Empty ? null : venue.GetVipTier(tierId);
        visitor.Vip = tier is not null;
        visitor.VipTierId = tier?.Id ?? Guid.Empty;
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        venue.LifetimeVisitors[key.ToString()] = visitor;
        venues.RepairActiveVenueData();
    }

    public void ToggleVip(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var currentlyVip = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor) && (visitor.Vip || visitor.VipTierId != Guid.Empty);
        SetVip(key, !currentlyVip);
    }

    public void SetBlacklist(VisitorKey key, bool blacklisted)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (blacklisted)
        {
            venue.Blacklist.Add(key.ToString());
            queue.Cancel(key, "Blacklisted");
            VenueService.RemoveKey(venue.Session.Ungreeted, key);
            VenueService.RemoveKey(venue.Session.Greeted, key);
            VenueService.RemoveKey(venue.Session.Skipped, key);
        }
        else
        {
            venue.Blacklist.Remove(key.ToString());
        }

        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            venue.LifetimeVisitors[key.ToString()] = Visitor.FromKey(key);

        venues.RepairActiveVenueData();
    }

    public void SaveNightlySnapshot()
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var s = venue.Session;
        s.Snapshots.Add(new NightlySnapshot
        {
            TotalVisitors = s.NightlyVisitors.Count,
            GreetedCount = s.Greeted.Count,
            UngreetedCount = s.Ungreeted.Count
        });
        persistence.SaveNow();
    }

    public void ResetSession()
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;

        venue.Session.Reset();
        venue.Queue.Clear();

        var restored = RestoreCurrentVisitorListAfterReset(venue);
        venues.RepairActiveVenueData();

        if (config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] Session reset. Current visitor list restored with {restored} visitor{(restored == 1 ? string.Empty : "s")}.");
    }

    private int RestoreCurrentVisitorListAfterReset(VenueProfile venue)
    {
        var restored = 0;
        foreach (var key in detection.GetCurrentVisibleVisitors())
        {
            AddCurrentVisitorAfterSessionReset(venue, key);
            restored++;
        }

        return restored;
    }

    private void AddCurrentVisitorAfterSessionReset(VenueProfile venue, VisitorKey key)
    {
        if (venue.Blacklist.Contains(key.ToString()))
        {
            EnsureBlacklistedSessionPresence(venue, key, hereWhenArrived: true, countVisit: false);
            return;
        }

        var session = venue.Session;
        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            visitor = Visitor.FromKey(key);

        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        if (existing is null)
        {
            session.NightlyVisitors.Add(new SessionVisitorState
            {
                Key = key,
                EnteredUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
                HereWhenArrived = true,
            });
        }
        else
        {
            existing.Present = true;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);
            existing.HereWhenArrived = true;
        }

        if (!VenueService.ContainsKey(session.Ungreeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Greeted, key))
            session.Greeted.Add(key);
    }

    public int ImportCurrentVisitorsForGreeting(IEnumerable<VisitorKey> keys)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return 0;

        var imported = 0;
        foreach (var key in keys)
        {
            if (venue.Blacklist.Contains(key.ToString())) continue;

            if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
                visitor = Visitor.FromKey(key);
            visitor.LastSeenUtc = DateTimeOffset.UtcNow;
            venue.LifetimeVisitors[key.ToString()] = visitor;

            var session = venue.Session;
            var state = EnsureSessionVisitor(key);
            state.Present = true;
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.HereWhenArrived = false;
            state.ReturningThisSession = HasBeenGreetedBefore(visitor);

            // Manual scan means the greeter intentionally wants to greet currently present people.
            // If they were only in Greeted because they were here on arrival, move them to Ungreeted.
            VenueService.RemoveKey(session.Greeted, key);
            if (VenueService.ContainsKey(session.Skipped, key) || VenueService.ContainsKey(session.Ungreeted, key))
                continue;

            session.Ungreeted.Add(key);
            imported++;
            if (config.AutoGreetEnabled)
                queue.Enqueue(key);
        }

        venues.RepairActiveVenueData();
        if (imported > 0 && config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] Manual scan added {imported} visitor{(imported == 1 ? string.Empty : "s")} to the ungreeted list.");
        return imported;
    }


    private bool ShouldQueueGreetingTimer(SessionData session, VisitorKey key, Visitor visitor, DateTimeOffset previousLastSeenUtc, bool wasAway, DateTimeOffset now)
    {
        if (!config.GreetingTimerEnabled || !wasAway)
            return false;

        if (!VenueService.ContainsKey(session.Greeted, key) || VenueService.ContainsKey(session.Skipped, key) || VenueService.ContainsKey(session.Ungreeted, key))
            return false;

        var basis = GetGreetingTimerBasis(visitor, previousLastSeenUtc);
        return now - basis >= TimeSpan.FromMinutes(GetGreetingTimerMinutes());
    }

    private void LogGreetingTimerStillWaiting(SessionData session, VisitorKey key, Visitor visitor, DateTimeOffset previousLastSeenUtc, bool wasAway, DateTimeOffset now)
    {
        if (!config.GreetingTimerEnabled || !wasAway)
            return;

        if (!VenueService.ContainsKey(session.Greeted, key) || VenueService.ContainsKey(session.Skipped, key) || VenueService.ContainsKey(session.Ungreeted, key))
            return;

        var basis = GetGreetingTimerBasis(visitor, previousLastSeenUtc);
        var elapsed = now - basis;
        var required = TimeSpan.FromMinutes(GetGreetingTimerMinutes());
        if (elapsed >= required)
            return;

        var remaining = Math.Max(1, (int)Math.Ceiling((required - elapsed).TotalMinutes));
        logs.Info("Greeting timer not ready", $"{key.Display} re-entered, but their returning greeting timer has {remaining} minute{(remaining == 1 ? string.Empty : "s")} remaining since their last main greeting.");
    }

    private static DateTimeOffset GetGreetingTimerBasis(Visitor visitor, DateTimeOffset previousLastSeenUtc)
    {
        if (visitor.LastGreetedUtc > DateTimeOffset.MinValue)
            return visitor.LastGreetedUtc;

        return previousLastSeenUtc;
    }

    private void QueueGreetingTimer(SessionData session, VisitorKey key, SessionVisitorState state, DateTimeOffset now, bool customVenueGreeting)
    {
        VenueService.RemoveKey(session.Greeted, key);
        VenueService.RemoveKey(session.Ungreeted, key);
        session.Ungreeted.Add(key);
        state.HereWhenArrived = false;
        state.LastSeenBeforeCurrentEntryUtc = now;

        logs.Info("Greeting timer queued", $"{key.Display} was queued for a returning greeting because their greeting timer reached {GetGreetingTimerMinutes()} minutes since their last main greeting.");
        if (config.AutoGreetEnabled)
            queue.Enqueue(key, allowDetachedCustomGreeting: customVenueGreeting, deferSave: true);
    }

    private int GetGreetingTimerMinutes() => Math.Clamp(config.GreetingTimerMinutes, 1, 360);

    private void EnsureBlacklistedSessionPresence(VenueProfile venue, VisitorKey key, bool hereWhenArrived, bool countVisit)
    {
        if (!config.ShowBlacklistedInActiveVisitors)
            return;

        var now = DateTimeOffset.UtcNow;
        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        var visitor = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var existingVisitor) ? existingVisitor : Visitor.FromKey(key);

        if (countVisit && (existing is null || !existing.Present))
            visitor.TotalVisitCount++;

        visitor.LastSeenUtc = now;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        if (existing is null)
        {
            session.NightlyVisitors.Add(new SessionVisitorState
            {
                Key = key,
                EnteredUtc = now,
                LastSeenUtc = now,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
                HereWhenArrived = hereWhenArrived,
            });
        }
        else
        {
            existing.Present = true;
            existing.LastSeenUtc = now;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);
            if (hereWhenArrived)
                existing.HereWhenArrived = true;
        }

        RepairAndRequestSave();
    }

    private SessionVisitorState EnsureSessionVisitor(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull ?? venues.ActiveVenue;
        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        if (existing is not null) return existing;

        existing = new SessionVisitorState { Key = key, Present = false, ReturningThisSession = false };
        session.NightlyVisitors.Add(existing);
        return existing;
    }

    private static bool HasBeenGreetedBefore(Visitor? visitor) => visitor?.HasBeenGreeted == true;

    private static bool SameKey(VisitorKey a, VisitorKey b) =>
        string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
}
