using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed class PlayerSessionService
{
    private readonly BlackjackSession session;
    private readonly LogService log;
    public PlayerSessionService(BlackjackSession session, LogService log) { this.session = session; this.log = log; }

    public void SyncParty(IEnumerable<PlayerSessionState> partyOrder)
    {
        NormalizeTrackedPlayers();
        var partyList = DeduplicatePartyOrder(partyOrder.ToList());
        var dealer = partyList.FirstOrDefault(p => p.Status == PlayerStatus.Dealer);
        if (dealer is not null)
            SyncDealer(dealer);

        foreach (var partyPlayer in partyList.Where(p => p.Status != PlayerStatus.Dealer))
        {
            var existing = FindExistingPlayerForPartySync(partyPlayer.Identity);
            if (existing == null)
            {
                session.SessionPlayers.Add(partyPlayer);
                log.Add(LogCategory.Info, $"Added party member {partyPlayer.DisplayName} as {partyPlayer.Status}.");
            }
            else
            {
                MergeLivePartyIdentity(existing, partyPlayer);
            }
        }

        foreach (var existing in session.SessionPlayers.Where(p => p.Status != PlayerStatus.Dealer).ToList())
        {
            if (partyList.All(p => p.Status == PlayerStatus.Dealer || (!IsSamePlayerForSync(p.Identity, existing.Identity) && !IsSameNormalizedName(p.Identity, existing.Identity))) && existing.Status != PlayerStatus.CashedOut)
            {
                existing.Status = PlayerStatus.LeftDisconnected;

                if (existing.Bank.TotalTracked <= 0 && CanAutoRemoveDisconnectedPlayer(existing))
                    RemovePlayer(existing, "Auto-removed disconnected player with 0 tracked gil.");
            }
        }

        if (session.Round.Phase == BlackjackPhase.BettingOpen
            && partyList.All(p => p.Status == PlayerStatus.Dealer))
            CloseBetting("Betting closed automatically because no players remain in the live party");

        NormalizeTrackedPlayers();
        session.SessionPlayers.Sort((a,b) => a.PartySlot.CompareTo(b.PartySlot));
    }

    public bool CloseBetting(string reason)
    {
        if (session.Round.Phase != BlackjackPhase.BettingOpen)
            return false;

        long returnedBets = 0;
        var affectedPlayers = 0;
        foreach (var player in session.SessionPlayers.Where(p => p.Status != PlayerStatus.Dealer))
        {
            if (player.Bank.ActiveBet > 0)
            {
                returnedBets += player.Bank.ActiveBet;
                player.Bank.Available += player.Bank.ActiveBet;
                player.Bank.ActiveBet = 0;
                affectedPlayers++;
            }

            player.BetConfirmed = false;
            player.Hands.Clear();
            if (player.Status == PlayerStatus.Playing)
                player.Status = player.Bank.TotalTracked > 0 ? PlayerStatus.SittingOut : PlayerStatus.CashedOut;
        }

        session.Round.Players.Clear();
        session.Round.ActivePlayerIndex = 0;
        session.Round.ActiveHandIndex = 0;
        session.Round.Phase = BlackjackPhase.CashOutBetweenHands;
        log.Add(LogCategory.RoundFlow, $"{reason}. Returned {returnedBets:N0} gil in pending bets for {affectedPlayers} player(s).");
        return true;
    }

    public void RemovePlayer(PlayerSessionState player, string reason = "Removed from table")
    {
        VoidActiveHandsForDisconnected(player);
        session.Round.Players.RemoveAll(p => IsSamePlayerForSync(p.Identity, player.Identity) || IsSameNormalizedName(p.Identity, player.Identity));
        if (session.Round.ActivePlayerIndex >= session.Round.Players.Count)
            session.Round.ActivePlayerIndex = Math.Max(0, session.Round.Players.Count - 1);
        if (session.Round.Players.Count == 0 && session.Round.Phase is not BlackjackPhase.Idle)
            session.Round.Phase = BlackjackPhase.CashOutBetweenHands;

        session.SessionPlayers.RemoveAll(p => IsSamePlayerForSync(p.Identity, player.Identity) || IsSameNormalizedName(p.Identity, player.Identity));
        NormalizeTrackedPlayers();
        log.Add(LogCategory.Info, $"{reason}: {player.DisplayName}.");
    }

    private void SyncDealer(PlayerSessionState liveDealer)
    {
        liveDealer.Status = PlayerStatus.Dealer;
        liveDealer.PartySlot = 1;
        liveDealer.BetConfirmed = false;
        liveDealer.Hands.Clear();

        var dealer = session.SessionPlayers.FirstOrDefault(p => p.Status == PlayerStatus.Dealer)
            ?? session.SessionPlayers.FirstOrDefault(p => IsSamePlayerForSync(p.Identity, liveDealer.Identity));

        if (dealer is null)
        {
            dealer = new PlayerSessionState
            {
                Identity = liveDealer.Identity,
                PartySlot = 1,
                Status = PlayerStatus.Dealer
            };
            session.SessionPlayers.Add(dealer);
            log.Add(LogCategory.Info, $"Added dealer {dealer.DisplayName}.");
        }
        else
        {
            var oldDisplayName = dealer.DisplayName;
            dealer.Identity = liveDealer.Identity;
            dealer.PartySlot = 1;
            dealer.Status = PlayerStatus.Dealer;
            dealer.BetConfirmed = false;
            dealer.Hands.Clear();
            dealer.Bank ??= new PlayerBank();

            if (!string.Equals(oldDisplayName, dealer.DisplayName, StringComparison.OrdinalIgnoreCase))
                log.Add(LogCategory.Info, $"Updated dealer identity to {dealer.DisplayName}.");
        }

        foreach (var duplicate in session.SessionPlayers
            .Where(p => !ReferenceEquals(p, dealer) && (p.Status == PlayerStatus.Dealer || IsSamePlayerForSync(p.Identity, liveDealer.Identity)))
            .ToList())
        {
            session.SessionPlayers.Remove(duplicate);
            log.Add(LogCategory.Info, $"Removed duplicate dealer entry {duplicate.DisplayName}.");
        }

        session.Round.Players.RemoveAll(p => p.Status == PlayerStatus.Dealer || IsSamePlayerForSync(p.Identity, liveDealer.Identity));
        if (session.Round.ActivePlayerIndex >= session.Round.Players.Count)
            session.Round.ActivePlayerIndex = Math.Max(0, session.Round.Players.Count - 1);
    }

    private bool ShouldRestoreRoundPlayerAsPlaying(PlayerSessionState player)
    {
        if (!IsCurrentRoundPlayer(player))
            return false;

        if (session.Round.Phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands)
            return false;

        return player.BetConfirmed
            || player.Bank.ActiveBet > 0
            || player.Hands.Any(h => !h.IsVoided && h.Cards.Count > 0);
    }

    private bool CanAutoRemoveDisconnectedPlayer(PlayerSessionState player)
    {
        if (!IsCurrentRoundPlayer(player))
            return true;

        if (session.Round.Phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands or BlackjackPhase.BettingOpen)
            return player.Bank.ActiveBet <= 0 && player.Hands.Count == 0;

        return false;
    }

    private bool IsCurrentRoundPlayer(PlayerSessionState player)
        => session.Round.Players.Any(p => IsSamePlayerForSync(p.Identity, player.Identity));

    private PlayerSessionState? FindExistingPlayerForPartySync(PlayerIdentity identity)
        => session.SessionPlayers.FirstOrDefault(p => p.Status != PlayerStatus.Dealer && IsSamePlayerForSync(p.Identity, identity))
           ?? session.SessionPlayers.FirstOrDefault(p => p.Status != PlayerStatus.Dealer && IsSameNormalizedName(p.Identity, identity));

    private void MergeLivePartyIdentity(PlayerSessionState existing, PlayerSessionState livePartyPlayer)
    {
        if (string.IsNullOrWhiteSpace(existing.Identity.World) && !string.IsNullOrWhiteSpace(livePartyPlayer.Identity.World))
            existing.Identity = livePartyPlayer.Identity;

        existing.PartySlot = livePartyPlayer.PartySlot;
        if ((existing.Status is PlayerStatus.LeftDisconnected or PlayerStatus.SittingOut) && ShouldRestoreRoundPlayerAsPlaying(existing))
            existing.Status = PlayerStatus.Playing;
        else if (existing.Status == PlayerStatus.LeftDisconnected)
            existing.Status = PlayerStatus.SittingOut;
    }

    private void NormalizeTrackedPlayers()
    {
        foreach (var group in session.SessionPlayers
            .Where(p => p.Status != PlayerStatus.Dealer)
            .GroupBy(p => NormalizeName(p.Identity.Name))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
            .ToList())
        {
            var players = group.ToList();
            var primary = players
                .OrderByDescending(IsCurrentRoundPlayer)
                .ThenByDescending(HasActiveRoundState)
                .ThenByDescending(p => !string.IsNullOrWhiteSpace(p.Identity.World))
                .ThenBy(p => p.PartySlot <= 0 ? int.MaxValue : p.PartySlot)
                .First();

            foreach (var duplicate in players.Where(p => !ReferenceEquals(p, primary)).ToList())
            {
                MergeDuplicateIntoPrimary(primary, duplicate);
                session.Round.Players = session.Round.Players
                    .Select(p => ReferenceEquals(p, duplicate) || IsSameNormalizedName(p.Identity, duplicate.Identity) ? primary : p)
                    .Distinct(ReferenceEqualityComparer<PlayerSessionState>.Instance)
                    .ToList();
                session.SessionPlayers.Remove(duplicate);
                log.Add(LogCategory.Warnings, $"Removed duplicate Blackjack table entry for {duplicate.DisplayName}; kept {primary.DisplayName}.");
            }
        }
    }

    private static List<PlayerSessionState> DeduplicatePartyOrder(List<PlayerSessionState> partyList)
    {
        var result = new List<PlayerSessionState>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in partyList)
        {
            var key = player.Status == PlayerStatus.Dealer ? $"dealer:{NormalizeName(player.Identity.Name)}" : NormalizeName(player.Identity.Name);
            if (string.IsNullOrWhiteSpace(key) || seen.Add(key))
                result.Add(player);
        }

        return result;
    }

    private void MergeDuplicateIntoPrimary(PlayerSessionState primary, PlayerSessionState duplicate)
    {
        if (string.IsNullOrWhiteSpace(primary.Identity.World) && !string.IsNullOrWhiteSpace(duplicate.Identity.World))
            primary.Identity = duplicate.Identity;

        if ((primary.PartySlot <= 0 || duplicate.PartySlot < primary.PartySlot) && duplicate.PartySlot > 0)
            primary.PartySlot = duplicate.PartySlot;

        if (duplicate.Bank.TotalTracked > primary.Bank.TotalTracked)
            primary.Bank = duplicate.Bank;
        else if (primary.Bank.LastTradeAmount <= 0 && duplicate.Bank.LastTradeAmount > 0)
            primary.Bank.LastTradeAmount = duplicate.Bank.LastTradeAmount;

        if (!primary.BetConfirmed && duplicate.BetConfirmed)
            primary.BetConfirmed = true;

        if (primary.Hands.Count == 0 && duplicate.Hands.Count > 0)
            primary.Hands = duplicate.Hands;

        if (duplicate.RoundHistory.Count > 0)
            primary.RoundHistory.AddRange(duplicate.RoundHistory);

        if (StatusPriority(duplicate.Status) > StatusPriority(primary.Status))
            primary.Status = duplicate.Status;
    }

    private bool HasActiveRoundState(PlayerSessionState player)
        => player.BetConfirmed || player.Bank.ActiveBet > 0 || player.Hands.Any(h => !h.IsVoided && h.Cards.Count > 0);

    private static int StatusPriority(PlayerStatus status) => status switch
    {
        PlayerStatus.Playing => 5,
        PlayerStatus.SittingOut => 4,
        PlayerStatus.LeftDisconnected => 3,
        PlayerStatus.CashedOut => 2,
        PlayerStatus.SpectatorStaff => 1,
        _ => 0
    };

    private static bool IsSameNormalizedName(PlayerIdentity a, PlayerIdentity b)
        => !string.IsNullOrWhiteSpace(NormalizeName(a.Name))
           && string.Equals(NormalizeName(a.Name), NormalizeName(b.Name), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var characterName = value.Trim();
        var atIndex = characterName.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
            characterName = characterName[..atIndex];

        Span<char> buffer = stackalloc char[Math.Min(characterName.Length, 64)];
        var count = 0;
        foreach (var ch in characterName)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;
            if (count >= buffer.Length)
                break;
            buffer[count++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..count]);
    }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static ReferenceEqualityComparer<T> Instance { get; } = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private static bool IsSamePlayerForSync(PlayerIdentity a, PlayerIdentity b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(a.World)
            || string.IsNullOrWhiteSpace(b.World)
            || string.Equals(a.World, b.World, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryReserveBet(PlayerSessionState player, long amount, out string reason)
    {
        reason = string.Empty;
        if (session.Round.Phase != BlackjackPhase.BettingOpen) { reason = "Bets can only be confirmed while betting is open."; return false; }
        if (amount < session.Rules.MinimumBet || amount > session.Rules.MaximumBet) { reason = $"Bet must be between {session.Rules.MinimumBet:N0} and {session.Rules.MaximumBet:N0} gil."; return false; }
        if (amount > player.Bank.Available) { reason = "Bet cannot exceed available bank."; return false; }
        player.Bank.Available -= amount;
        player.Bank.ActiveBet += amount;
        player.Bank.LastBet = amount;
        player.BetConfirmed = true;
        player.Status = PlayerStatus.Playing;

        // Betting controls can be clicked more than once before the deal starts.
        // Treat each click as adding to the same confirmed hand bet instead of replacing
        // the hand. Otherwise settlement/export narration would only see the final
        // increment, even though ActiveBet and Available bank were updated correctly.
        var primaryHand = player.Hands.FirstOrDefault(h => h.HandNumber == 1);
        if (primaryHand == null)
        {
            player.Hands.Clear();
            player.Hands.Add(new BlackjackHand { HandNumber = 1, Bet = amount, OriginalBet = amount });
        }
        else
        {
            primaryHand.Bet += amount;
            primaryHand.OriginalBet += amount;
        }

        log.Add(LogCategory.RoundFlow, $"Reserved {amount:N0} gil bet for {player.DisplayName}. Total active bet: {player.Bank.ActiveBet:N0} gil.");
        return true;
    }

    public void AddBankDeposit(PlayerSessionState player, long amount)
    {
        player.Bank.Available += amount;
        player.Bank.LastTradeAmount = amount;
        if (amount > 0 && (player.Status is PlayerStatus.SittingOut or PlayerStatus.CashedOut or PlayerStatus.LeftDisconnected))
            player.Status = PlayerStatus.Playing;
        log.Add(LogCategory.Trades, $"Bank deposit: {player.DisplayName} +{amount:N0} gil.");
    }

    public void VoidActiveHandsForDisconnected(PlayerSessionState player)
    {
        foreach (var hand in player.Hands.Where(h => !h.IsComplete))
        {
            hand.IsVoided = true;
            hand.IsComplete = true;
            player.Bank.Available += hand.Bet;
            player.Bank.ActiveBet = Math.Max(0, player.Bank.ActiveBet - hand.Bet);
            log.Add(LogCategory.Warnings, $"Voided active hand for disconnected player {player.DisplayName}; returned {hand.Bet:N0} gil bet to tracked bank.");
        }
    }
}
