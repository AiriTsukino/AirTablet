
using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class DiagnosticLogService
{
    private const int MaxEntries = 200;
    private readonly List<MacroLogEntry> entries = [];

    public event Action? LogAdded;

    public IReadOnlyList<MacroLogEntry> Entries => entries;

    public static string SupportedSyntaxText =>
        "Supported macro syntax:\n" +
        "  /tell <t> message\n" +
        "  /tell <playername> message\n" +
        "  /t <t> message\n" +
        "  /t <playername> message\n" +
        "  /say message or /s message\n" +
        "  /shout message or /sh message\n" +
        "  /yell message or /y message\n" +
        "  Use <playername> in non-tell channels to mention a visitor by Name@World. Example: /say Welcome <playername>!\n" +
        "  <playername> is replaced with Name@World.\n" +
        "  Supported FFXIV emote commands, such as /dote <t>, /hug <t>, /wave, /hum, or /beesknees\n" +
        "  Targeted emotes are queued until the visitor is targetable when that setting is enabled.\n" +
        "  /wait 1, /wait.1, /wait1\n" +
        "  Inline waits such as <wait.1> or <wait.02> at the end of a supported line\n\n" +
        "If you need another command syntax supported, request a syntax update before using it in active greets.";

    public static string FullSupportedSyntaxText =>
        SupportedSyntaxText + "\n\nSupported emote commands:\n" + EmoteCommandRegistry.SupportedCommandsText;

    public void Info(string title, string message)
        => Add(new MacroLogEntry { Severity = MacroLogSeverity.Info, Title = title, Message = message });

    public void Warning(string title, string message)
        => Add(new MacroLogEntry { Severity = MacroLogSeverity.Warning, Title = title, Message = message });

    public void MacroSyntaxError(string macroName, int lineNumber, string lineText, string message)
    {
        Add(new MacroLogEntry
        {
            Severity = MacroLogSeverity.Error,
            Title = "Macro syntax not recognized",
            Message = message,
            MacroName = macroName,
            LineNumber = lineNumber,
            LineText = lineText,
        });
    }

    public void Clear()
    {
        entries.Clear();
        LogAdded?.Invoke();
    }

    private void Add(MacroLogEntry entry)
    {
        entries.Insert(0, entry);
        if (entries.Count > MaxEntries)
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

        LogAdded?.Invoke();
    }
}
