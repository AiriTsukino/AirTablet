using System.Numerics;
using System.Text;
using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class LogTab
{
    private readonly DiagnosticLogService logs;

    public LogTab(DiagnosticLogService logs)
    {
        this.logs = logs;
    }

    public void Draw()
    {
        UiHelpers.Section("Log");
        UiHelpers.TextDisabledWrapped("Macro syntax errors, queue messages, and diagnostic entries appear here. The console box is selectable so users can copy text for support or troubleshooting.");

        var consoleText = BuildConsoleText();

        if (ImGui.Button("Clear log"))
        {
            logs.Clear();
            consoleText = BuildConsoleText();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy log"))
            ImGui.SetClipboardText(consoleText);

        ImGui.SameLine();
        if (ImGui.Button("Copy supported syntax"))
            ImGui.SetClipboardText(DiagnosticLogService.FullSupportedSyntaxText);

        ImGui.Spacing();
        UiHelpers.TextDisabledWrapped("Full macro syntax and supported emote commands are available in Settings > Help.");
        ImGui.Separator();

        var height = Math.Max(
            AirTablet.UI.TabletAppTheme.Px(260f),
            ImGui.GetContentRegionAvail().Y - AirTablet.UI.TabletAppTheme.Px(8f));
        var consoleBuffer = CreateReadOnlyBuffer(consoleText);
        ImGui.InputTextMultiline("##autogreet-log-console", consoleBuffer.AsSpan(), new Vector2(-1f, height), ImGuiInputTextFlags.ReadOnly);
    }

    private static byte[] CreateReadOnlyBuffer(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[bytes.Length + 1];
        bytes.CopyTo(buffer, 0);
        return buffer;
    }

    private string BuildConsoleText()
    {
        if (logs.Entries.Count == 0)
            return "No log entries yet.";

        return string.Join("\n\n", logs.Entries.Reverse().Select(FormatEntry));
    }

    private static string FormatEntry(MacroLogEntry entry)
    {
        var text = $"[{entry.CreatedUtc.LocalDateTime:g}] [{entry.Severity}] {entry.Title}";

        if (!string.IsNullOrWhiteSpace(entry.MacroName))
            text += $"\nMacro: {entry.MacroName}";

        if (entry.LineNumber > 0)
            text += $"\nLine {entry.LineNumber}: {entry.LineText}";

        if (!string.IsNullOrWhiteSpace(entry.Message))
            text += $"\n{entry.Message}";

        return text;
    }
}
