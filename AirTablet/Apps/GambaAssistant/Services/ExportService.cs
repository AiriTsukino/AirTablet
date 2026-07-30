using System.Text;
using GambaAssistant.Games.Blackjack;

namespace GambaAssistant.Services;

public sealed class ExportService
{
    private readonly PersistenceService persistence;
    private readonly Configuration config;
    private readonly BlackjackSession session;
    private readonly LogService log;

    public ExportService(PersistenceService persistence, Configuration config, BlackjackSession session, LogService log)
    {
        this.persistence = persistence;
        this.config = config;
        this.session = session;
        this.log = log;
    }

    public string ExportJson()
    {
        var path = Path.Combine(GetExportDirectory(), $"gamba-session-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(session, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        log.Add(LogCategory.Info, $"Exported JSON: {path}");
        return path;
    }

    public string ExportCsv()
    {
        var path = Path.Combine(GetExportDirectory(), $"gamba-rounds-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sb = new StringBuilder();
        sb.AppendLine("round,timestamp,player,dealer_cards,player_cards,bet,total_return,player_delta,actions,outcome,bank_after");

        foreach (var p in session.SessionPlayers)
        {
            foreach (var r in p.RoundHistory)
            {
                sb.Append(r.RoundNumber).Append(',')
                  .Append(EscapeCsv(r.Timestamp.ToString("O"))).Append(',')
                  .Append(EscapeCsv(r.Player)).Append(',')
                  .Append(EscapeCsv(r.DealerCards)).Append(',')
                  .Append(EscapeCsv(r.PlayerCards)).Append(',')
                  .Append(r.Bet).Append(',')
                  .Append(r.TotalReturn).Append(',')
                  .Append(r.PlayerDelta).Append(',')
                  .Append(EscapeCsv(r.Actions)).Append(',')
                  .Append(EscapeCsv(r.Outcome)).Append(',')
                  .Append(r.BankAfter)
                  .AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString());
        log.Add(LogCategory.Info, $"Exported CSV: {path}");
        return path;
    }

    private string GetExportDirectory()
    {
        var configured = config.General.ExportDirectory?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return Environment.ExpandEnvironmentVariables(configured);

        return Path.Combine(persistence.ConfigRoot, "Exports");
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
