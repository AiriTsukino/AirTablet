namespace GambaAssistant.Services;

public enum LogCategory { Info, Warnings, Errors, Trades, Dice, ChatQueue, RoundFlow, Settlement, Undo, Debug, Demo }
public sealed record LogEntry(DateTimeOffset Timestamp, LogCategory Category, string Message);

public sealed class LogService
{
    public List<LogEntry> Entries { get; } = [];
    public void Add(LogCategory category, string message)
    {
        Entries.Add(new LogEntry(DateTimeOffset.Now, category, message));
        if (Entries.Count > 2000) Entries.RemoveRange(0, Entries.Count - 2000);
    }
    public void Clear() => Entries.Clear();
}
