namespace GambaAssistant.Games.Blackjack;

public enum BlackjackRank { Ace = 1, Two, Three, Four, Five, Six, Seven, Eight, Nine, Ten, Jack, Queen, King }

[Serializable]
public readonly record struct BlackjackCard(BlackjackRank Rank)
{
    public int HardValue => Rank switch { BlackjackRank.Ace => 1, BlackjackRank.Jack or BlackjackRank.Queen or BlackjackRank.King => 10, _ => (int)Rank };
    public bool IsAce => Rank == BlackjackRank.Ace;
    public bool IsTenValue => HardValue == 10;
    public override string ToString() => Rank switch { BlackjackRank.Ace => "A", BlackjackRank.Jack => "J", BlackjackRank.Queen => "Q", BlackjackRank.King => "K", _ => HardValue.ToString() };
    public static BlackjackCard FromDice(int value)
    {
        if (value < 1 || value > 13) throw new ArgumentOutOfRangeException(nameof(value), "Blackjack dice rolls must be 1-13.");
        return new BlackjackCard((BlackjackRank)value);
    }
}
