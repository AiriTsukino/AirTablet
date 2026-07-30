using System.Text;
using Dalamud.Bindings.ImGui;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;
using System.Numerics;

namespace GambaAssistant.UI.Tabs;

public sealed class LogTerminalTab
{
    private readonly LogService log;
    private string visibleLogText = string.Empty;
    private string fullLogText = string.Empty;
    private int lastEntryCount = -1;
    private DateTimeOffset lastTimestamp = DateTimeOffset.MinValue;

    public LogTerminalTab(LogService log) => this.log = log;

    public void Draw()
    {
        RefreshCachedTextIfNeeded();

        UiHelpers.Card("Terminal Controls", () =>
        {
            if (ImGui.Button("Clear Log"))
                log.Clear();

            ImGui.SameLine();
            if (ImGui.Button("Copy Visible"))
                ImGui.SetClipboardText(visibleLogText);

            ImGui.SameLine();
            if (ImGui.Button("Copy All"))
                ImGui.SetClipboardText(fullLogText);

            ImGui.SameLine();
            ImGui.TextDisabled($"Showing latest {Math.Min(log.Entries.Count, 300)} of {log.Entries.Count} entries.");

            ImGui.TextWrapped("Tip: click inside the log box and use Ctrl+A / Ctrl+C, or use the copy buttons above. Dice parser diagnostics now include the raw chat body so copied logs are usually enough for troubleshooting.");
        });

        UiHelpers.Card("Session Log", () =>
        {
            if (log.Entries.Count == 0)
            {
                ImGui.TextDisabled("No log entries yet.");
                return;
            }

            var height = Math.Max(
                AirTablet.UI.TabletAppTheme.Px(220f),
                ImGui.GetContentRegionAvail().Y - AirTablet.UI.TabletAppTheme.Px(10f));
            ImGui.InputTextMultiline("##GambaAssistantCopyableLog", ref visibleLogText, Math.Max(visibleLogText.Length + 1024, 4096), new Vector2(-1, height), ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
        });
    }

    private void RefreshCachedTextIfNeeded()
    {
        var count = log.Entries.Count;
        var last = count > 0 ? log.Entries[^1].Timestamp : DateTimeOffset.MinValue;
        if (count == lastEntryCount && last == lastTimestamp)
            return;

        visibleLogText = BuildLogText(log.Entries.TakeLast(300));
        fullLogText = BuildLogText(log.Entries);
        lastEntryCount = count;
        lastTimestamp = last;
    }

    private static string BuildLogText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
            sb.Append('[').Append(entry.Timestamp.ToString("T")).Append("] [").Append(entry.Category).Append("] ").AppendLine(entry.Message);
        return sb.ToString();
    }
}
