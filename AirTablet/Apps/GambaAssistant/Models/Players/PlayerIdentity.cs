namespace GambaAssistant.Models.Players;

[Serializable]
public readonly record struct PlayerIdentity(string Name, string World)
{
    public string Display => string.IsNullOrWhiteSpace(World) ? Name : $"{Name}@{World}";
    public override string ToString() => Display;
    public static PlayerIdentity UnknownDealer() => new("Dealer", string.Empty);
}
