using GambaAssistant.Models.Games;

namespace GambaAssistant.Games.Blackjack;

public sealed class BlackjackModule : IGameModule
{
    public string Name => "Blackjack";
    public IGameSession CreateSession() => new BlackjackSession();
}
