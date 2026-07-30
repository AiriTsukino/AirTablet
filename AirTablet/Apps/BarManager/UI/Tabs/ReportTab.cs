using System.Numerics;
using BarManager.Services;
using BarManager.UI.Components;
using Dalamud.Bindings.ImGui;

namespace BarManager.UI.Tabs;

internal sealed class ReportTab
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private string auditReportText = string.Empty;
    private string gambaReportText = string.Empty;
    private string lastSavedPath = string.Empty;

    public ReportTab(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
    }

    public void Draw()
    {
        if (!ImGui.BeginChild("##ReportScroll", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0)), false))
        {
            ImGui.EndChild();
            return;
        }

        UiHelpers.SectionTitle("Nightly Audit Report");
        if (ImGui.Button("Generate / Refresh Audit"))
            auditReportText = ReportService.BuildNightlyReport(config);
        ImGui.SameLine();
        if (ImGui.Button("Copy Audit"))
        {
            if (string.IsNullOrWhiteSpace(auditReportText))
                auditReportText = ReportService.BuildNightlyReport(config);
            ImGui.SetClipboardText(auditReportText);
        }
        ImGui.SameLine();
        if (ImGui.Button("Save Audit Report"))
        {
            if (string.IsNullOrWhiteSpace(auditReportText))
                auditReportText = ReportService.BuildNightlyReport(config);

            var defaultPath = persistence.GetDefaultAuditReportPath();
            var path = FileDialogService.PickTextToSave(Path.GetDirectoryName(defaultPath) ?? persistence.AuditReportRoot, Path.GetFileName(defaultPath), "Save audit report");
            if (!string.IsNullOrWhiteSpace(path))
                lastSavedPath = persistence.SaveAuditReport(auditReportText, path);
        }

        UiHelpers.TextWrappedMuted("Save Audit Report opens a file picker so you can choose the exact report location for this night.");
        UiHelpers.TextWrappedMuted($"Default audit report folder: {persistence.AuditReportRoot}");
        if (string.IsNullOrWhiteSpace(auditReportText))
            auditReportText = ReportService.BuildNightlyReport(config);
        ImGui.InputTextMultiline("##auditReport", ref auditReportText, 65535, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, 250f)), ImGuiInputTextFlags.ReadOnly);

        UiHelpers.SectionTitle("Gamba Report");
        UiHelpers.TextWrappedMuted("Kept separate from the main audit report so large gamba nights do not make the audit report massive.");
        if (ImGui.Button("Generate / Refresh Gamba"))
            gambaReportText = ReportService.BuildGambaReport(config);
        ImGui.SameLine();
        if (ImGui.Button("Copy Gamba"))
        {
            if (string.IsNullOrWhiteSpace(gambaReportText))
                gambaReportText = ReportService.BuildGambaReport(config);
            ImGui.SetClipboardText(gambaReportText);
        }
        ImGui.SameLine();
        if (ImGui.Button("Save Gamba Report"))
        {
            if (string.IsNullOrWhiteSpace(gambaReportText))
                gambaReportText = ReportService.BuildGambaReport(config);

            var defaultPath = persistence.GetDefaultGambaReportPath();
            var path = FileDialogService.PickTextToSave(Path.GetDirectoryName(defaultPath) ?? persistence.GambaReportRoot, Path.GetFileName(defaultPath), "Save gamba report");
            if (!string.IsNullOrWhiteSpace(path))
                lastSavedPath = persistence.SaveGambaReport(gambaReportText, path);
        }

        if (!string.IsNullOrWhiteSpace(lastSavedPath))
            UiHelpers.TextWrappedMuted($"Saved: {lastSavedPath}");

        if (string.IsNullOrWhiteSpace(gambaReportText))
            gambaReportText = ReportService.BuildGambaReport(config);
        ImGui.InputTextMultiline(
            "##gambaReport",
            ref gambaReportText,
            65535,
            new Vector2(-1, MathF.Max(AirTablet.UI.TabletAppTheme.Px(260f), ImGui.GetContentRegionAvail().Y)),
            ImGuiInputTextFlags.ReadOnly);
        ImGui.EndChild();
    }
}
