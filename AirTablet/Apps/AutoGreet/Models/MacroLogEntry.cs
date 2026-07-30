
namespace AutoGreet.Models;

public enum MacroLogSeverity
{
    Info,
    Warning,
    Error,
}

[Serializable]
public sealed class MacroLogEntry
{
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public MacroLogSeverity Severity { get; set; } = MacroLogSeverity.Info;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MacroName { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string LineText { get; set; } = string.Empty;
}
