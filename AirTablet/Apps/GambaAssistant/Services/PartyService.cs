using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed class PartyService
{
    private readonly LogService log;
    private string lastPartySignature = string.Empty;

    public int LastLivePartyMemberCount { get; private set; }
    public DateTime LastSyncUtc { get; private set; }
    public bool IsLocalPlayerLoaded => DalamudServices.PlayerState.IsLoaded;

    public PartyService(LogService log) => this.log = log;

    public List<PlayerSessionState> GetPartyTableOrder()
    {
        var result = new List<PlayerSessionState>();
        var dealer = GetLocalDealerIdentity();
        result.Add(new PlayerSessionState { Identity = dealer, PartySlot = 1, Status = PlayerStatus.Dealer });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dealer.ToString() };
        var livePartyCount = 0;

        try
        {
            var slot = 2;
            foreach (var member in DalamudServices.PartyList)
            {
                var name = member.Name.TextValue.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                livePartyCount++;
                var world = member.World.ValueNullable?.Name.ExtractText() ?? string.Empty;
                var identity = new PlayerIdentity(name, world);

                // Dalamud's party list may include the local character. Keep the dealer fixed in slot 1.
                if (IsSameCharacter(identity, dealer) || !seen.Add(identity.ToString()))
                    continue;

                result.Add(new PlayerSessionState
                {
                    Identity = identity,
                    PartySlot = slot++,
                    Status = PlayerStatus.SittingOut
                });
            }
        }
        catch (Exception ex)
        {
            log.Add(LogCategory.Warnings, $"Could not read party list: {ex.Message}");
        }

        LastLivePartyMemberCount = livePartyCount;
        LastSyncUtc = DateTime.UtcNow;
        LogPartyChanges(result);
        return result;
    }

    private static PlayerIdentity GetLocalDealerIdentity()
    {
        if (!DalamudServices.PlayerState.IsLoaded)
            return PlayerIdentity.UnknownDealer();

        var localName = DalamudServices.PlayerState.CharacterName.Trim();
        var localWorld = DalamudServices.PlayerState.HomeWorld.Value.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(localName))
            localName = "Dealer";

        return new PlayerIdentity(localName, localWorld);
    }

    private static bool IsSameCharacter(PlayerIdentity a, PlayerIdentity b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        // If a world is unavailable for either side, name match is the safest local-player duplicate check.
        return string.IsNullOrWhiteSpace(a.World)
            || string.IsNullOrWhiteSpace(b.World)
            || string.Equals(a.World, b.World, StringComparison.OrdinalIgnoreCase);
    }

    private void LogPartyChanges(IReadOnlyCollection<PlayerSessionState> result)
    {
        var signature = string.Join("|", result.OrderBy(p => p.PartySlot).Select(p => $"{p.PartySlot}:{p.DisplayName}:{p.Status}"));
        if (string.Equals(signature, lastPartySignature, StringComparison.Ordinal))
            return;

        lastPartySignature = signature;
        log.Add(LogCategory.Info, $"Party sync detected {Math.Max(result.Count - 1, 0)} player(s) plus dealer.");
    }
}
