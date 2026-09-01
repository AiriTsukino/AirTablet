using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace WardrobeManager;

internal sealed unsafe class SelfieCameraService : IDisposable
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly PortraitTextureCache textures;
    private readonly Action<string> notify;
    private readonly Action returnToApp;
    private WardrobePreset? preset;
    private string status = "Frame your character inside the 9:16 guide.";
    private bool captureArmed;
    private bool hidGameUi;
    private DateTime captureAt;
    private DateTime overlayHiddenUntil;
    private NormalizedCrop requestedCrop;
    private Vector2 guideOffset;
    private bool guideSizeDirty;
    private string pendingCapturedSelfie = string.Empty;

    public SelfieCameraService(Configuration config, PersistenceService persistence, PortraitTextureCache textures, Action<string> notify, Action returnToApp)
    {
        this.config = config;
        this.persistence = persistence;
        this.textures = textures;
        this.notify = notify;
        this.returnToApp = returnToApp;
        DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
    }

    public void Open(WardrobePreset target)
    {
        preset = target;
        captureArmed = false;
        hidGameUi = false;
        guideOffset = Vector2.Zero;
        pendingCapturedSelfie = string.Empty;
        status = "Frame your character inside the 9:16 guide.";
        DalamudServices.CommandManager.ProcessCommand("/gpose");
        DalamudServices.CommandManager.ProcessCommand("/airtablet");
    }

    private void Draw()
    {
        if (preset is null) return;
        if (captureArmed && DateTime.UtcNow >= captureAt) CaptureNow();
        if (preset is null || DateTime.UtcNow < overlayHiddenUntil) return;
        var crop = DrawGuide();

        var cameraWidth = MathF.Min(TabletAppTheme.Px(460f), ImGui.GetMainViewport().WorkSize.X * 0.42f);
        // A zero height asks ImGui to auto-fit that axis every frame. Keeping the width
        // explicit prevents wrapped copy from turning the controls into a very thin,
        // excessively tall window.
        ImGui.SetNextWindowSize(new Vector2(cameraWidth, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().WorkPos + new Vector2(24f, 70f), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.075f, 0.06f, 0.12f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, TabletAppTheme.Accent);
        var open = true;
        if (ImGui.Begin("Wardrobe Selfie Camera###WardrobeSelfieCamera", ref open,
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            DrawWrapped(TabletAppTheme.AccentHover, preset.Name);
            DrawWrapped(TabletAppTheme.MutedText, "Frame the live game view inside the guide. Use Move Guide to reposition it and Resize to change its size. The portrait crop remains locked to 9:16. WardrobeManager hides the game UI and camera controls before capturing the rendered frame.");
            ImGui.Separator();
            DrawWrapped(captureArmed ? TabletAppTheme.AccentHover : TabletAppTheme.MutedText, status);
            if (!string.IsNullOrWhiteSpace(pendingCapturedSelfie))
            {
                DrawWrapped(TabletAppTheme.MutedText, "Replace the existing portrait? Confirming deletes its managed copy and assigns the newly captured selfie.");
                if (ImGui.Button("Replace Existing Selfie", new Vector2(-1f, 34f))) AssignCapturedSelfie(pendingCapturedSelfie);
                if (ImGui.Button("Keep Existing and Return", new Vector2(-1f, 0f)))
                {
                    notify("Existing WardrobeManager portrait kept. The new capture remains in the selfie folder.");
                    CloseAndReturn();
                }
            }
            else
            {
                var disabled = captureArmed;
                if (disabled) ImGui.BeginDisabled();
                if (ImGui.Button("Take Selfie", new Vector2(-1f, 34f))) RequestCapture(crop);
                if (disabled) ImGui.EndDisabled();
                if (ImGui.Button("Cancel and Return", new Vector2(-1f, 0f))) CloseAndReturn();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        if (!open) CloseAndReturn();
    }

    private NormalizedCrop DrawGuide()
    {
        var viewport = ImGui.GetMainViewport();
        var maximumHeight = MathF.Min(viewport.WorkSize.Y * 0.92f, viewport.WorkSize.X * 0.90f * 16f / 9f);
        var minimumHeight = MathF.Min(viewport.WorkSize.Y * 0.28f, maximumHeight);
        var height = Math.Clamp(viewport.WorkSize.Y * config.SelfieGuideHeightRatio, minimumHeight, maximumHeight);
        var width = height * 9f / 16f;
        var centered = viewport.WorkPos + (viewport.WorkSize - new Vector2(width, height)) * 0.5f;
        var min = centered + guideOffset;
        min.X = Math.Clamp(min.X, viewport.WorkPos.X, viewport.WorkPos.X + viewport.WorkSize.X - width);
        min.Y = Math.Clamp(min.Y, viewport.WorkPos.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - height);
        guideOffset = min - centered;
        var max = min + new Vector2(width, height);
        var draw = ImGui.GetForegroundDrawList();
        var accent = ImGui.ColorConvertFloat4ToU32(TabletAppTheme.AccentHover);
        var faint = ImGui.ColorConvertFloat4ToU32(new Vector4(TabletAppTheme.AccentHover.X, TabletAppTheme.AccentHover.Y, TabletAppTheme.AccentHover.Z, 0.42f));
        draw.AddRect(min, max, accent, 8f, ImDrawFlags.None, 3f);
        draw.AddLine(new Vector2(min.X + width / 3f, min.Y), new Vector2(min.X + width / 3f, max.Y), faint, 1f);
        draw.AddLine(new Vector2(min.X + width * 2f / 3f, min.Y), new Vector2(min.X + width * 2f / 3f, max.Y), faint, 1f);
        draw.AddLine(new Vector2(min.X, min.Y + height / 3f), new Vector2(max.X, min.Y + height / 3f), faint, 1f);
        draw.AddLine(new Vector2(min.X, min.Y + height * 2f / 3f), new Vector2(max.X, min.Y + height * 2f / 3f), faint, 1f);
        draw.AddText(min + new Vector2(8f, 8f), accent, "9:16 portrait");
        DrawGuideGrabPin(min, width);
        DrawGuideResizePin(min, width, height, minimumHeight, maximumHeight);
        return new NormalizedCrop((min.X - viewport.Pos.X) / viewport.Size.X, (min.Y - viewport.Pos.Y) / viewport.Size.Y, width / viewport.Size.X, height / viewport.Size.Y);
    }

    private void DrawGuideGrabPin(Vector2 guideMin, float guideWidth)
    {
        const float pinWidth = 104f;
        const float pinHeight = 30f;
        var pinPosition = new Vector2(guideMin.X + (guideWidth - pinWidth) * 0.5f, guideMin.Y + 8f);
        ImGui.SetNextWindowPos(pinPosition);
        ImGui.SetNextWindowSize(new Vector2(pinWidth, pinHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        if (ImGui.Begin("###WardrobePortraitGuidePin", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.PushStyleColor(ImGuiCol.Button, TabletAppTheme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TabletAppTheme.AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, TabletAppTheme.AccentHover);
            ImGui.Button("Move Guide", new Vector2(pinWidth, pinHeight));
            if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left)) guideOffset += ImGui.GetIO().MouseDelta;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Grab and drag to reposition the 9:16 portrait guide.");
            ImGui.PopStyleColor(3);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void DrawGuideResizePin(Vector2 guideMin, float guideWidth, float guideHeight, float minimumHeight, float maximumHeight)
    {
        const float pinWidth = 84f;
        const float pinHeight = 30f;
        var pinPosition = guideMin + new Vector2(guideWidth - pinWidth - 8f, guideHeight - pinHeight - 8f);
        ImGui.SetNextWindowPos(pinPosition);
        ImGui.SetNextWindowSize(new Vector2(pinWidth, pinHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        if (ImGui.Begin("###WardrobePortraitGuideResizePin", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.PushStyleColor(ImGuiCol.Button, TabletAppTheme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TabletAppTheme.AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, TabletAppTheme.AccentHover);
            ImGui.Button("Resize", new Vector2(pinWidth, pinHeight));
            if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var resizedHeight = Math.Clamp(guideHeight + ImGui.GetIO().MouseDelta.Y, minimumHeight, maximumHeight);
                config.SelfieGuideHeightRatio = resizedHeight / ImGui.GetMainViewport().WorkSize.Y;
                guideSizeDirty = true;
            }
            if (guideSizeDirty && ImGui.IsItemDeactivated())
            {
                DalamudServices.PluginInterface.SavePluginConfig(config);
                guideSizeDirty = false;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drag to resize. The portrait guide remains locked to 9:16.");
            ImGui.PopStyleColor(3);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void RequestCapture(NormalizedCrop crop)
    {
        requestedCrop = crop;
        hidGameUi = !DalamudServices.GameGui.GameUiHidden;
        if (hidGameUi) SetGameUiVisible(false);
        captureAt = DateTime.UtcNow.AddMilliseconds(350);
        overlayHiddenUntil = DateTime.UtcNow.AddMilliseconds(900);
        captureArmed = true;
        status = "Preparing a clean game frame…";
    }

    private void CaptureNow()
    {
        captureArmed = false;
        if (preset is null) { RestoreGameUi(); return; }
        var fileIdentity = $"{preset.Id:N}-{preset.Name}";
        if (!DirectGameCapture.TryCapture(requestedCrop, ResolveOutputFolder(), fileIdentity, out var captured, out var error))
        {
            RestoreGameUi();
            status = "The selfie could not be captured: " + error;
            return;
        }

        RestoreGameUi();
        if (!string.IsNullOrWhiteSpace(preset.ImagePath) && File.Exists(preset.ImagePath))
        {
            pendingCapturedSelfie = captured;
            status = "Selfie captured. Confirm whether to replace the existing portrait.";
            return;
        }
        AssignCapturedSelfie(captured);
    }

    private void AssignCapturedSelfie(string captured)
    {
        if (preset is null) return;
        try
        {
            var old = preset.ImagePath;
            preset.ImagePath = persistence.ImportImage(captured, preset.Id);
            textures.Invalidate(old);
            persistence.Save();
            pendingCapturedSelfie = string.Empty;
            notify($"Selfie saved for {preset.Name}.");
            CloseAndReturn();
        }
        catch (Exception ex) { status = "The selfie was captured but could not be assigned: " + ex.Message; }
    }

    private string ResolveOutputFolder() => !string.IsNullOrWhiteSpace(config.SelfieFolder)
        ? config.SelfieFolder
        : Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, "Wardrobe Selfies");

    private void CloseAndReturn()
    {
        RestoreGameUi();
        preset = null;
        captureArmed = false;
        pendingCapturedSelfie = string.Empty;
        returnToApp();
    }

    private static void SetGameUiVisible(bool visible)
    {
        try
        {
            var module = UIModule.Instance();
            if (module is null) return;
            const UiFlags all = UiFlags.Shortcuts | UiFlags.Hud | UiFlags.Nameplates | UiFlags.Chat | UiFlags.ActionBars | UiFlags.TargetInfo;
            module->ToggleUi(all, visible, false);
        }
        catch (Exception ex) { DalamudServices.Log.Warning(ex, "WardrobeManager could not change game UI visibility."); }
    }

    private void RestoreGameUi()
    {
        if (!hidGameUi) return;
        hidGameUi = false;
        SetGameUiVisible(true);
    }

    private static void DrawWrapped(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    public void Dispose()
    {
        DalamudServices.PluginInterface.UiBuilder.Draw -= Draw;
        RestoreGameUi();
        preset = null;
    }
}
