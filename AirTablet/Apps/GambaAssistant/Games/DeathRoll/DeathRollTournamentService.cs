using System.Reflection;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using GambaAssistant.Models.Players;
using GambaAssistant.Services;

namespace GambaAssistant.Games.DeathRoll;

public sealed class DeathRollTournamentService : IDisposable
{
    private readonly Configuration config;
    private readonly PartyService party;
    private readonly ChatQueueService chat;
    private readonly LogService log;
    private static readonly Regex RandomRangeRegex = new(@"\(\s*1\s*-\s*(?<max>\d{1,6})\s*\)|\b1\s*-\s*(?<max2>\d{1,6})\b|\(?\s*out\s+of\s+(?<max3>\d{1,6})\.?(?:\s*\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameBeforeRandomRegex = new(@"(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\s+(?:rolls?|rolled|random|dice)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private double nextDemoRollTime;
    private readonly Queue<(string Player, int Max)> demoRolls = new();

    public DeathRollTournament Tournament { get; }

    public DeathRollTournamentService(Configuration config, PartyService party, ChatQueueService chat, LogService log, DeathRollTournament? tournament = null)
    {
        this.config = config;
        this.party = party;
        this.chat = chat;
        this.log = log;
        Tournament = tournament ?? new DeathRollTournament();
        Tournament.NormalizeAfterLoad();
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    public bool AddEntrant(PlayerIdentity identity, out string reason)
    {
        reason = string.Empty;
        if (Tournament.Status != DeathRollTournamentStatus.Setup)
        {
            reason = "Entrants can only be changed before the tournament starts.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(identity.Name))
        {
            reason = "Player name is required.";
            return false;
        }

        if (Tournament.Entrants.Count >= config.DeathRoll.MaxPlayers)
        {
            reason = $"Tournament is limited to {config.DeathRoll.MaxPlayers} entrants.";
            return false;
        }

        if (Tournament.Entrants.Any(p => SameIdentity(p.Identity, identity)))
        {
            reason = $"{identity} is already entered.";
            return false;
        }

        Tournament.Entrants.Add(new DeathRollPlayer { Identity = identity });
        log.Add(LogCategory.Info, $"DRT entrant added: {identity}.");
        return true;
    }

    public void RemoveEntrant(DeathRollPlayer player)
    {
        if (Tournament.Status != DeathRollTournamentStatus.Setup)
            return;

        Tournament.Entrants.Remove(player);
        log.Add(LogCategory.Info, $"DRT entrant removed: {player.DisplayName}.");
    }

    public int AddPartyMembers()
    {
        var added = 0;
        foreach (var p in party.GetPartyTableOrder().Where(p => p.Status != Models.Players.PlayerStatus.Dealer))
        {
            if (AddEntrant(p.Identity, out _)) added++;
        }
        return added;
    }

    public bool TryAddCurrentTarget(out string reason)
    {
        reason = string.Empty;
        try
        {
            var target = DalamudServices.TargetManager.Target;
            if (target is null)
            {
                reason = "No current target is selected. Use Add Yourself to add your own character.";
                return false;
            }

            var identity = TryResolveTargetIdentity(target, out var resolutionLog);
            if (string.IsNullOrWhiteSpace(identity.Name))
            {
                reason = $"Current target did not expose a readable player identity. {resolutionLog}".Trim();
                log.Add(LogCategory.Warnings, reason);
                return false;
            }

            var added = AddEntrant(identity, out reason);
            if (added)
                log.Add(LogCategory.Info, $"DRT target added from current target: {identity}. {resolutionLog}".Trim());
            else
                log.Add(LogCategory.Warnings, $"DRT could not add current target '{identity}': {reason}. {resolutionLog}".Trim());

            return added;
        }
        catch (Exception ex)
        {
            reason = $"Could not read current target: {ex.Message}";
            log.Add(LogCategory.Warnings, reason);
            return false;
        }
    }

    public bool TryAddLocalPlayer(out string reason, string? context = null)
    {
        reason = string.Empty;
        var identity = GetLocalPlayerIdentity();
        if (string.IsNullOrWhiteSpace(identity.Name))
        {
            reason = string.IsNullOrWhiteSpace(context) ? "Could not read your local character name." : $"{context} Could not read your local character name.";
            return false;
        }

        var added = AddEntrant(identity, out reason);
        if (added && !string.IsNullOrWhiteSpace(context))
            log.Add(LogCategory.Info, context);
        return added;
    }

    public bool CanStart(out string reason)
    {
        reason = string.Empty;
        var count = Tournament.Entrants.Count;
        if (Tournament.Status != DeathRollTournamentStatus.Setup)
        {
            reason = "Tournament is already running.";
            return false;
        }
        if (count < 2)
        {
            reason = "At least 2 players are required.";
            return false;
        }
        if (count % 2 != 0)
        {
            reason = "An even number of entrants is required.";
            return false;
        }
        if (count > config.DeathRoll.MaxPlayers)
        {
            reason = $"Entrant count exceeds the configured maximum of {config.DeathRoll.MaxPlayers}.";
            return false;
        }
        return true;
    }

    public bool StartTournament(out string reason)
    {
        if (!CanStart(out reason)) return false;

        Tournament.Rounds.Clear();
        Tournament.ActiveMatchId = null;
        foreach (var entrant in Tournament.Entrants)
            entrant.Eliminated = false;

        var firstRound = new List<DeathRollMatch>();
        var entrants = Tournament.Entrants
            .OrderBy(_ => Random.Shared.Next())
            .ToList();
        log.Add(LogCategory.Info, "DRT bracket entrants randomized for tournament seeding.");
        for (var i = 0; i < entrants.Count; i += 2)
        {
            firstRound.Add(new DeathRollMatch
            {
                RoundIndex = 0,
                MatchIndex = firstRound.Count,
                PlayerA = entrants[i],
                PlayerB = entrants[i + 1],
                CurrentMax = Tournament.StartingMax,
                Status = DeathRollMatchStatus.Waiting,
            });
        }

        Tournament.Rounds.Add(firstRound);

        // Pre-generate the full bracket so the DRT page shows the whole tournament
        // immediately. Later rounds start as TBD placeholders and are populated
        // from completed winners round-by-round.
        var matchCount = firstRound.Count / 2;
        var roundIndex = 1;
        while (matchCount >= 1)
        {
            var round = new List<DeathRollMatch>();
            for (var i = 0; i < matchCount; i++)
            {
                round.Add(new DeathRollMatch
                {
                    RoundIndex = roundIndex,
                    MatchIndex = i,
                    CurrentMax = Tournament.StartingMax,
                    Status = DeathRollMatchStatus.Waiting,
                });
            }

            Tournament.Rounds.Add(round);
            matchCount /= 2;
            roundIndex++;
        }

        Tournament.Status = DeathRollTournamentStatus.Running;
        config.DeathRoll.JoinBroadcastActive = false;
        Tournament.ActiveMatchId = firstRound.First().Id;
        log.Add(LogCategory.Info, $"DRT started with {entrants.Count} entrants and {Tournament.Rounds.Count} generated bracket round(s).");
        EnqueueDrtChat($"Death Roll Tournament started with {entrants.Count} players. Starting {MatchLabel(firstRound.First())}: {firstRound.First().PlayerA?.DisplayName} vs {firstRound.First().PlayerB?.DisplayName}.");
        PromptSeedingRolls(firstRound.First());
        return true;
    }

    public void ResetTournament()
    {
        Tournament.Status = DeathRollTournamentStatus.Setup;
        config.DeathRoll.JoinBroadcastActive = false;
        Tournament.Rounds.Clear();
        Tournament.ActiveMatchId = null;
        foreach (var entrant in Tournament.Entrants)
            entrant.Eliminated = false;
        demoRolls.Clear();
        log.Add(LogCategory.Info, "DRT reset to setup.");
    }

    public void ClearEntrants()
    {
        if (Tournament.Status != DeathRollTournamentStatus.Setup) return;
        Tournament.Entrants.Clear();
        config.DeathRoll.JoinBroadcastActive = false;
    }

    public void BroadcastJoinPrompt()
    {
        if (Tournament.Status != DeathRollTournamentStatus.Setup)
        {
            log.Add(LogCategory.Warnings, "!join broadcast can only be enabled during DRT setup.");
            return;
        }

        config.DeathRoll.JoinBroadcastActive = true;
        EnqueueDrtChat("Type !join in chat to join the DRT Tournament");
        log.Add(LogCategory.Info, "DRT !join listener enabled and broadcast queued.");
    }

    public void StopJoinBroadcast()
    {
        config.DeathRoll.JoinBroadcastActive = false;
        log.Add(LogCategory.Info, "DRT !join listener disabled.");
    }

    public string GetMatchLabel(DeathRollMatch match)
        => DeathRollTournamentLabels.GetMatchLabel(match.RoundIndex, match.MatchIndex, Tournament.Rounds.Count);

    public string GetStageName(int roundIndex, bool plural = false)
        => DeathRollTournamentLabels.GetStageName(roundIndex, Tournament.Rounds.Count, plural);

    private string MatchLabel(DeathRollMatch match) => GetMatchLabel(match);

    public void SetActiveMatch(DeathRollMatch match)
    {
        Tournament.ActiveMatchId = match.Id;
        log.Add(LogCategory.Info, $"DRT active match set to {MatchLabel(match)}: {match.PlayerA?.DisplayName} vs {match.PlayerB?.DisplayName}.");
    }

    public void PromptSeedingRolls(DeathRollMatch match)
    {
        if (match.PlayerA is null || match.PlayerB is null || match.Status == DeathRollMatchStatus.Complete) return;
        match.Status = DeathRollMatchStatus.SeedingRolls;
        EnqueueDrtChat($"DRT {MatchLabel(match)}: {match.PlayerA.DisplayName} vs {match.PlayerB.DisplayName}. Both players roll {GetSeedRollCommand()}. Higher roll goes first.");
        QueueAutomaticSeedRollIfNeeded(match, match.PlayerA);
        QueueAutomaticSeedRollIfNeeded(match, match.PlayerB);
    }

    public void PromptCurrentTurn(DeathRollMatch match)
    {
        if (match.Status != DeathRollMatchStatus.Playing || match.CurrentTurn is null) return;
        if (!match.FirstDeathRollTaken && match.CurrentMax == Tournament.StartingMax)
            EnqueueDrtChat($"DRT {MatchLabel(match)}: {match.CurrentTurn.DisplayName}, roll {GetRollCommand()}.");
        else
            EnqueueDrtChat($"DRT {MatchLabel(match)}: {match.CurrentTurn.DisplayName}, roll {GetRollCommand(match.CurrentMax)}.");

        QueueAutomaticRollIfNeeded(match.CurrentTurn, match.CurrentMax);
    }

    private void EnqueueDrtChat(string message)
    {
        if (config.DeathRoll.DisableChatBroadcasts)
        {
            log.Add(LogCategory.ChatQueue, $"DRT broadcast suppressed: {message}");
            return;
        }

        log.Add(LogCategory.ChatQueue, $"DRT broadcast queued ({NormalizeDeathRollChatChannel(config.DeathRoll.ChatChannel)}): {message}");
        chat.EnqueueDeathRoll(message);
    }

    private bool UseDiceCommandWording() => NormalizeDeathRollChatChannel(config.DeathRoll.ChatChannel) == "/party";

    private bool RequireDiceRollsInPartyChat()
        => UseDiceCommandWording() && config.DeathRoll.RequireDiceRollsInPartyChat;

    private string GetRollCommand(int? max = null)
    {
        var command = UseDiceCommandWording() ? "/dice" : "/random";
        return max.HasValue ? $"{command} {max.Value}" : command;
    }

    private string GetExpectedRollCommand(DeathRollMatch match)
        => !match.FirstDeathRollTaken && match.CurrentMax == Tournament.StartingMax ? GetRollCommand() : GetRollCommand(match.CurrentMax);

    private string GetSeedRollCommand() => GetRollCommand(Tournament.SeedingMax);

    private void AnnounceWrongRollRange(DeathRollMatch match, DeathRollPlayer player)
        => EnqueueDrtChat($"DRT {MatchLabel(match)}: {player.DisplayName}, use {GetExpectedRollCommand(match)}.");

    private void AnnounceWrongSeedRollRange(DeathRollMatch match, DeathRollPlayer player)
        => EnqueueDrtChat($"DRT {MatchLabel(match)}: {player.DisplayName}, use {GetSeedRollCommand()} for your seed roll.");

    private static string NormalizeDeathRollChatChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "/say" or "say" or "/s" or "s" => "/say",
        "/shout" or "shout" or "/sh" or "sh" => "/shout",
        "/yell" or "yell" or "/y" or "y" => "/yell",
        "/party" or "party" or "/p" or "p" => "/party",
        _ => "/party",
    };

    public bool TryHandleRandomRoll(string playerName, int value, int? maxFromLine = null)
    {
        var match = Tournament.ActiveMatch;
        if (Tournament.Status != DeathRollTournamentStatus.Running || match is null || match.Status == DeathRollMatchStatus.Complete)
            return false;

        var player = match.GetPlayer(playerName);
        if (player is null)
            return false;

        if (match.Status == DeathRollMatchStatus.SeedingRolls || match.Status == DeathRollMatchStatus.Waiting)
        {
            if (!maxFromLine.HasValue)
            {
                log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s seed roll {value}; could not verify they used {GetSeedRollCommand()}.");
                AnnounceWrongSeedRollRange(match, player);
                return true;
            }

            if (maxFromLine.Value != Tournament.SeedingMax)
            {
                log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s seed roll {value}/{maxFromLine.Value}; expected {GetSeedRollCommand()}.");
                AnnounceWrongSeedRollRange(match, player);
                return true;
            }

            var isPlayerA = SameIdentity(player.Identity, match.PlayerA?.Identity ?? default);
            var isPlayerB = SameIdentity(player.Identity, match.PlayerB?.Identity ?? default);
            if (isPlayerA && match.SeedRollA.HasValue)
            {
                log.Add(LogCategory.Warnings, $"DRT ignored extra seed roll from {player.DisplayName}; their seed roll is already locked at {match.SeedRollA.Value}.");
                return true;
            }
            if (isPlayerB && match.SeedRollB.HasValue)
            {
                log.Add(LogCategory.Warnings, $"DRT ignored extra seed roll from {player.DisplayName}; their seed roll is already locked at {match.SeedRollB.Value}.");
                return true;
            }

            if (isPlayerA)
                match.SeedRollA = value;
            else if (isPlayerB)
                match.SeedRollB = value;

            match.Status = DeathRollMatchStatus.SeedingRolls;
            match.History.Add($"{player.DisplayName} seed rolled {value}/{Tournament.SeedingMax}. Locked in.");
            log.Add(LogCategory.Info, $"DRT {MatchLabel(match)}: {player.DisplayName} seed rolled {value}; locked in.");

            if (match.SeedRollA.HasValue && match.SeedRollB.HasValue)
                ResolveSeeding(match);

            return true;
        }

        if (match.Status != DeathRollMatchStatus.Playing || match.CurrentTurn is null)
            return false;

        if (!SameIdentity(player.Identity, match.CurrentTurn.Identity))
        {
            log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s roll because it is {match.CurrentTurn.DisplayName}'s turn.");
            return true;
        }

        var isOpeningDeathRoll = !match.FirstDeathRollTaken && match.CurrentMax == Tournament.StartingMax;
        if (isOpeningDeathRoll && DateTime.UtcNow < match.OpeningRollsAcceptedAfterUtc)
        {
            log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s roll {value}; opening death roll is not accepting rolls yet. This prevents the final seed roll chat line from being double-counted as the first death roll.");
            return true;
        }

        if (!maxFromLine.HasValue)
        {
            if (!isOpeningDeathRoll)
            {
                log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s roll {value}; could not verify they used {GetRollCommand(match.CurrentMax)}.");
                AnnounceWrongRollRange(match, player);
                return true;
            }
        }
        else if (isOpeningDeathRoll && maxFromLine.Value == Tournament.SeedingMax)
        {
            log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s roll {value}/{maxFromLine.Value}; that is a seed-roll range, not the opening death roll command.");
            AnnounceWrongRollRange(match, player);
            return true;
        }
        else if (maxFromLine.Value != match.CurrentMax)
        {
            log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s roll {value}/{maxFromLine.Value}; expected {GetExpectedRollCommand(match)}.");
            AnnounceWrongRollRange(match, player);
            return true;
        }

        if (value == 0 && isOpeningDeathRoll)
        {
            var otherPlayer = match.OtherPlayer(player);
            var behavior = NormalizeOpeningZeroRollBehavior(config.DeathRoll.OpeningZeroRollBehavior);
            if (behavior == "skip" && otherPlayer is not null)
            {
                match.CurrentTurn = otherPlayer;
                match.History.Add($"{player.DisplayName} rolled 0 on the opening death roll; turn skipped to {otherPlayer.DisplayName}.");
                log.Add(LogCategory.Info, $"DRT {MatchLabel(match)}: {player.DisplayName} rolled 0 on the opening death roll; skipping to {otherPlayer.DisplayName}.");
                EnqueueDrtChat($"DRT {MatchLabel(match)}: {player.DisplayName} rolled 0. {otherPlayer.DisplayName}, roll {GetRollCommand()}.");
                QueueAutomaticRollIfNeeded(otherPlayer, match.CurrentMax);
                return true;
            }

            CompleteMatch(match, otherPlayer, player, "rolled 0 on the opening death roll");
            return true;
        }

        if (value < 1 || value > match.CurrentMax)
            return true;

        var rollLabel = maxFromLine.HasValue ? $"{value}/{match.CurrentMax}" : $"{value}/default {match.CurrentMax}";
        match.FirstDeathRollTaken = true;
        match.History.Add($"{player.DisplayName} rolled {rollLabel}.");
        log.Add(LogCategory.Info, $"DRT {MatchLabel(match)}: {player.DisplayName} rolled {rollLabel}.");

        if (value == 1)
        {
            var winner = match.OtherPlayer(player);
            CompleteMatch(match, winner, player);
            return true;
        }

        match.CurrentMax = value;
        match.CurrentTurn = match.OtherPlayer(player);
        if (config.DeathRoll.AnnounceNextTurnAfterRoll)
            PromptCurrentTurn(match);
        else if (match.CurrentTurn is not null)
            QueueAutomaticRollIfNeeded(match.CurrentTurn, match.CurrentMax);
        return true;
    }

    private void ResolveSeeding(DeathRollMatch match)
    {
        if (!match.SeedRollA.HasValue || !match.SeedRollB.HasValue || match.PlayerA is null || match.PlayerB is null)
            return;

        if (match.SeedRollA.Value == match.SeedRollB.Value)
        {
            EnqueueDrtChat($"DRT {MatchLabel(match)}: seed tie at {match.SeedRollA.Value}. Roll {GetSeedRollCommand()} again.");
            match.History.Add($"Seed tie at {match.SeedRollA.Value}; re-roll seeding.");
            match.SeedRollA = null;
            match.SeedRollB = null;
            QueueAutomaticSeedRollIfNeeded(match, match.PlayerA);
            QueueAutomaticSeedRollIfNeeded(match, match.PlayerB);
            return;
        }

        match.CurrentTurn = match.SeedRollA.Value > match.SeedRollB.Value ? match.PlayerA : match.PlayerB;
        match.CurrentMax = Tournament.StartingMax;
        match.FirstDeathRollTaken = false;
        match.OpeningRollsAcceptedAfterUtc = DateTime.UtcNow.AddSeconds(0.75);
        match.Status = DeathRollMatchStatus.Playing;
        match.History.Add($"{match.CurrentTurn.DisplayName} goes first after seed rolls {match.SeedRollA}/{match.SeedRollB}.");
        EnqueueDrtChat($"DRT {MatchLabel(match)}: {match.CurrentTurn.DisplayName} goes first. Start with {GetRollCommand()}.");
        QueueAutomaticRollIfNeeded(match.CurrentTurn, match.CurrentMax);
    }

    private void CompleteMatch(DeathRollMatch match, DeathRollPlayer? winner, DeathRollPlayer loser, string eliminationReason = "rolled 1")
    {
        if (winner is null) return;
        match.Winner = winner;
        match.Loser = loser;
        match.Status = DeathRollMatchStatus.Complete;
        match.CurrentTurn = null;
        loser.Eliminated = true;
        match.History.Add($"{loser.DisplayName} {eliminationReason} and is eliminated. {winner.DisplayName} advances.");
        EnqueueDrtChat($"DRT {MatchLabel(match)}: {loser.DisplayName} {eliminationReason} and is eliminated. {winner.DisplayName} advances.");
        log.Add(LogCategory.Info, $"DRT {MatchLabel(match)} complete. Winner: {winner.DisplayName}.");
        AdvanceBracketIfReady();
    }

    private void AdvanceBracketIfReady()
    {
        var completedMatch = Tournament.AllMatches.LastOrDefault(m => m.Status == DeathRollMatchStatus.Complete && m.Winner is not null);
        if (completedMatch is null)
        {
            SelectNextWaitingMatch();
            return;
        }

        var currentRound = Tournament.Rounds.ElementAtOrDefault(completedMatch.RoundIndex);
        if (currentRound is null)
        {
            SelectNextWaitingMatch();
            return;
        }

        if (currentRound.Any(m => m.PlayerA is not null && m.PlayerB is not null && m.Status != DeathRollMatchStatus.Complete && m.Status != DeathRollMatchStatus.Bye))
        {
            SelectNextWaitingMatch();
            return;
        }

        var winners = currentRound.Select(m => m.Winner).Where(w => w is not null).Cast<DeathRollPlayer>().ToList();
        if (winners.Count == 1)
        {
            Tournament.Status = DeathRollTournamentStatus.Complete;
            Tournament.ActiveMatchId = null;
            EnqueueDrtChat($"DRT complete! Winner: {winners[0].DisplayName}.");
            log.Add(LogCategory.Info, $"DRT complete. Winner: {winners[0].DisplayName}.");
            return;
        }

        var nextRoundIndex = completedMatch.RoundIndex + 1;
        var nextRound = Tournament.Rounds.ElementAtOrDefault(nextRoundIndex);
        if (nextRound is null)
        {
            nextRound = new List<DeathRollMatch>();
            Tournament.Rounds.Add(nextRound);
        }

        for (var i = 0; i < winners.Count; i += 2)
        {
            var targetMatchIndex = i / 2;
            DeathRollMatch target;
            if (targetMatchIndex < nextRound.Count)
            {
                target = nextRound[targetMatchIndex];
            }
            else
            {
                target = new DeathRollMatch
                {
                    RoundIndex = nextRoundIndex,
                    MatchIndex = targetMatchIndex,
                    CurrentMax = Tournament.StartingMax,
                    Status = DeathRollMatchStatus.Waiting,
                };
                nextRound.Add(target);
            }

            target.PlayerA = winners[i];
            target.PlayerB = i + 1 < winners.Count ? winners[i + 1] : null;
            target.Winner = null;
            target.Loser = null;
            target.CurrentTurn = null;
            target.CurrentMax = Tournament.StartingMax;
            target.SeedRollA = null;
            target.SeedRollB = null;
            target.FirstDeathRollTaken = false;
            target.OpeningRollsAcceptedAfterUtc = DateTime.MinValue;
            target.History.Clear();

            if (target.PlayerB is null)
            {
                target.Winner = target.PlayerA;
                target.Status = DeathRollMatchStatus.Bye;
                target.History.Add($"{target.PlayerA?.DisplayName} advances by bye.");
            }
            else
            {
                target.Status = DeathRollMatchStatus.Waiting;
            }
        }

        SelectNextWaitingMatch();
    }

    private void SelectNextWaitingMatch()
    {
        var next = Tournament.AllMatches.FirstOrDefault(m =>
            m.PlayerA is not null &&
            m.PlayerB is not null &&
            m.Status is DeathRollMatchStatus.Waiting or DeathRollMatchStatus.SeedingRolls or DeathRollMatchStatus.Playing);
        if (next is null) return;
        Tournament.ActiveMatchId = next.Id;
        if (next.Status == DeathRollMatchStatus.Waiting)
            PromptSeedingRolls(next);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (chat.DemoMode)
            return;

        var sender = StripChatNoise(message.Sender.ToString());
        var body = StripChatNoise(message.Message.ToString());
        if (string.IsNullOrWhiteSpace(sender) && string.IsNullOrWhiteSpace(body)) return;

        if (TryHandleJoinMessage(sender, body, message))
            return;

        var combined = string.IsNullOrWhiteSpace(sender) ? body : $"{sender} {body}";
        if (!LooksLikeRandom(combined)) return;

        if (!LooksLikeRealDiceMessage(message))
        {
            if (Tournament.Status == DeathRollTournamentStatus.Running && Tournament.ActiveMatch is not null)
                log.Add(LogCategory.Warnings, $"DRT ignored dice-looking chat text because it did not contain the game's dice icon/autotranslate payloads. sender='{sender}', body='{body}', payloads='{GetMessagePayloadSummary(message)}'.");
            return;
        }

        if (!TryParseRandomRoll(combined, out var value, out var max))
        {
            if (Tournament.Status == DeathRollTournamentStatus.Running && Tournament.ActiveMatch is not null)
                log.Add(LogCategory.Warnings, $"DRT saw a random/roll chat line but could not parse it. sender='{sender}', body='{body}', combined='{combined}'.");
            return;
        }

        var roller = ResolveRollerName(sender, body, combined, max);
        if (string.IsNullOrWhiteSpace(roller))
        {
            if (Tournament.Status == DeathRollTournamentStatus.Running && Tournament.ActiveMatch is not null)
                log.Add(LogCategory.Warnings, $"DRT parsed random {value}/{(max.HasValue ? max.Value.ToString() : "?")} but could not identify the roller. sender='{sender}', body='{body}'.");
            return;
        }

        if (TryRejectRollCommandForConfiguredChannel(roller, message))
            return;

        TryHandleRandomRoll(roller, value, max);
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (!chat.DemoMode || demoRolls.Count == 0 || ImGuiNETShim.TimeNow() < nextDemoRollTime)
            return;

        var (player, max) = demoRolls.Dequeue();
        var value = Random.Shared.Next(1, Math.Max(2, max + 1));
        log.Add(LogCategory.Demo, $"Demo DRT random: {player} rolls {value}/{max}.");
        TryHandleRandomRoll(player, value, max);
        nextDemoRollTime = ImGuiNETShim.TimeNow() + chat.DeathRollDelaySeconds;
    }

    private void QueueDemoRoll(string player, int max)
    {
        demoRolls.Enqueue((player, max));
        if (nextDemoRollTime <= 0)
            nextDemoRollTime = ImGuiNETShim.TimeNow() + chat.DeathRollDelaySeconds;
    }

    private void QueueAutomaticSeedRollIfNeeded(DeathRollMatch match, DeathRollPlayer? player)
    {
        if (player is null)
            return;

        QueueAutomaticRollIfNeeded(player, Tournament.SeedingMax);
    }

    private void QueueAutomaticRollIfNeeded(DeathRollPlayer? player, int max)
    {
        if (player is null)
            return;

        if (chat.DemoMode)
            QueueDemoRoll(player.DisplayName, max);
    }

    private bool TryHandleJoinMessage(string sender, string body, IHandleableChatMessage message)
    {
        if (!config.DeathRoll.JoinBroadcastActive || Tournament.Status != DeathRollTournamentStatus.Setup)
            return false;

        if (!IsJoinCommandMessage(body))
            return false;

        if (!IsJoinChatChannelAllowed(message))
        {
            log.Add(LogCategory.Warnings, $"DRT !join saw a join message but ignored it because the incoming chat channel is not allowed for the configured DRT output. sender='{sender}', body='{body}', metadata='{GetMessageLogKindText(message)}; {GetChatTypeText(message)}'.");
            return false;
        }

        var identity = ParseJoinIdentity(sender);
        if (string.IsNullOrWhiteSpace(identity.Name))
            identity = ParseJoinIdentityFromBody(body);

        if (string.IsNullOrWhiteSpace(identity.Name))
        {
            log.Add(LogCategory.Warnings, $"DRT !join could not parse the joining player's name. sender='{sender}', body='{body}'.");
            return false;
        }

        if (AddEntrant(identity, out var reason))
        {
            EnqueueDrtChat($"{identity.Name} joined the DRT Tournament.");
            log.Add(LogCategory.Info, $"DRT !join added {identity}.");
        }
        else
        {
            log.Add(LogCategory.Warnings, $"DRT !join ignored {identity}: {reason}");
        }

        return true;
    }


    private static bool IsJoinCommandMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var cleaned = StripChatNoise(body);

        // Do not let our own broadcast prompt ("Type !join in chat...") join the
        // plugin owner. A valid join message should be just !join, optionally with
        // a chat/player prefix added by Dalamud or another chat formatter.
        if (Regex.IsMatch(cleaned, @"(?i)\btype\s+!join\b|\bto\s+join\s+the\s+DRT\s+Tournament\b"))
            return false;

        cleaned = Regex.Replace(cleaned, @"(?i)^\s*\[[^\]]+\]\s*", string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, @"^\s*[\p{L}\p{N}'\- ]+(?:@[\p{L}\p{N}'\- ]+)?\s*[:：>]\s*", string.Empty).Trim();

        return Regex.IsMatch(cleaned, @"(?i)^!join\s*[!.?,]?\s*$");
    }

    private bool IsJoinChatChannelAllowed(IHandleableChatMessage message)
    {
        var channel = GetIncomingTextChatChannel(message);
        if (UseDiceCommandWording())
            return channel == IncomingTextChatChannel.Party;

        return channel is IncomingTextChatChannel.Say or IncomingTextChatChannel.Yell or IncomingTextChatChannel.Shout;
    }

    private PlayerIdentity ParseJoinIdentity(string sender)
    {
        sender = StripChatNoise(sender);
        if (string.IsNullOrWhiteSpace(sender))
            return default;

        var atParts = sender.Split('@', 2, StringSplitOptions.TrimEntries);
        if (atParts.Length == 2)
            return new PlayerIdentity(CleanPlayerName(atParts[0]), CleanWorldName(atParts[1]));

        var world = GetLocalPlayerIdentity().World;
        return new PlayerIdentity(CleanPlayerName(sender), world);
    }


    private PlayerIdentity ParseJoinIdentityFromBody(string body)
    {
        body = StripChatNoise(body);
        if (string.IsNullOrWhiteSpace(body))
            return default;

        // Some Dalamud/chat-format combinations expose the sender separately, but
        // others include a display prefix in the body, such as
        // "Player Name: !join", "[Party] Player Name: !join", or
        // "Player Name@World !join". Fall back to parsing that prefix.
        var cleaned = Regex.Replace(body, @"(?i)^\s*\[[^\]]+\]\s*", string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, @"(?i)\b!join\b.*$", string.Empty).Trim();
        cleaned = cleaned.Trim(':', '：', '-', '–', '—', '>', ' ');

        if (string.IsNullOrWhiteSpace(cleaned))
            return default;

        var atParts = cleaned.Split('@', 2, StringSplitOptions.TrimEntries);
        if (atParts.Length == 2)
            return new PlayerIdentity(CleanPlayerName(atParts[0]), CleanWorldName(atParts[1]));

        var world = GetLocalPlayerIdentity().World;
        return new PlayerIdentity(CleanPlayerName(cleaned), world);
    }

    private static string CleanWorldName(string value)
    {
        value = StripChatNoise(value);
        value = Regex.Replace(value, @"[^\p{L}\p{N}'\- ]", string.Empty).Trim();
        return Regex.Replace(value, @"\s+", " ");
    }

    private static bool LooksLikeRandom(string text)
        => text.Contains("random", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dice", StringComparison.OrdinalIgnoreCase)
            || text.Contains("roll", StringComparison.OrdinalIgnoreCase);

    private bool TryRejectRollCommandForConfiguredChannel(string playerName, IHandleableChatMessage message)
    {
        var match = Tournament.ActiveMatch;
        if (Tournament.Status != DeathRollTournamentStatus.Running || match is null || match.Status == DeathRollMatchStatus.Complete)
            return false;

        var player = match.GetPlayer(playerName);
        if (player is null)
            return false;

        var configuredForParty = UseDiceCommandWording();
        var sourceChannel = GetIncomingRollChannel(message);

        // /dice in party still renders as visible "Random!" text, so command validation
        // must use Dalamud's LogKind/chat channel metadata instead of the displayed text.
        // Party DRT expects party /dice; all non-party DRT channels expect normal /random.
        var acceptsRoll = configuredForParty
            ? (!config.DeathRoll.RequireDiceRollsInPartyChat || sourceChannel == IncomingRollChannel.Party)
            : sourceChannel == IncomingRollChannel.Random;

        if (acceptsRoll)
            return false;

        var expectedCommand = match.Status is DeathRollMatchStatus.SeedingRolls or DeathRollMatchStatus.Waiting
            ? GetSeedRollCommand()
            : GetExpectedRollCommand(match);

        var chatType = GetChatTypeText(message);
        if (configuredForParty && config.DeathRoll.RequireDiceRollsInPartyChat)
        {
            log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s roll because party DRT is configured to require party /dice rolls. Expected {expectedCommand}. Incoming chat metadata: '{chatType}'.");
            EnqueueDrtChat($"DRT {MatchLabel(match)}: {player.DisplayName}, party DRT only accepts /dice rolls. Use {expectedCommand}.");
        }
        else
        {
            log.Add(LogCategory.Warnings, $"DRT ignored {player.DisplayName}'s roll because this DRT chat channel expects /random rolls. Expected {expectedCommand}. Incoming chat metadata: '{chatType}'.");
            EnqueueDrtChat($"DRT {MatchLabel(match)}: {player.DisplayName}, use {expectedCommand}.");
        }

        return true;
    }

    private enum IncomingTextChatChannel
    {
        Unknown,
        Party,
        Say,
        Yell,
        Shout
    }

    private static IncomingTextChatChannel GetIncomingTextChatChannel(IHandleableChatMessage message)
    {
        var logKind = GetMessageLogKindText(message);
        var metadata = GetChatTypeText(message);
        var combined = string.IsNullOrWhiteSpace(metadata) ? logKind : $"{logKind}; {metadata}";

        if (IsPartyChatKind(combined))
            return IncomingTextChatChannel.Party;
        if (IsSayChatKind(combined))
            return IncomingTextChatChannel.Say;
        if (IsYellChatKind(combined))
            return IncomingTextChatChannel.Yell;
        if (IsShoutChatKind(combined))
            return IncomingTextChatChannel.Shout;

        return IncomingTextChatChannel.Unknown;
    }

    private enum IncomingRollChannel
    {
        Unknown,
        Party,
        Random
    }

    private static IncomingRollChannel GetIncomingRollChannel(IHandleableChatMessage message)
    {
        var logKind = GetMessageLogKindText(message);
        if (IsPartyChatKind(logKind))
            return IncomingRollChannel.Party;
        if (IsRandomChatKind(logKind))
            return IncomingRollChannel.Random;

        var metadata = GetChatTypeText(message);
        if (IsPartyChatKind(metadata))
            return IncomingRollChannel.Party;
        if (IsRandomChatKind(metadata))
            return IncomingRollChannel.Random;

        return IncomingRollChannel.Unknown;
    }

    private static string GetMessageLogKindText(IHandleableChatMessage message)
    {
        try
        {
            return message.LogKind.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsPartyChatKind(string text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, @"(?i)(^|[^A-Za-z])(Party|PartyChat|PartyMember|EchoParty)([^A-Za-z]|$)");

    private static bool IsRandomChatKind(string text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, @"(?i)(^|[^A-Za-z])(Random|Roll)([^A-Za-z]|$)")
            && !IsPartyChatKind(text);

    private static bool IsSayChatKind(string text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, @"(?i)(^|[^A-Za-z])(Say|SayChat)([^A-Za-z]|$)");

    private static bool IsYellChatKind(string text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, @"(?i)(^|[^A-Za-z])(Yell|YellChat)([^A-Za-z]|$)");

    private static bool IsShoutChatKind(string text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, @"(?i)(^|[^A-Za-z])(Shout|ShoutChat)([^A-Za-z]|$)");

    private static string GetChatTypeText(IHandleableChatMessage message)
    {
        try
        {
            var parts = new List<string>();
            var messageType = message.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var property in messageType.GetProperties(flags))
            {
                if (!LooksLikeChatMetadataName(property.Name))
                    continue;

                try
                {
                    var value = property.GetValue(message);
                    if (value is not null)
                        parts.Add($"{property.Name}={value}");
                }
                catch
                {
                    // Some reflected properties may throw; skip them.
                }
            }

            foreach (var field in messageType.GetFields(flags))
            {
                if (!LooksLikeChatMetadataName(field.Name))
                    continue;

                try
                {
                    var value = field.GetValue(message);
                    if (value is not null)
                        parts.Add($"{field.Name}={value}");
                }
                catch
                {
                    // Some reflected fields may throw; skip them.
                }
            }

            if (parts.Count > 0)
                return string.Join("; ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // Fall through to unsafe default.
        }

        return string.Empty;
    }

    private static bool LooksLikeChatMetadataName(string name)
        => name.Contains("Type", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kind", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Channel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Mode", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Source", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Target", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Chat", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRealDiceMessage(IHandleableChatMessage message)
    {
        // Match BarManager's spoof guard: visible text is not enough because
        // players can type "Random!" or "Dice!" manually. Real FFXIV dice messages carry
        // non-text SeString payloads such as dice icons/autotranslate arrows.
        try
        {
            var sawPayload = false;
            foreach (var payload in message.Message.Payloads)
            {
                sawPayload = true;
                var typeName = payload.GetType().Name;
                if (typeName.Equals("TextPayload", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (typeName.Contains("Icon", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("AutoTranslate", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Bitmap", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return sawPayload && false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMessagePayloadSummary(IHandleableChatMessage message)
    {
        try
        {
            var types = message.Message.Payloads
                .Select(p => p.GetType().Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            return types.Length == 0 ? "none" : string.Join(", ", types);
        }
        catch
        {
            return "unavailable";
        }
    }

    private static bool TryParseRandomRoll(string text, out int value, out int? max)
    {
        value = 0;
        max = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // FFXIV /random and party /dice lines can come from chat as sender+body, and
        // different chat plugins can format them slightly differently, for example:
        //   Random! (1-10) 7
        //   Dice! (1-10) 7
        //   Random! (1-999) 737
        //   Jeszie Jane Random! (1-10) 7
        //   Jeszie Jane Dice! (1-10) 7
        //   Jeszie Jane rolls 7. (1-10)
        // Always extract the max from the range first, remove that range, then use
        // the last remaining standalone number as the rolled value.
        var range = RandomRangeRegex.Match(text);
        if (range.Success)
        {
            var maxText = range.Groups["max"].Success
                ? range.Groups["max"].Value
                : range.Groups["max2"].Success
                    ? range.Groups["max2"].Value
                    : range.Groups["max3"].Value;
            if (int.TryParse(maxText, out var parsedMax))
                max = parsedMax;
        }

        var withoutRange = RandomRangeRegex.Replace(text, " ");
        withoutRange = Regex.Replace(withoutRange, @"\s+", " ").Trim();

        // Current FFXIV English output for /random or /dice in chat often looks like:
        //   Random! You roll 3 (out of 10.)
        //   Dice! You roll 3 (out of 10.)
        //   Random! <Player> rolls 7 (out of 10.)
        //   Dice! <Player> rolls 7 (out of 10.)
        // Once the "out of N" range is removed, take the number after roll/rolls first.
        var rollWordMatch = Regex.Match(withoutRange, @"(?i)\b(?:you\s+)?rolls?\s+(?:a\s+|an\s+)?(?<value>\d{1,6})\b");
        if (rollWordMatch.Success && int.TryParse(rollWordMatch.Groups["value"].Value, out value) && value >= 0)
            return true;

        var numbers = Regex.Matches(withoutRange, @"(?<![A-Za-z0-9])\d{1,6}(?![A-Za-z0-9])");
        for (var i = numbers.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(numbers[i].Value, out value) && value >= 0)
                return true;
        }

        return false;
    }

    private string ResolveRollerName(string sender, string body, string combined, int? maxFromLine)
    {
        var localDisplay = GetLocalPlayerDisplayName();
        if (Regex.IsMatch(body, @"(?i)\byou\s+roll\b") || Regex.IsMatch(combined, @"(?i)\byou\s+roll\b"))
            return localDisplay;

        if (!string.IsNullOrWhiteSpace(sender) && !LooksLikeRandom(sender))
            return sender;

        var fromBody = ExtractNameBeforeRandom(body);
        if (!string.IsNullOrWhiteSpace(fromBody) && !fromBody.Equals("Random", StringComparison.OrdinalIgnoreCase))
            return fromBody;

        var fromCombined = ExtractNameBeforeRandom(combined);
        return fromCombined.Equals("Random", StringComparison.OrdinalIgnoreCase) ? string.Empty : fromCombined;
    }


    private PlayerIdentity TryResolveTargetIdentity(object target, out string resolutionLog)
    {
        var details = new List<string>();

        var directName = ReadTargetName(target);
        if (!string.IsNullOrWhiteSpace(directName))
            details.Add($"name='{directName}'");

        // First: direct party name match. This is what worked before and is still
        // the safest path when the selected target exposes a normal display name.
        var byName = FindPartyIdentityByName(directName);
        if (!string.IsNullOrWhiteSpace(byName.Name))
        {
            resolutionLog = $"Matched party member by target name.";
            return byName;
        }

        // Second: try object id / entity id properties on both the target and the
        // party member records. Different Dalamud versions expose different names.
        var targetObjectIds = GetObjectUIntProperties(target, "GameObjectId", "ObjectId", "EntityId", "ObjectIndex", "GameObjectID").ToList();
        if (targetObjectIds.Count > 0)
            details.Add($"ids={string.Join("/", targetObjectIds)}");

        foreach (var id in targetObjectIds)
        {
            var byObject = FindPartyIdentityByObjectId(id);
            if (!string.IsNullOrWhiteSpace(byObject.Name))
            {
                resolutionLog = $"Matched party member by object/entity id {id}.";
                return byObject;
            }
        }

        // Third: compare the target object address against party member GameObject
        // addresses when those are exposed.
        var targetAddresses = GetObjectAddressCandidates(target).ToList();
        if (targetAddresses.Count > 0)
            details.Add($"addr={string.Join("/", targetAddresses.Select(a => $"0x{a:X}"))}");

        foreach (var address in targetAddresses)
        {
            var byAddress = FindPartyIdentityByObjectAddress(address);
            if (!string.IsNullOrWhiteSpace(byAddress.Name))
            {
                resolutionLog = $"Matched party member by object address 0x{address:X}.";
                return byAddress;
            }
        }

        // Fourth: if the target is not in party or party metadata is incomplete,
        // add the readable target name with the best available world fallback.
        if (!string.IsNullOrWhiteSpace(directName))
        {
            var world = GetObjectWorld(target);
            if (string.IsNullOrWhiteSpace(world))
                world = GetLocalPlayerWorldIfNameMatches(directName);
            if (string.IsNullOrWhiteSpace(world))
                world = GetLocalPlayerIdentity().World;

            resolutionLog = details.Count > 0 ? $"Used target object fallback ({string.Join(", ", details)})." : "Used target name fallback.";
            return new PlayerIdentity(directName, world);
        }

        resolutionLog = details.Count > 0 ? $"Target details: {string.Join(", ", details)}." : string.Empty;
        return default;
    }

    private PlayerIdentity FindPartyIdentityByObjectId(uint objectId)
    {
        if (objectId == 0)
            return default;

        try
        {
            foreach (var member in DalamudServices.PartyList)
            {
                foreach (var memberObjectId in GetObjectUIntProperties(member, "GameObjectId", "ObjectId", "EntityId", "ObjectIndex", "GameObjectID"))
                {
                    if (memberObjectId != objectId)
                        continue;

                    var identity = GetPartyMemberIdentity(member);
                    if (!string.IsNullOrWhiteSpace(identity.Name))
                        return identity;
                }

                var gameObject = GetObjectProperty(member, "GameObject", "Object", "GameObjectReference");
                if (gameObject is not null)
                {
                    foreach (var memberObjectId in GetObjectUIntProperties(gameObject, "GameObjectId", "ObjectId", "EntityId", "ObjectIndex", "GameObjectID"))
                    {
                        if (memberObjectId != objectId)
                            continue;

                        var identity = GetPartyMemberIdentity(member);
                        if (!string.IsNullOrWhiteSpace(identity.Name))
                            return identity;
                    }
                }
            }
        }
        catch
        {
            // Fall through to normal name-based target handling.
        }

        return default;
    }

    private PlayerIdentity FindPartyIdentityByObjectAddress(ulong address)
    {
        if (address == 0)
            return default;

        try
        {
            foreach (var member in DalamudServices.PartyList)
            {
                var gameObject = GetObjectProperty(member, "GameObject", "Object", "GameObjectReference");
                foreach (var memberAddress in GetObjectAddressCandidates(member).Concat(gameObject is null ? Enumerable.Empty<ulong>() : GetObjectAddressCandidates(gameObject)))
                {
                    if (memberAddress != address)
                        continue;

                    var identity = GetPartyMemberIdentity(member);
                    if (!string.IsNullOrWhiteSpace(identity.Name))
                        return identity;
                }
            }
        }
        catch
        {
            // Fall through.
        }

        return default;
    }

    private static PlayerIdentity GetPartyMemberIdentity(object member)
    {
        try
        {
            var nameObj = member.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(member);
            var name = ExtractFriendlyText(nameObj).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return default;

            var world = string.Empty;
            var worldObj = member.GetType().GetProperty("World", BindingFlags.Instance | BindingFlags.Public)?.GetValue(member);
            if (worldObj is not null)
            {
                var valueProp = worldObj.GetType().GetProperty("ValueNullable")?.GetValue(worldObj)
                    ?? worldObj.GetType().GetProperty("Value")?.GetValue(worldObj)
                    ?? worldObj;
                var nameProp = valueProp?.GetType().GetProperty("Name")?.GetValue(valueProp);
                world = ExtractFriendlyText(nameProp);
            }

            return new PlayerIdentity(name, world);
        }
        catch
        {
            return default;
        }
    }

    private static string ReadTargetName(object target)
    {
        var name = CleanPlayerName(GetObjectTextProperty(target, "Name"));
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        // Some game object implementations expose the display name through slightly
        // different property names. Try the common options before failing.
        foreach (var property in new[] { "CharacterName", "DisplayName", "ObjectName", "NameString" })
        {
            name = CleanPlayerName(GetObjectTextProperty(target, property));
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        var fallback = CleanPlayerName(target.ToString() ?? string.Empty);
        return fallback.Contains("Dalamud", StringComparison.OrdinalIgnoreCase) || fallback.Contains("FFXIVClientStructs", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : fallback;
    }

    private PlayerIdentity FindPartyIdentityByName(string name)
    {
        var cleanName = CleanPlayerName(name);
        if (string.IsNullOrWhiteSpace(cleanName))
            return default;

        foreach (var partyPlayer in party.GetPartyTableOrder())
        {
            if (string.Equals(CleanPlayerName(partyPlayer.Identity.Name), cleanName, StringComparison.OrdinalIgnoreCase))
                return partyPlayer.Identity;
        }

        return default;
    }

    private static string CleanPlayerName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = StripChatNoise(value);
        value = Regex.Replace(value, @"\s*@\s*.*$", string.Empty).Trim();
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value;
    }

    private static PlayerIdentity GetLocalPlayerIdentity()
    {
        try
        {
            if (!DalamudServices.PlayerState.IsLoaded)
                return new PlayerIdentity(string.Empty, string.Empty);

            var name = DalamudServices.PlayerState.CharacterName.Trim();
            var world = DalamudServices.PlayerState.HomeWorld.Value.Name.ExtractText();
            return new PlayerIdentity(name, world);
        }
        catch
        {
            return new PlayerIdentity(string.Empty, string.Empty);
        }
    }

    private static string GetLocalPlayerWorldIfNameMatches(string name)
    {
        var local = GetLocalPlayerIdentity();
        return string.Equals(local.Name, name, StringComparison.OrdinalIgnoreCase) ? local.World : string.Empty;
    }

    private static string GetLocalPlayerDisplayName()
    {
        var identity = GetLocalPlayerIdentity();
        return string.IsNullOrWhiteSpace(identity.Name) ? string.Empty : identity.ToString();
    }

    private static string ExtractNameBeforeRandom(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var match = NameBeforeRandomRegex.Match(text);
        if (match.Success)
            return match.Groups["name"].Value.Trim();

        var idx = text.IndexOf("random", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) idx = text.IndexOf("roll", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return string.Empty;
        return text[..idx].Trim();
    }

    private static string StripChatNoise(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(c => !char.IsControl(c) && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '\'' or '-' or ',' or '.' or '@' or '(' or ')' or '!' or ':' or '：')).ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim().Trim('.', ' ');
    }

    private static string NormalizeOpeningZeroRollBehavior(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "skip" or "skip turn" or "skip_turn" => "skip",
        _ => "eliminate",
    };

    private static bool SameIdentity(PlayerIdentity a, PlayerIdentity b)
    {
        if (string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(b.Name)) return false;
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)) return false;
        return string.IsNullOrWhiteSpace(a.World) || string.IsNullOrWhiteSpace(b.World) || string.Equals(a.World, b.World, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<uint> GetObjectUIntProperties(object obj, params string[] properties)
    {
        var seen = new HashSet<uint>();
        foreach (var property in properties)
        {
            foreach (var value in GetPropertyValueCandidates(obj, property))
            {
                if (TryConvertUInt(value, out var parsed) && parsed != 0 && seen.Add(parsed))
                    yield return parsed;
            }
        }
    }

    private static IEnumerable<ulong> GetObjectAddressCandidates(object obj)
    {
        foreach (var property in new[] { "Address", "Pointer", "GameObjectAddress" })
        {
            foreach (var value in GetPropertyValueCandidates(obj, property))
            {
                if (TryConvertULong(value, out var parsed) && parsed != 0)
                    yield return parsed;
            }
        }
    }

    private static object? GetObjectProperty(object obj, params string[] properties)
    {
        foreach (var property in properties)
        {
            try
            {
                var value = obj.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj);
                if (value is not null)
                    return value;
            }
            catch
            {
                // Try the next property.
            }
        }

        return null;
    }

    private static IEnumerable<object?> GetPropertyValueCandidates(object obj, string property)
    {
        object? value = null;
        try
        {
            value = obj.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj);
        }
        catch
        {
            yield break;
        }

        if (value is null)
            yield break;

        yield return value;

        foreach (var nested in new[] { "Value", "ValueNullable", "Object", "GameObject" })
        {
            object? nestedValue = null;
            try
            {
                nestedValue = value.GetType().GetProperty(nested, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
            }
            catch
            {
                // Ignore this nested property.
            }

            if (nestedValue is not null)
                yield return nestedValue;
        }
    }

    private static bool TryConvertUInt(object? value, out uint result)
    {
        result = 0;
        if (value is null) return false;
        if (value is uint u) { result = u; return true; }
        if (value is int i && i >= 0) { result = (uint)i; return true; }
        if (value is ulong ul && ul <= uint.MaxValue) { result = (uint)ul; return true; }
        if (value is long l && l >= 0 && l <= uint.MaxValue) { result = (uint)l; return true; }
        return uint.TryParse(value.ToString(), out result);
    }

    private static bool TryConvertULong(object? value, out ulong result)
    {
        result = 0;
        if (value is null) return false;
        if (value is nint ni && ni != 0) { result = (ulong)ni; return true; }
        if (value is nuint nui && nui != 0) { result = (ulong)nui; return true; }
        if (value is IntPtr ip && ip != IntPtr.Zero) { result = (ulong)ip.ToInt64(); return true; }
        if (value is UIntPtr up && up != UIntPtr.Zero) { result = up.ToUInt64(); return true; }
        if (value is ulong ul) { result = ul; return true; }
        if (value is long l && l >= 0) { result = (ulong)l; return true; }
        return ulong.TryParse(value.ToString(), out result);
    }

    private static string GetObjectTextProperty(object obj, string property)
    {
        var value = obj.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj);
        return ExtractFriendlyText(value);
    }

    private static string InvokeNoArgumentExtractText(object value)
    {
        try
        {
            var method = value.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, "ExtractText", StringComparison.Ordinal)
                    && m.GetParameters().Length == 0
                    && m.ReturnType == typeof(string));

            var result = method?.Invoke(value, Array.Empty<object>())?.ToString();
            return result ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractFriendlyText(object? value)
    {
        if (value is null) return string.Empty;
        if (value is string s) return s;

        var type = value.GetType();

        var textValue = type.GetProperty("TextValue", BindingFlags.Instance | BindingFlags.Public)?.GetValue(value)?.ToString();
        if (!string.IsNullOrWhiteSpace(textValue)) return textValue;

        var extractText = InvokeNoArgumentExtractText(value);
        if (!string.IsNullOrWhiteSpace(extractText)) return extractText;

        var toString = value.ToString();
        if (string.IsNullOrWhiteSpace(toString)) return string.Empty;

        // Some SeString-like objects do not expose TextValue but ToString can include only the readable player name.
        // Avoid returning obvious type names such as "Dalamud....SeString" as a fake player name.
        return toString.Contains('.', StringComparison.Ordinal) && toString.Contains("SeString", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : toString;
    }

    private static string GetObjectWorld(object obj)
    {
        var homeWorld = obj.GetType().GetProperty("HomeWorld", BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj);
        if (homeWorld is null) return string.Empty;
        var valueProp = homeWorld.GetType().GetProperty("Value")?.GetValue(homeWorld) ?? homeWorld.GetType().GetProperty("ValueNullable")?.GetValue(homeWorld);
        var nameProp = valueProp?.GetType().GetProperty("Name")?.GetValue(valueProp);
        return ExtractFriendlyText(nameProp);
    }

    public void Dispose()
    {
        DalamudServices.ChatGui.ChatMessage -= OnChatMessage;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
    }

    private static class ImGuiNETShim
    {
        public static double TimeNow()
        {
            try { return Dalamud.Bindings.ImGui.ImGui.GetTime(); }
            catch { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0; }
        }
    }
}
