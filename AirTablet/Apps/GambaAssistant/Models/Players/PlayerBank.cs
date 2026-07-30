namespace GambaAssistant.Models.Players;

[Serializable]
public sealed class PlayerBank
{
    public long Available { get; set; }
    public long ActiveBet { get; set; }
    public long TotalTracked => Available + ActiveBet;
    public long LastTradeAmount { get; set; }
    public long LastBet { get; set; }
    public bool HasUnpaidBalance { get; set; }
    public long UnpaidBalance { get; set; }
}
