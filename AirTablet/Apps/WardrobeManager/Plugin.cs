using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace WardrobeManager;

internal sealed class Plugin : IDisposable
{
    private enum HonorificSavePhase { None, WaitingForUnload, WaitingForReload }
    private const string WardrobeManagerVersion = "1.0.53.0";
    private const string DevelopmentWarningModal = "WardrobeManager is in development##WardrobeManager";
    private const string ManualHonorificModal = "New Honorific Title";
    private static readonly string[] HonorificEffectPalettes =
    [
        "Default Glow", "Two Colour Gradient", "Pride Rainbow", "Transgender", "Lesbian", "Bisexual",
        "Black & White", "Black & Red", "Black & Blue", "Black & Yellow", "Black & Green", "Black & Pink",
        "Black & Cyan", "Cherry Blossom", "Golden", "Pastel Rainbow", "Dark Rainbow", "Non-binary",
    ];
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly IntegrationService integrations;
    private readonly NativeImageDialog imageDialog = new();
    private readonly PortraitTextureCache textures = new();
    private readonly SelfieCameraService selfieCamera;
    private List<AvailableMod> availableMods = [];
    private WardrobePresetType activeType;
    private WardrobePreset? editing;
    private WardrobePreset? editingSnapshot;
    private WardrobePreset? pendingApply;
    private WardrobePreset? pendingDelete;
    private WardrobePreset? pendingDesignSync;
    private WardrobeFolder? pendingFolderRemoval;
    private string modSearch = string.Empty;
    private string notification = string.Empty;
    private bool foregroundRequested;
    private bool settingsVisible;
    private bool foldersVisible;
    private Guid activeFolderId;
    private string newFolderName = string.Empty;
    private bool imageCleanupRequested;
    private DateTime nextAutomaticGlamourerScan;
    private readonly Dictionary<Guid, int> missingGlamourerDesignScans = [];
    private bool outfitDirty;
    private bool closeEditorAfterSync;
    private Guid quickSelectionId;
    private List<GlamourerQuickDesign> quickDesigns = [];
    private DateTime nextQuickDesignRefresh;
    private DateTime lastLibraryDraw;
    private bool quickSelectionInitialized;
    private bool legacyMarkerCleanupCompleted;
    private DateTime nextLegacyMarkerCleanup;
    private bool glamourerEnablePending;
    private DateTime glamourerEnableAt;
    private bool developmentWarningOpened;
    private List<CustomizePlusProfile> customizeProfiles = [];
    private List<HonorificTitle> honorificTitles = [];
    private DateTime nextCharacterIntegrationRefresh;
    private WardrobePreset? pendingHonorificSave;
    private HonorificSavePhase honorificSavePhase;
    private DateTime honorificUnloadedSince;
    private DateTime honorificReadySince;
    private DateTime honorificConfigWriteBeforeUnload;
    private WardrobePreset? manualHonorificPreset;
    private ManualHonorificDraft? manualHonorificDraft;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        persistence = new PersistenceService();
        integrations = new IntegrationService();
        selfieCamera = new SelfieCameraService(
            config,
            persistence,
            textures,
            message => notification = message,
            () => foregroundRequested = true);
        config.SelfieGuideHeightRatio = Math.Clamp(config.SelfieGuideHeightRatio, 0.28f, 0.92f);
        if (config.Version < 10)
        {
            config.Version = 10;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        RefreshMods();
    }

    public void Tick()
    {
        ProcessHonorificSave();
        if (glamourerEnablePending && DateTime.UtcNow >= glamourerEnableAt)
        {
            glamourerEnablePending = false;
            DalamudServices.CommandManager.ProcessCommand("/xlenableplugin \"Glamourer\"");
            notification = "Glamourer was re-enabled after the folder update.";
        }
        if (legacyMarkerCleanupCompleted || DateTime.UtcNow < nextLegacyMarkerCleanup) return;
        nextLegacyMarkerCleanup = DateTime.UtcNow.AddSeconds(2);
        if (!integrations.RequirementState.GlamourerConnected) return;

        var retainedFolders = persistence.Data.Folders
            .Select(folder => string.IsNullOrWhiteSpace(folder.GlamourerPath) ? folder.Name : folder.GlamourerPath)
            .ToList();
        var cleanedMarkers = integrations.CleanupLegacyFolderMarkers(retainedFolders, out var markerCleanupError);
        legacyMarkerCleanupCompleted = string.IsNullOrWhiteSpace(markerCleanupError);
        if (cleanedMarkers > 0)
            notification = $"Removed {cleanedMarkers} obsolete WardrobeManager Glamourer folder marker{(cleanedMarkers == 1 ? string.Empty : "s")}. Reload Glamourer once more to refresh its folder list.";
        else if (!string.IsNullOrWhiteSpace(markerCleanupError))
            DalamudServices.Log.Debug("WardrobeManager legacy folder marker cleanup will retry: {Reason}", markerCleanupError);
    }

    public void Draw()
    {
        imageDialog.Pump();
        TryAutomaticGlamourerImport();
        if (settingsVisible) DrawSettings();
        else if (editing is not null)
        {
            // Isolate the fixed editor viewport from AirTablet's scrollable module host.
            // Only the mod-layers pane owns scrolling; the preset page itself never does.
            var visible = ImGui.BeginChild("##wardrobe-preset-editor-viewport", Vector2.Zero, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings);
            if (visible) DrawEditor();
            ImGui.EndChild();
        }
        else DrawLibrary();
        DrawApplyConfirmation();
        DrawDesignSyncConfirmation();
        DrawDeleteConfirmation();
        DrawCreateFolderModal();
        DrawFolderRemovalConfirmation();
        DrawImageCleanupConfirmation();
        DrawManualHonorificModal();
        DrawDevelopmentWarning();
    }

    public bool CanNavigateBack() => settingsVisible || editing is not null || activeFolderId != Guid.Empty;
    public bool NavigateBack()
    {
        if (settingsVisible) { settingsVisible = false; return true; }
        if (editing is not null)
        {
            if ((editing.Type is WardrobePresetType.Outfit or WardrobePresetType.Character) && outfitDirty)
                RequestDesignSync(editing, true);
            else CloseEditor();
            return true;
        }
        if (activeFolderId != Guid.Empty) { activeFolderId = Guid.Empty; return true; }
        return false;
    }
    public bool ConsumeForegroundRequest() { if (!foregroundRequested) return false; foregroundRequested = false; return true; }
    public string? ConsumeNotification() { if (string.IsNullOrWhiteSpace(notification)) return null; var value = notification; notification = string.Empty; return value; }

    private void DrawLibrary()
    {
        var now = DateTime.UtcNow;
        var synchronizeQuickSelection = !quickSelectionInitialized
            || now - lastLibraryDraw > TimeSpan.FromMilliseconds(500);
        lastLibraryDraw = now;
        if (ImGui.BeginTable("##wardrobe-header", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Tabs", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Folder", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(115f));
            ImGui.TableSetupColumn("New", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(125f));
            ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(90f));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            foreach (var type in Enum.GetValues<WardrobePresetType>())
            {
                if (type != WardrobePresetType.Outfit) ImGui.SameLine(0f, TabletAppTheme.Px(8f));
                DrawTypeTab(type);
            }
            ImGui.SameLine(0f, TabletAppTheme.Px(8f));
            DrawFolderTab();
            ImGui.TableNextColumn();
            if (foldersVisible && ImGui.Button("New Folder", new Vector2(-1f, 0f)))
            {
                newFolderName = "New Folder";
                TabletAppTheme.OpenCenteredModal("Create outfit folder");
            }
            ImGui.TableNextColumn();
            if ((!foldersVisible || activeFolderId != Guid.Empty) && ImGui.Button(foldersVisible ? "New Outfit" : "New Preset", new Vector2(-1f, 0f)))
                CreatePreset(foldersVisible ? WardrobePresetType.Outfit : activeType);
            ImGui.TableNextColumn();
            if (ImGui.Button("Settings", new Vector2(-1f, 0f))) settingsVisible = true;
            ImGui.EndTable();
        }
        DrawConnectionAndQuickRow(synchronizeQuickSelection);
        ImGui.Separator();

        if (foldersVisible)
        {
            if (activeFolderId != Guid.Empty)
            {
                var folder = persistence.Data.Folders.FirstOrDefault(item => item.Id == activeFolderId);
                if (folder is null) activeFolderId = Guid.Empty;
                else
                {
                    if (ImGui.Button("Back to All Outfits", TabletAppTheme.Px(new Vector2(170f, 0f)))) activeFolderId = Guid.Empty;
                    ImGui.SameLine();
                    ImGui.TextColored(TabletAppTheme.AccentHover, folder.Name);
                    ImGui.Separator();
                }
            }
            if (activeFolderId == Guid.Empty)
            {
                DrawOutfitFolders();
                return;
            }
        }

        var presets = persistence.Data.Presets
            .Where(x => x.Type == (foldersVisible ? WardrobePresetType.Outfit : activeType))
            .Where(x => !foldersVisible || x.FolderId == activeFolderId)
            .OrderByDescending(x => x.IsFavorite)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (presets.Count == 0)
        {
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 35f)));
            var emptyMessage = foldersVisible
                ? "This folder has no outfit presets. Create one here or assign an existing outfit to this folder from its editor."
                : $"No {TypeLabel(activeType).ToLowerInvariant()} presets yet. Create one to capture an appearance and configure it.";
            TextColoredWrapped(TabletAppTheme.MutedText, emptyMessage);
            return;
        }

        var available = ImGui.GetContentRegionAvail().X;
        var cardWidth = TabletAppTheme.Px(220f);
        var columns = Math.Max(1, (int)((available + TabletAppTheme.Px(10f)) / (cardWidth + TabletAppTheme.Px(10f))));
        if (!ImGui.BeginTable("##wardrobe-grid", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings)) return;
        foreach (var preset in presets)
        {
            ImGui.TableNextColumn();
            DrawPresetCard(preset);
        }
        ImGui.EndTable();
    }

    private void DrawOutfitFolders()
    {
        var folders = persistence.Data.Folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0) return;
        var available = ImGui.GetContentRegionAvail().X;
        var width = TabletAppTheme.Px(220f);
        var columns = Math.Max(1, (int)((available + TabletAppTheme.Px(10f)) / (width + TabletAppTheme.Px(10f))));
        if (!ImGui.BeginTable("##wardrobe-folder-grid", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings)) return;
        foreach (var folder in folders)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(folder.Id.ToString());
            ImGui.PushStyleColor(ImGuiCol.ChildBg, TabletAppTheme.SurfaceRaised);
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.48f));
            if (ImGui.BeginChild("##folder", new Vector2(-1f, TabletAppTheme.Px(135f)), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.TextColored(TabletAppTheme.AccentHover, "Folder");
                ImGui.TextWrapped(folder.Name);
                var count = persistence.Data.Presets.Count(preset => preset.Type == WardrobePresetType.Outfit && preset.FolderId == folder.Id);
                TextColoredWrapped(TabletAppTheme.MutedText, $"{count} outfit{(count == 1 ? string.Empty : "s")}");
                var buttonWidth = MathF.Max(TabletAppTheme.Px(62f), (ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(6f)) / 2f);
                if (ImGui.Button("Open", new Vector2(buttonWidth, 0f))) activeFolderId = folder.Id;
                ImGui.SameLine();
                if (ImGui.Button("Remove", new Vector2(buttonWidth, 0f)))
                {
                    pendingFolderRemoval = folder;
                    TabletAppTheme.OpenCenteredModal("Remove WardrobeManager folder?");
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            ImGui.PopID();
        }
        ImGui.EndTable();
        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawPresetCard(WardrobePreset preset)
    {
        if (preset.Type == WardrobePresetType.Emote)
        {
            DrawEmotePresetCard(preset);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.ChildBg, TabletAppTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.48f));
        if (ImGui.BeginChild($"##wardrobe-card-{preset.Id}", new Vector2(-1f, TabletAppTheme.Px(430f)), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var portrait = textures.Get(preset.ImagePath);
            var width = ImGui.GetContentRegionAvail().X;
            var portraitWidth = MathF.Min(width, TabletAppTheme.Px(174f));
            var portraitSize = new Vector2(portraitWidth, portraitWidth * 16f / 9f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (width - portraitWidth) * 0.5f));
            var portraitPosition = ImGui.GetCursorScreenPos();
            if (portrait is not null) DrawPortrait(portrait, portraitSize); else
            {
                var position = ImGui.GetCursorScreenPos();
                ImGui.Dummy(portraitSize);
                ImGui.GetWindowDrawList().AddRectFilled(position, position + portraitSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.10f, 0.18f, 1f)), TabletAppTheme.Px(5f));
                var label = preset.Type == WardrobePresetType.Emote ? "Emote" : "Portrait";
                var textSize = ImGui.CalcTextSize(label);
                ImGui.GetWindowDrawList().AddText(position + (portraitSize - textSize) * 0.5f, ImGui.ColorConvertFloat4ToU32(TabletAppTheme.MutedText), label);
            }
            ImGui.TextWrapped(preset.Name);
            TextColoredWrapped(TabletAppTheme.MutedText, preset.Type == WardrobePresetType.Outfit
                ? $"{preset.Mods.Count} Glamourer mod association{(preset.Mods.Count == 1 ? string.Empty : "s")}"
                : $"{preset.Mods.Count(x => x.Enabled)} mod layer(s)");
            var buttonWidth = MathF.Max(TabletAppTheme.Px(62f), (ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(6f)) / 2f);
            if (ImGui.Button($"Apply##{preset.Id}", new Vector2(buttonWidth, 0f))) RequestApply(preset);
            ImGui.SameLine();
            if (ImGui.Button($"Edit##{preset.Id}", new Vector2(buttonWidth, 0f))) BeginEdit(preset);
            DrawFavoriteStar(preset, portraitPosition + TabletAppTheme.Px(new Vector2(5f, 5f)));
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private void DrawEmotePresetCard(WardrobePreset preset)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TabletAppTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.48f));
        if (ImGui.BeginChild($"##wardrobe-emote-card-{preset.Id}", new Vector2(-1f, TabletAppTheme.Px(240f)), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var titlePosition = ImGui.GetCursorScreenPos();
            var titleWidth = ImGui.GetContentRegionAvail().X;
            var favoriteSize = GetFavoriteStarSize(preset);
            var favoritePosition = new Vector2(
                titlePosition.X + MathF.Max(0f, titleWidth - favoriteSize.X),
                titlePosition.Y);
            var availableTitleWidth = MathF.Max(
                TabletAppTheme.Px(24f),
                titleWidth - favoriteSize.X - ImGui.GetStyle().ItemSpacing.X);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availableTitleWidth);
            ImGui.TextWrapped(preset.Name);
            ImGui.PopTextWrapPos();
            var titleBottom = ImGui.GetItemRectMax().Y;
            DrawFavoriteStar(preset, favoritePosition);
            ImGui.SetCursorScreenPos(new Vector2(
                titlePosition.X,
                MathF.Max(titleBottom, favoritePosition.Y + favoriteSize.Y) + ImGui.GetStyle().ItemSpacing.Y));
            var previewHeight = ImGui.GetTextLineHeightWithSpacing() * 5f + TabletAppTheme.Px(8f);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.10f, 0.18f, 1f));
            if (ImGui.BeginChild("##emote-preview", new Vector2(-1f, previewHeight), true))
            {
                if (preset.Mods.Count == 0)
                {
                    TextColoredWrapped(TabletAppTheme.MutedText, "No emote mod actions configured");
                }
                else
                {
                    foreach (var rule in preset.Mods)
                    {
                        var state = rule.Enabled ? "Enabled" : "Disabled";
                        var options = rule.Options.Values.SelectMany(value => value)
                            .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
                        var optionText = options.Count == 0 ? string.Empty : " — " + string.Join(", ", options);
                        ImGui.TextWrapped($"{state}: {rule.Name}{optionText}");
                    }
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            var buttonWidth = MathF.Max(TabletAppTheme.Px(62f), (ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(6f)) / 2f);
            if (ImGui.Button($"Apply##{preset.Id}", new Vector2(buttonWidth, 0f))) RequestApply(preset);
            ImGui.SameLine();
            if (ImGui.Button($"Edit##{preset.Id}", new Vector2(buttonWidth, 0f))) BeginEdit(preset);
            ImGui.Dummy(new Vector2(0f, TabletAppTheme.Px(6f)));
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private unsafe void DrawFavoriteStar(WardrobePreset preset, Vector2 position)
    {
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(position);
        var star = preset.IsFavorite ? "\u2605" : "\u2606";
        var font = ImGui.GetFont();
        var glyph = font.FindGlyph(star[0]);
        var fontScale = font.FontSize > 0f ? ImGui.GetFontSize() / font.FontSize : 1f;
        var glyphSize = glyph is null
            ? ImGui.CalcTextSize(star)
            : new Vector2((glyph->X1 - glyph->X0) * fontScale, (glyph->Y1 - glyph->Y0) * fontScale);
        var margin = TabletAppTheme.Px(new Vector2(10f, 10f));
        var size = glyphSize + margin * 2f;
        var clicked = ImGui.InvisibleButton($"##wardrobe-favorite-{preset.Id}", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var background = preset.IsFavorite
            ? new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, active ? 0.96f : hovered ? 0.88f : 0.78f)
            : new Vector4(0.055f, 0.045f, 0.085f, active ? 0.98f : hovered ? 0.94f : 0.86f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, ImGui.ColorConvertFloat4ToU32(background), TabletAppTheme.Px(5f));
        draw.AddRect(position, position + size,
            ImGui.ColorConvertFloat4ToU32(new Vector4(TabletAppTheme.AccentHover.X,
                TabletAppTheme.AccentHover.Y, TabletAppTheme.AccentHover.Z, 0.9f)), TabletAppTheme.Px(5f));
        var glyphCenter = glyph is null
            ? ImGui.CalcTextSize(star) * 0.5f
            : new Vector2((glyph->X0 + glyph->X1) * 0.5f * fontScale,
                (glyph->Y0 + glyph->Y1) * 0.5f * fontScale);
        var textPosition = position + size * 0.5f - glyphCenter;
        draw.AddText(textPosition,
            ImGui.ColorConvertFloat4ToU32(preset.IsFavorite ? Vector4.One : TabletAppTheme.AccentHover), star);
        if (clicked)
        {
            preset.IsFavorite = !preset.IsFavorite;
            persistence.Save();
        }
        if (hovered)
            ImGui.SetTooltip(preset.IsFavorite ? "Remove from favorites" : "Keep this preset at the start of the page");
        ImGui.SetCursorScreenPos(cursor);
    }

    private unsafe Vector2 GetFavoriteStarSize(WardrobePreset preset)
    {
        var star = preset.IsFavorite ? "\u2605" : "\u2606";
        var font = ImGui.GetFont();
        var glyph = font.FindGlyph(star[0]);
        var fontScale = font.FontSize > 0f ? ImGui.GetFontSize() / font.FontSize : 1f;
        var glyphSize = glyph is null
            ? ImGui.CalcTextSize(star)
            : new Vector2((glyph->X1 - glyph->X0) * fontScale, (glyph->Y1 - glyph->Y0) * fontScale);
        return glyphSize + TabletAppTheme.Px(new Vector2(20f, 20f));
    }

    private void DrawConnectionAndQuickRow(bool synchronizeQuickSelection)
    {
        var available = ImGui.GetContentRegionAvail().X;
        var controlsWidth = MathF.Min(available * 0.52f,
            available / 3f + TabletAppTheme.Px(125f) + ImGui.GetStyle().ItemSpacing.X + ImGui.GetStyle().CellPadding.X * 2f);
        if (!ImGui.BeginTable("##wardrobe-status-quick", 2,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings)) return;
        ImGui.TableSetupColumn("Connection", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Quick Selection", ImGuiTableColumnFlags.WidthFixed, controlsWidth);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var status = integrations.RequirementState;
        var connectedColor = new Vector4(0.30f, 0.92f, 0.46f, 1f);
        var disconnectedColor = new Vector4(1f, 0.35f, 0.35f, 1f);
        ImGui.TextColored(status.PenumbraConnected ? connectedColor : disconnectedColor, "Penumbra");
        ImGui.SameLine(0f, 0f);
        ImGui.TextUnformatted(" and ");
        ImGui.SameLine(0f, 0f);
        ImGui.TextColored(status.GlamourerConnected ? connectedColor : disconnectedColor, "Glamourer");
        ImGui.SameLine(0f, 0f);
        ImGui.TextUnformatted(" " + status.Message);
        ImGui.TableNextColumn();
        var cellWidth = ImGui.GetContentRegionAvail().X;
        var buttonWidth = MathF.Min(TabletAppTheme.Px(118f), cellWidth * 0.34f);
        var dropdownWidth = MathF.Max(1f, cellWidth - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        DrawQuickSelectionBar(dropdownWidth, buttonWidth, synchronizeQuickSelection);
        ImGui.EndTable();
    }

    private void DrawQuickSelectionBar(float dropdownWidth, float buttonWidth, bool synchronizeQuickSelection)
    {
        if (synchronizeQuickSelection || DateTime.UtcNow >= nextQuickDesignRefresh)
        {
            quickDesigns = integrations.GetQuickDesigns().ToList();
            nextQuickDesignRefresh = DateTime.UtcNow.AddSeconds(2);
            if (synchronizeQuickSelection)
            {
                var glamourerSelected = integrations.GetSelectedQuickDesign();
                quickSelectionId = quickDesigns.Any(design => design.Id == glamourerSelected)
                    ? glamourerSelected
                    : Guid.Empty;
                quickSelectionInitialized = true;
            }
            else if (quickSelectionId != Guid.Empty && quickDesigns.All(design => design.Id != quickSelectionId))
            {
                quickSelectionId = Guid.Empty;
            }
        }

        if (quickDesigns.Count == 0)
        {
            ImGui.SetNextItemWidth(dropdownWidth);
            ImGui.BeginDisabled();
            ImGui.Button("No Glamourer Quick Designs", new Vector2(dropdownWidth, 0f));
            ImGui.SameLine();
            ImGui.Button("Apply Selected", new Vector2(buttonWidth, 0f));
            ImGui.EndDisabled();
            return;
        }

        var selected = quickDesigns.FirstOrDefault(design => design.Id == quickSelectionId);
        ImGui.SetNextItemWidth(dropdownWidth);
        if (ImGui.BeginCombo("##wardrobe-quick-design", selected?.Name ?? "No Quick Design selected"))
        {
            foreach (var design in quickDesigns)
            {
                if (!ImGui.Selectable(design.Name, design.Id == quickSelectionId)) continue;
                quickSelectionId = design.Id;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(selected is null);
        if (ImGui.Button("Apply Selected", new Vector2(buttonWidth, 0f)) && selected is not null)
        {
            var preset = persistence.Data.Presets.FirstOrDefault(item => item.Type == WardrobePresetType.Outfit
                && item.GlamourerDesignId == quickSelectionId);
            if (preset is not null) RequestApply(preset);
            else notification = integrations.ApplyQuickDesign(selected.Id, selected.Name).Message;
        }
        ImGui.EndDisabled();
    }

    private void BeginEdit(WardrobePreset preset)
    {
        if (preset.Type == WardrobePresetType.Outfit && preset.GlamourerDesignId != Guid.Empty)
        {
            if (integrations.RefreshOutfitFromGlamourer(preset, out var error)) persistence.Save();
            else notification = error;
        }
        else if (preset.Type == WardrobePresetType.Character && preset.GlamourerDesignId != Guid.Empty)
        {
            if (integrations.RefreshCharacterFromGlamourer(preset, out var error)) persistence.Save();
            else notification = error;
        }
        editing = preset;
        editingSnapshot = ClonePreset(preset);
        outfitDirty = false;
        modSearch = string.Empty;
        RefreshMods();
    }

    private void CloseEditor()
    {
        editing = null;
        editingSnapshot = null;
        outfitDirty = false;
        modSearch = string.Empty;
    }

    private static WardrobePreset ClonePreset(WardrobePreset preset)
        => Newtonsoft.Json.JsonConvert.DeserializeObject<WardrobePreset>(
            Newtonsoft.Json.JsonConvert.SerializeObject(preset)) ?? new WardrobePreset();

    private void DiscardEditorChanges(WardrobePreset preset)
    {
        if (editingSnapshot is not null)
        {
            var index = persistence.Data.Presets.FindIndex(item => ReferenceEquals(item, preset) || item.Id == preset.Id);
            if (index >= 0)
            {
                persistence.Data.Presets[index] = ClonePreset(editingSnapshot);
                persistence.Save();
            }
        }
        CloseEditor();
        notification = "Preset changes were discarded.";
    }

    private void DrawEditor()
    {
        var preset = editing!;
        if (ImGui.BeginTable("##wardrobe-editor-header", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(90f));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"Edit {TypeLabel(preset.Type)} Preset");
            ImGui.SameLine();
            if (ImGui.Button("Done", TabletAppTheme.Px(new Vector2(90f, 0f))))
            {
                persistence.Save();
                if ((preset.Type is WardrobePresetType.Outfit or WardrobePresetType.Character) && outfitDirty) RequestDesignSync(preset, true);
                else CloseEditor();
                ImGui.EndTable();
                return;
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(90f, 0f))))
            {
                pendingDelete = preset;
                TabletAppTheme.OpenCenteredModal(preset.Type is WardrobePresetType.Outfit or WardrobePresetType.Character
                    ? "Delete linked Glamourer design?" : "Delete WardrobeManager preset?");
            }
            ImGui.TableNextColumn();
            if (ImGui.Button("Settings", new Vector2(-1f, 0f))) settingsVisible = true;
            ImGui.EndTable();
        }
        ImGui.Separator();

        // The table adds vertical cell padding around its children. Reserve that space so
        // the editor consumes the remaining tablet viewport without creating an outer scroll bar.
        var panelHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y
            - ImGui.GetStyle().CellPadding.Y * 2f - TabletAppTheme.Px(20f));
        if (ImGui.BeginTable("##wardrobe-editor", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Identity", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Mods", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawIdentityEditor(preset, panelHeight);
            ImGui.TableNextColumn();
            DrawModEditor(preset, panelHeight);
            ImGui.EndTable();
        }
    }

    private void DrawIdentityEditor(WardrobePreset preset, float height)
    {
        if (preset.Type == WardrobePresetType.Character)
        {
            DrawCharacterIdentityEditor(preset, height);
            return;
        }
        DrawCard("Preset", height, false, () =>
        {
            var name = preset.Name;
            if (preset.Type is WardrobePresetType.Outfit or WardrobePresetType.Character)
            {
                if (ImGui.BeginTable("##wardrobe-identity-fields", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    TextColoredWrapped(TabletAppTheme.MutedText, "Preset name");
                    ImGui.TableSetColumnIndex(1);
                    TextColoredWrapped(TabletAppTheme.MutedText, preset.Type == WardrobePresetType.Outfit ? "Folder" : "Penumbra collection");
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##wardrobe-preset-name", ref name, 80))
                    {
                        preset.Name = name;
                        if (preset.Type is WardrobePresetType.Outfit or WardrobePresetType.Character) outfitDirty = true;
                        persistence.Save();
                    }
                    ImGui.TableSetColumnIndex(1);
                    if (preset.Type == WardrobePresetType.Outfit)
                    {
                        var folderName = persistence.Data.Folders.FirstOrDefault(folder => folder.Id == preset.FolderId)?.Name ?? "Unfiled";
                        ImGui.SetNextItemWidth(-1f);
                        if (ImGui.BeginCombo("##wardrobe-folder", folderName))
                        {
                            if (ImGui.Selectable("Unfiled", preset.FolderId == Guid.Empty)) { preset.FolderId = Guid.Empty; outfitDirty = true; persistence.Save(); }
                            foreach (var folder in persistence.Data.Folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
                                if (ImGui.Selectable(folder.Name, preset.FolderId == folder.Id)) { preset.FolderId = folder.Id; outfitDirty = true; persistence.Save(); }
                            ImGui.EndCombo();
                        }
                    }
                    else DrawCollectionCombo(preset);
                    ImGui.EndTable();
                }
            }
            else
            {
                TextColoredWrapped(TabletAppTheme.MutedText, "Preset name");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputText("##wardrobe-preset-name", ref name, 80)) { preset.Name = name; persistence.Save(); }
            }

            var portrait = textures.Get(preset.ImagePath);
            var portraitWidth = MathF.Min(ImGui.GetContentRegionAvail().X, TabletAppTheme.Px(175f));
            var size = new Vector2(portraitWidth, portraitWidth * 16f / 9f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - portraitWidth) * 0.5f));
            if (portrait is not null) DrawPortrait(portrait, size); else
            {
                var position = ImGui.GetCursorScreenPos();
                ImGui.Dummy(size);
                ImGui.GetWindowDrawList().AddRectFilled(position, position + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.10f, 0.18f, 1f)), TabletAppTheme.Px(5f));
                var labelSize = ImGui.CalcTextSize("No portrait selected");
                ImGui.GetWindowDrawList().AddText(position + (size - labelSize) * 0.5f, ImGui.ColorConvertFloat4ToU32(TabletAppTheme.MutedText), "No portrait selected");
            }
            if (preset.Type is WardrobePresetType.Outfit or WardrobePresetType.Character)
            {
                if (ImGui.BeginTable("##wardrobe-image-buttons", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
                {
                    ImGui.TableNextColumn();
                    if (ImGui.Button("Choose Portrait", new Vector2(-1f, 0f))) ChoosePortrait(preset);
                    ImGui.TableNextColumn();
                    if (ImGui.Button("Take Selfie", new Vector2(-1f, 0f))) selfieCamera.Open(preset);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.PushTextWrapPos(TabletAppTheme.Px(340f));
                        ImGui.TextUnformatted("Open the live game camera with a 9:16 portrait guide and save the result directly to this preset.");
                        ImGui.PopTextWrapPos();
                        ImGui.EndTooltip();
                    }
                    ImGui.EndTable();
                }
                if (ImGui.Button("Capture Current Appearance", new Vector2(-1f, 0f)))
                {
                    if (preset.Type == WardrobePresetType.Character)
                    {
                        if (integrations.TryCaptureCharacterAppearance(out var characterJson, out var error))
                        {
                            preset.CharacterAppearanceJson = characterJson;
                            outfitDirty = true;
                            persistence.Save();
                            notification = $"Captured the physical appearance for {preset.Name}. Press Save to update the character preset.";
                        }
                        else notification = error;
                    }
                    else if (integrations.TryCaptureOutfitAppearance(out var outfitJson, out var equipment, out var outfitError))
                    {
                        preset.OutfitAppearanceJson = outfitJson;
                        preset.EquipmentItemIds = equipment;
                        outfitDirty = true;
                        persistence.Save();
                        notification = $"Captured the current outfit for {preset.Name}. Save to Glamourer to update the linked outfit design.";
                    }
                    else notification = outfitError;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(TabletAppTheme.Px(340f));
                    ImGui.TextUnformatted(preset.Type == WardrobePresetType.Character
                        ? "Capture only your physical character appearance and Customize Parameters. Weapons, armor, accessories, dyes, crests, materials, and the portrait image are not captured."
                        : "Capture only your current outfit: equipment, weapons, accessories, dyes, materials, and advanced dyes. Physical appearance and Customize Parameters are excluded. This does not take or change the portrait image.");
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
                TextColoredWrapped(TabletAppTheme.MutedText, preset.Type == WardrobePresetType.Character
                    ? string.IsNullOrWhiteSpace(preset.CharacterAppearanceJson) ? "No physical appearance captured" : "Physical customizations and Customize Parameters captured"
                    : string.IsNullOrWhiteSpace(preset.OutfitAppearanceJson) && string.IsNullOrWhiteSpace(preset.GlamourerState)
                        ? "No outfit captured" : "Outfit captured (physical appearance excluded)");
            }
            else if (ImGui.Button("Choose Portrait", new Vector2(-1f, 0f))) ChoosePortrait(preset);
        }, false);
    }

    private void DrawCharacterIdentityEditor(WardrobePreset preset, float height)
    {
        if (DateTime.UtcNow >= nextCharacterIntegrationRefresh)
        {
            nextCharacterIntegrationRefresh = DateTime.UtcNow.AddSeconds(5);
            customizeProfiles = integrations.GetCustomizePlusProfiles().ToList();
            honorificTitles = integrations.GetHonorificTitles().ToList();
        }

        DrawCard("Character", height, false, () =>
        {
            if (!ImGui.BeginTable("##character-identity-columns", 2,
                    ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings)) return;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var portrait = textures.Get(preset.ImagePath);
            var portraitWidth = MathF.Min(ImGui.GetContentRegionAvail().X, TabletAppTheme.Px(160f));
            var portraitSize = new Vector2(portraitWidth, portraitWidth * 16f / 9f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f,
                (ImGui.GetContentRegionAvail().X - portraitWidth) * 0.5f));
            if (portrait is not null) DrawPortrait(portrait, portraitSize);
            else
            {
                var position = ImGui.GetCursorScreenPos();
                ImGui.Dummy(portraitSize);
                ImGui.GetWindowDrawList().AddRectFilled(position, position + portraitSize,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.10f, 0.18f, 1f)), TabletAppTheme.Px(5f));
                var labelSize = ImGui.CalcTextSize("No portrait selected");
                ImGui.GetWindowDrawList().AddText(position + (portraitSize - labelSize) * 0.5f,
                    ImGui.ColorConvertFloat4ToU32(TabletAppTheme.MutedText), "No portrait selected");
            }
            if (ImGui.Button("Choose Portrait", new Vector2(-1f, 0f))) ChoosePortrait(preset);
            if (ImGui.Button("Take Selfie", new Vector2(-1f, 0f))) selfieCamera.Open(preset);

            ImGui.TableNextColumn();
            TextColoredWrapped(TabletAppTheme.MutedText, "Preset name");
            var name = preset.Name;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##character-preset-name", ref name, 80))
            {
                preset.Name = name;
                outfitDirty = true;
                persistence.Save();
            }

            TextColoredWrapped(TabletAppTheme.MutedText, "Penumbra collection");
            DrawCollectionCombo(preset);

            TextColoredWrapped(TabletAppTheme.MutedText, "Customize+ profile");
            var selectedProfile = customizeProfiles.FirstOrDefault(profile => profile.Id == preset.CustomizePlusProfileId);
            var profilePreview = selectedProfile is null ? "Do not change" : selectedProfile.Path;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##character-customize-profile", profilePreview))
            {
                if (ImGui.Selectable("Do not change", preset.CustomizePlusProfileId == Guid.Empty))
                {
                    preset.CustomizePlusProfileId = Guid.Empty;
                    preset.CustomizePlusProfileName = string.Empty;
                    preset.CustomizePlusProfilePath = string.Empty;
                    persistence.Save();
                }
                foreach (var profile in customizeProfiles)
                {
                    if (!ImGui.Selectable(profile.Path, profile.Id == preset.CustomizePlusProfileId)) continue;
                    preset.CustomizePlusProfileId = profile.Id;
                    preset.CustomizePlusProfileName = profile.Name;
                    preset.CustomizePlusProfilePath = profile.Path;
                    persistence.Save();
                }
                ImGui.EndCombo();
            }

            TextColoredWrapped(TabletAppTheme.MutedText, "Honorific title");
            var honorificPreview = !preset.HonorificTitleConfigured ? "Do not change"
                : string.IsNullOrWhiteSpace(preset.HonorificTitleName) ? "No title" : preset.HonorificTitleName;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##character-honorific-title", honorificPreview))
            {
                if (ImGui.Selectable("Do not change", !preset.HonorificTitleConfigured))
                {
                    preset.HonorificTitleConfigured = false;
                    outfitDirty = true;
                    persistence.Save();
                }
                if (ImGui.Selectable("No title", preset.HonorificTitleConfigured && string.IsNullOrWhiteSpace(preset.HonorificTitleName)))
                {
                    preset.HonorificTitleConfigured = true;
                    preset.HonorificTitleName = string.Empty;
                    preset.HonorificTitleJson = string.Empty;
                    preset.HonorificTitleId = string.Empty;
                    preset.HonorificUsesExistingTitle = false;
                    outfitDirty = true;
                    persistence.Save();
                }
                foreach (var title in honorificTitles)
                {
                    var label = title.IsPrefix ? $"{title.Name} (prefix)" : $"{title.Name} (suffix)";
                    if (!ImGui.Selectable(label, preset.HonorificUsesExistingTitle
                            && preset.HonorificTitleId == title.Id
                            && preset.HonorificTitleName.Equals(title.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    preset.HonorificTitleConfigured = true;
                    preset.HonorificTitleName = title.Name;
                    preset.HonorificCustomIsPrefix = title.IsPrefix;
                    preset.HonorificTitleJson = title.Json;
                    preset.HonorificTitleId = title.Id;
                    preset.HonorificUsesExistingTitle = true;
                    outfitDirty = true;
                    persistence.Save();
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("New Honorific Title", new Vector2(-1f, 0f)))
            {
                manualHonorificPreset = preset;
                manualHonorificDraft = ManualHonorificDraft.FromPreset(preset);
                TabletAppTheme.OpenCenteredModal(ManualHonorificModal);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create or edit a persistent Honorific title with placement, colour, glow or gradient effects, and activation conditions.");

            if (ImGui.Button("Capture Current Appearance", new Vector2(-1f, 0f)))
            {
                if (integrations.TryCaptureCharacterAppearance(out var characterJson, out var error))
                {
                    preset.CharacterAppearanceJson = characterJson;
                    outfitDirty = true;
                    persistence.Save();
                    notification = $"Captured the physical appearance for {preset.Name}. Press Save to update the character preset.";
                }
                else notification = error;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Captures only physical Customizations and Customize Parameters. It does not capture weapons, armor, accessories, dyes, crests, materials, or the portrait image.");
            TextColoredWrapped(TabletAppTheme.MutedText,
                string.IsNullOrWhiteSpace(preset.CharacterAppearanceJson)
                    ? "No physical appearance captured"
                    : "Physical customizations and Customize Parameters captured");
            ImGui.EndTable();
        }, false);
    }

    private static void UpdateCustomHonorific(WardrobePreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.HonorificTitleName))
        {
            preset.HonorificTitleJson = string.Empty;
            return;
        }
        preset.HonorificTitleJson = new Newtonsoft.Json.Linq.JObject
        {
            ["Title"] = preset.HonorificTitleName.Trim(),
            ["IsPrefix"] = preset.HonorificCustomIsPrefix,
            ["IsOriginal"] = false,
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    private void DrawManualHonorificModal()
    {
        if (manualHonorificPreset is null || manualHonorificDraft is null
            || !TabletAppTheme.BeginCenteredModal(ManualHonorificModal)) return;
        var draft = manualHonorificDraft;
        TextColoredWrapped(TabletAppTheme.MutedText,
            "Creates a persistent title in Honorific. Confirming reloads Honorific once so the title remains editable in Honorific afterward.");
        ImGui.Spacing();
        var controlBackground = new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y,
            TabletAppTheme.Accent.Z, 0.24f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, controlBackground);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(TabletAppTheme.Accent.X,
            TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.36f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(TabletAppTheme.Accent.X,
            TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.48f));

        if (ImGui.BeginTable("##manual-honorific-layout", 2,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(TabletAppTheme.AccentHover, "Title options");
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##manual-honorific-title", "Title text", ref draft.Title, 32);
            ImGui.Checkbox("Appears before character name", ref draft.IsPrefix);

            ImGui.TextDisabled("Glow / gradient effect");
            var paletteIndex = Math.Clamp(draft.EffectPalette + 2, 0, HonorificEffectPalettes.Length - 1);
            ImGui.SetNextItemWidth(-1f);
            if (DrawManualTitleCombo("##manual-honorific-palette", ref paletteIndex, HonorificEffectPalettes))
                draft.EffectPalette = paletteIndex - 2;
            if (draft.EffectPalette >= -1)
            {
                var styles = new[] { "Pulse", "Wave", "Static" };
                var animation = (int)draft.EffectAnimation;
                ImGui.SetNextItemWidth(-1f);
                if (DrawManualTitleCombo("##manual-honorific-animation", ref animation, styles))
                    draft.EffectAnimation = (WardrobeHonorificAnimation)animation;
            }

            ImGui.TextDisabled("Activation condition");
            var conditions = new[] { "None", "Class / Job", "Role", "Gear Set", "Original Title", "Location" };
            var condition = (int)draft.Condition;
            ImGui.SetNextItemWidth(-1f);
            if (DrawManualTitleCombo("##manual-honorific-condition", ref condition, conditions))
            {
                draft.Condition = (WardrobeHonorificCondition)condition;
                draft.ConditionParam = 0;
            }
            if (draft.Condition == WardrobeHonorificCondition.JobRole)
            {
                var roles = new[] { "Choose role", "Tank", "Healer", "DPS", "Crafter / Gatherer", "Melee DPS", "Ranged Physical DPS", "Ranged Magical DPS", "Crafter", "Gatherer" };
                draft.ConditionParam = Math.Clamp(draft.ConditionParam, 0, roles.Length - 1);
                ImGui.SetNextItemWidth(-1f);
                DrawManualTitleCombo("##manual-honorific-role", ref draft.ConditionParam, roles);
            }
            else if (draft.Condition is WardrobeHonorificCondition.ClassJob
                     or WardrobeHonorificCondition.GearSet
                     or WardrobeHonorificCondition.OriginalTitle)
            {
                var label = draft.Condition switch
                {
                    WardrobeHonorificCondition.ClassJob => "Class / Job ID",
                    WardrobeHonorificCondition.GearSet => "Gear set number",
                    _ => "Original title ID",
                };
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputInt($"{label}##manual-honorific-condition-param", ref draft.ConditionParam))
                    draft.ConditionParam = Math.Max(0, draft.ConditionParam);
            }
            else if (draft.Condition == WardrobeHonorificCondition.Location)
            {
                ImGui.TextDisabled($"Territory ID: {draft.TerritoryId}");
                if (ImGui.Button("Use Current Location", new Vector2(-1f, 0f)))
                    draft.TerritoryId = DalamudServices.ClientState.TerritoryType;
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(TabletAppTheme.AccentHover, "Colour palette");
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.09f, 0.075f, 0.14f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, TabletAppTheme.Px(new Vector2(12f, 8f)));
            if (ImGui.BeginChild("##manual-honorific-colours", new Vector2(-1f, TabletAppTheme.Px(305f)), true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (ImGui.BeginTabBar("##manual-honorific-colour-tabs"))
                {
                    if (ImGui.BeginTabItem("Title"))
                    {
                        ImGui.Checkbox("Use title colour", ref draft.UseTextColor);
                        ImGui.BeginDisabled(!draft.UseTextColor);
                        DrawCenteredColorPicker("##manual-title-colour-picker", ref draft.TextColor);
                        ImGui.EndDisabled();
                        ImGui.EndTabItem();
                    }
                    if (draft.EffectPalette <= -1 && ImGui.BeginTabItem(draft.EffectPalette == -1 ? "Effect 1" : "Glow"))
                    {
                        if (draft.EffectPalette == -2) ImGui.Checkbox("Use custom glow", ref draft.UseGlow);
                        ImGui.BeginDisabled(draft.EffectPalette == -2 && !draft.UseGlow);
                        DrawCenteredColorPicker("##manual-glow-colour-picker", ref draft.GlowColor);
                        ImGui.EndDisabled();
                        ImGui.EndTabItem();
                    }
                    if (draft.EffectPalette == -1 && ImGui.BeginTabItem("Effect 2"))
                    {
                        DrawCenteredColorPicker("##manual-effect-two-colour-picker", ref draft.EffectColor2);
                        ImGui.EndTabItem();
                    }
                    if (draft.EffectPalette >= 0 && ImGui.BeginTabItem("Effect"))
                    {
                        TextColoredWrapped(TabletAppTheme.MutedText,
                            $"{HonorificEffectPalettes[draft.EffectPalette + 2]} uses Honorific's built-in palette with the selected {draft.EffectAnimation.ToString().ToLowerInvariant()} animation.");
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
            ImGui.EndTable();
        }
        ImGui.PopStyleColor(3);

        var valid = !string.IsNullOrWhiteSpace(draft.Title)
            && draft.Title.Trim().Length <= 32
            && !draft.Title.Any(char.IsControl)
            && (draft.Condition != WardrobeHonorificCondition.Location || draft.TerritoryId != 0);
        if (!valid)
            TextColoredWrapped(new Vector4(1f, 0.35f, 0.35f, 1f),
                "Enter a title of 1–32 printable characters. Location conditions also need a territory.");
        var actionWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button("Confirm", new Vector2(actionWidth, 0f)))
        {
            var preset = manualHonorificPreset;
            draft.ApplyTo(preset, honorificTitles);
            UpdateCustomHonorific(preset);
            outfitDirty = true;
            persistence.Save();
            if (QueueManualHonorificSave(preset))
                notification = $"Saving {preset.HonorificTitleName} as a persistent Honorific title.";
            manualHonorificPreset = null;
            manualHonorificDraft = null;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(actionWidth, 0f)))
        {
            manualHonorificPreset = null;
            manualHonorificDraft = null;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private static bool DrawManualTitleCombo(string id, ref int selectedIndex, IReadOnlyList<string> options)
    {
        selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, options.Count - 1));
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(TabletAppTheme.Px(180f), 0f),
            new Vector2(float.MaxValue, TabletAppTheme.Px(220f)));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.07f, 0.055f, 0.105f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y,
            TabletAppTheme.Accent.Z, 0.78f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);
        var changed = false;
        if (ImGui.BeginCombo(id, options.Count == 0 ? "None" : options[selectedIndex]))
        {
            for (var index = 0; index < options.Count; index++)
            {
                var selected = index == selectedIndex;
                if (ImGui.Selectable(options[index], selected))
                {
                    selectedIndex = index;
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        return changed;
    }

    private static void DrawCenteredColorPicker(string id, ref Vector3 colour)
    {
        var available = ImGui.GetContentRegionAvail().X;
        var width = MathF.Min(available, TabletAppTheme.Px(250f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (available - width) * 0.5f));
        ImGui.SetNextItemWidth(width);
        ImGui.ColorPicker3(id, ref colour,
            ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.PickerHueWheel);
    }

    private void ChoosePortrait(WardrobePreset preset)
    {
        imageDialog.Pick(path =>
        {
            try
            {
                var old = preset.ImagePath;
                preset.ImagePath = persistence.ImportImage(path, preset.Id);
                textures.Invalidate(old);
                persistence.Save();
                notification = "WardrobeManager portrait saved.";
            }
            catch (Exception ex) { notification = ex.Message; }
        });
    }

    private void DrawModEditor(WardrobePreset preset, float height)
    {
        var glamourerDesign = preset.Type is WardrobePresetType.Outfit or WardrobePresetType.Character;
        DrawCard(preset.Type == WardrobePresetType.Emote ? "Emote replacer layers" : "Mod associations", height, true, () =>
        {
            ImGui.TextWrapped(glamourerDesign
                ? $"These entries mirror Glamourer's mod associations for this {TypeLabel(preset.Type).ToLowerInvariant().TrimEnd('s')}. Add or remove mods manually, choose their enabled state, priority, and options, then save the changes back to Glamourer."
                : "Higher priorities overwrite lower priorities. Conflicting mods outside this preset are disabled when it is applied.");
            if (glamourerDesign)
            {
                var actionWidth = MathF.Max(TabletAppTheme.Px(105f), (ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(10f)) / 3f);
                ImGui.BeginDisabled(!outfitDirty);
                var saveLabel = preset.Type == WardrobePresetType.Character ? "Save" : "Save to Glamourer";
                if (ImGui.Button(saveLabel, new Vector2(actionWidth, 0f))) RequestDesignSync(preset, false);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(preset.Type == WardrobePresetType.Character
                        ? "Saves the physical Glamourer character design and this preset's Penumbra collection, Customize+ profile, Honorific title, and mod associations."
                        : "Saves this captured outfit, Glamourer folder, and mod associations to the linked Glamourer design.");
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(outfitDirty || preset.GlamourerDesignId == Guid.Empty);
                if (ImGui.Button("Refresh", new Vector2(actionWidth, 0f)))
                {
                    var refreshed = preset.Type == WardrobePresetType.Outfit
                        ? integrations.RefreshOutfitFromGlamourer(preset, out var error)
                        : integrations.RefreshCharacterFromGlamourer(preset, out error);
                    if (refreshed)
                    {
                        outfitDirty = false;
                        persistence.Save();
                        notification = $"Refreshed {preset.Name} from Glamourer.";
                    }
                    else notification = error;
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(preset.GlamourerDesignId == Guid.Empty);
                if (ImGui.Button("Open Glamourer", new Vector2(actionWidth, 0f))) integrations.OpenLinkedDesign(preset);
                ImGui.EndDisabled();
                if (outfitDirty)
                    TextColoredWrapped(TabletAppTheme.MutedText, "Unsaved Glamourer changes. Save or press Done to review the replacement confirmation.");
            }
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##wardrobe-mod-search", "Search installed Penumbra mods", ref modSearch, 120);
            var matches = string.IsNullOrWhiteSpace(modSearch) ? [] : availableMods.Where(x => x.Name.Contains(modSearch.Trim(), StringComparison.OrdinalIgnoreCase) || x.Directory.Contains(modSearch.Trim(), StringComparison.OrdinalIgnoreCase)).Where(x => preset.Mods.All(rule => !rule.Directory.Equals(x.Directory, StringComparison.OrdinalIgnoreCase))).Take(8).ToList();
            foreach (var match in matches)
            {
                if (ImGui.Selectable($"{match.Name}##add-{match.Directory}"))
                {
                    preset.Mods.Add(integrations.CreateRule(match, 0));
                    modSearch = string.Empty;
                    if (glamourerDesign) outfitDirty = true;
                    persistence.Save();
                }
            }
            ImGui.Separator();
            var displayOrder = preset.Mods.Select((rule, index) => (Rule: rule, Index: index)).ToList();
            foreach (var entry in displayOrder)
            {
                var rule = entry.Rule;
                var index = entry.Index;
                ImGui.PushID(index);
                if (glamourerDesign)
                {
                    var rowFlags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;
                    if (ImGui.BeginTable("##association-row", 4, rowFlags))
                    {
                        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(112f));
                        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(82f));
                        ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(76f));
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        if (ImGui.BeginCombo("##association-state", AssociationStateLabel(rule.AssociationState)))
                        {
                            foreach (var state in Enum.GetValues<GlamourerModAssociationState>())
                            {
                                if (!ImGui.Selectable(AssociationStateLabel(state), rule.AssociationState == state)) continue;
                                rule.AssociationState = state;
                                rule.Enabled = state == GlamourerModAssociationState.Enabled;
                                outfitDirty = true;
                                persistence.Save();
                            }
                            ImGui.EndCombo();
                        }
                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(rule.Name);
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        var priority = rule.Priority;
                        if (ImGui.InputInt("##rule-priority", ref priority, 0, 0))
                        {
                            rule.Priority = Math.Clamp(priority, -9999, 9999);
                            outfitDirty = true;
                            persistence.Save();
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Priority");
                        ImGui.TableNextColumn();
                        var remove = ImGui.Button("Remove", new Vector2(-1f, 0f));
                        ImGui.EndTable();
                        if (remove)
                        {
                            preset.Mods.Remove(rule);
                            outfitDirty = true;
                            persistence.Save();
                        }
                        else DrawOptionEditor(rule, false, true);
                    }
                }
                else
                {
                    var rowFlags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;
                    if (ImGui.BeginTable("##layer-row", 4, rowFlags))
                    {
                        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(30f));
                        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(82f));
                        ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(76f));
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        var enabled = rule.Enabled;
                        if (ImGui.Checkbox("##enabled", ref enabled))
                        {
                            rule.Enabled = enabled;
                            persistence.Save();
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(enabled ? "Enabled" : "Disabled");
                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(rule.Name);
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        var priority = rule.Priority;
                        if (ImGui.InputInt("##rule-priority", ref priority, 0, 0))
                        {
                            rule.Priority = Math.Clamp(priority, -9999, 9999);
                            persistence.Save();
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Priority");
                        ImGui.TableNextColumn();
                        var remove = ImGui.Button("Remove", new Vector2(-1f, 0f));
                        ImGui.EndTable();
                        if (remove)
                        {
                            preset.Mods.Remove(rule);
                            persistence.Save();
                        }
                        else DrawOptionEditor(rule, preset.Type == WardrobePresetType.Emote, false);
                    }
                }
                ImGui.Separator();
                ImGui.PopID();
            }
            if (preset.Mods.Count == 0)
                TextColoredWrapped(TabletAppTheme.MutedText, glamourerDesign
                    ? "No Glamourer mod associations are configured. Search installed Penumbra mods above to add one manually."
                    : "Search above to add installed mods to this preset.");
        });
    }

    private static string AssociationStateLabel(GlamourerModAssociationState state) => state switch
    {
        GlamourerModAssociationState.Enabled => "Enabled",
        GlamourerModAssociationState.Disabled => "Disabled",
        GlamourerModAssociationState.Inherit => "Inherit",
        GlamourerModAssociationState.Remove => "Remove",
        _ => "Ignore",
    };

    private void DrawCollectionCombo(WardrobePreset preset)
    {
        var collections = integrations.GetCollections();
        var fallback = integrations.GetYourselfCollection();
        var selectedName = collections.FirstOrDefault(item => item.Id == preset.PenumbraCollectionId)?.Name
            ?? fallback?.Name ?? "No collection available";
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo("##wardrobe-collection", selectedName)) return;
        foreach (var collection in collections)
        {
            if (!ImGui.Selectable(collection.Name, collection.Id == preset.PenumbraCollectionId)) continue;
            preset.PenumbraCollectionId = collection.Id;
            preset.PenumbraCollectionName = collection.Name;
            persistence.Save();
        }
        ImGui.EndCombo();
    }

    private void DrawSettings()
    {
        ImGui.TextColored(TabletAppTheme.AccentHover, "WardrobeManager Settings");
        ImGui.Separator();
        var confirm = config.ConfirmBeforeApply;
        if (ImGui.Checkbox("Confirm before applying presets", ref confirm))
        {
            config.ConfirmBeforeApply = confirm;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        TextColoredWrapped(TabletAppTheme.MutedText, "Ask before applying a Glamourer design or another WardrobeManager preset.");
        var reloadGlamourer = config.ReloadGlamourerAfterFolderDelete;
        if (ImGui.Checkbox("Reload Glamourer after deleting an outfit folder", ref reloadGlamourer))
        {
            config.ReloadGlamourerAfterFolderDelete = reloadGlamourer;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        TextColoredWrapped(TabletAppTheme.MutedText,
            "Off by default. When enabled, WardrobeManager disables and then re-enables Glamourer after a folder is removed so Glamourer's folder list refreshes immediately.");
        ImGui.Spacing();
        ImGui.TextColored(TabletAppTheme.AccentHover, "Selfie folder");
        TextColoredWrapped(TabletAppTheme.MutedText, "WardrobeManager saves each directly captured 9:16 selfie here. Presets keep a separate managed copy so moving or deleting exported selfies will not break their portraits.");
        var folder = string.IsNullOrWhiteSpace(config.SelfieFolder) ? DefaultSelfieFolder() : config.SelfieFolder;
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##wardrobe-selfie-folder", ref folder, 1024, ImGuiInputTextFlags.ReadOnly);
        if (ImGui.Button("Choose Folder", TabletAppTheme.Px(new Vector2(150f, 0f))))
        {
            imageDialog.PickFolder(path =>
            {
                config.SelfieFolder = path;
                DalamudServices.PluginInterface.SavePluginConfig(config);
                RescanImages(path, "WardrobeManager selfie folder updated.");
            });
        }
        ImGui.SameLine();
        if (ImGui.Button("Use Default", TabletAppTheme.Px(new Vector2(130f, 0f))))
        {
            config.SelfieFolder = string.Empty;
            DalamudServices.PluginInterface.SavePluginConfig(config);
            RescanImages(DefaultSelfieFolder(), "WardrobeManager is using the default selfie folder.");
        }
        ImGui.SameLine();
        if (ImGui.Button("Rescan Images", TabletAppTheme.Px(new Vector2(145f, 0f))))
            RescanImages(folder);
        ImGui.SameLine();
        if (ImGui.Button("Delete Unused Images", TabletAppTheme.Px(new Vector2(180f, 0f))))
        {
            imageCleanupRequested = true;
            TabletAppTheme.OpenCenteredModal("Delete unused WardrobeManager images?");
        }

    }

    private static string DefaultSelfieFolder()
        => Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, "Wardrobe Selfies");

    private void TryAutomaticGlamourerImport()
    {
        if (DateTime.UtcNow < nextAutomaticGlamourerScan) return;
        nextAutomaticGlamourerScan = DateTime.UtcNow.AddSeconds(3);
        ImportGlamourerDesigns();
    }

    private void ImportGlamourerDesigns()
    {
        var scan = integrations.ScanGlamourerDesigns();
        if (!scan.Success)
        {
            return;
        }

        var existing = persistence.Data.Presets
            .Where(preset => preset.GlamourerDesignId != Guid.Empty)
            .GroupBy(preset => preset.GlamourerDesignId)
            .ToDictionary(group => group.Key, group => group.First());
        var designIds = scan.Designs.Select(design => design.Id).ToHashSet();
        foreach (var present in designIds) missingGlamourerDesignScans.Remove(present);
        foreach (var missing in existing.Keys.Where(id => !designIds.Contains(id)))
            missingGlamourerDesignScans[missing] = missingGlamourerDesignScans.GetValueOrDefault(missing) + 1;
        var removed = persistence.Data.Presets.RemoveAll(preset =>
            (preset.Type is WardrobePresetType.Outfit or WardrobePresetType.Character)
            && preset.GlamourerDesignId != Guid.Empty
            && missingGlamourerDesignScans.GetValueOrDefault(preset.GlamourerDesignId) >= 2);
        if (removed > 0)
            foreach (var missing in missingGlamourerDesignScans.Where(pair => pair.Value >= 2).Select(pair => pair.Key).ToList())
                missingGlamourerDesignScans.Remove(missing);
        var added = 0;
        var reclassified = 0;
        var organized = 0;
        var synchronized = 0;
        var foldersCreated = 0;
        var importedIntoFolders = 0;
        foreach (var design in scan.Designs)
        {
            var type = design.AppliesEquipment ? WardrobePresetType.Outfit : WardrobePresetType.Character;
            if (existing.TryGetValue(design.Id, out var existingPreset))
            {
                // Never overwrite unsaved editor changes with the periodic mirror.
                if (ReferenceEquals(existingPreset, editing) && outfitDirty) continue;
                var existingFolderId = type == WardrobePresetType.Outfit
                    ? ResolveGlamourerFolder(design.FolderPath, ref foldersCreated)
                    : Guid.Empty;
                var changed = existingPreset.Type != type
                    || existingPreset.FolderId != existingFolderId
                    || !existingPreset.Name.Equals(design.Name.Trim(), StringComparison.Ordinal)
                    || !existingPreset.GlamourerState.Equals(design.State, StringComparison.Ordinal)
                    || !existingPreset.GlamourerFolderPath.Equals(design.FolderPath, StringComparison.Ordinal)
                    || !existingPreset.EquipmentItemIds.OrderBy(pair => pair.Key)
                        .SequenceEqual(design.EquipmentItemIds.OrderBy(pair => pair.Key))
                    || (type == WardrobePresetType.Character
                        ? !existingPreset.CharacterAppearanceJson.Equals(design.CharacterJson, StringComparison.Ordinal)
                        : !existingPreset.OutfitAppearanceJson.Equals(design.OutfitJson, StringComparison.Ordinal))
                    || !ModRulesEqual(existingPreset.Mods, design.ModAssociations);
                if (!changed) continue;
                if (existingPreset.Type != type)
                {
                    existingPreset.Type = type;
                    reclassified++;
                }
                if (existingPreset.FolderId != existingFolderId)
                {
                    existingPreset.FolderId = existingFolderId;
                    organized++;
                }
                if (!existingPreset.EquipmentItemIds.OrderBy(pair => pair.Key).SequenceEqual(design.EquipmentItemIds.OrderBy(pair => pair.Key)))
                {
                    existingPreset.EquipmentItemIds = new Dictionary<string, uint>(design.EquipmentItemIds, StringComparer.OrdinalIgnoreCase);
                    organized++;
                }
                existingPreset.Name = design.Name.Trim();
                existingPreset.GlamourerState = design.State;
                existingPreset.GlamourerFolderPath = design.FolderPath;
                if (type == WardrobePresetType.Character)
                    existingPreset.CharacterAppearanceJson = design.CharacterJson;
                else
                    existingPreset.OutfitAppearanceJson = design.OutfitJson;
                existingPreset.Mods = design.ModAssociations.Select(CloneRule).ToList();
                existingPreset.RegisteredOutfitMods = [];
                existingPreset.AutomaticLayersScanned = false;
                synchronized++;
                continue;
            }

            var folderId = type == WardrobePresetType.Outfit
                ? ResolveGlamourerFolder(design.FolderPath, ref foldersCreated)
                : Guid.Empty;
            persistence.Data.Presets.Add(new WardrobePreset
            {
                Type = type,
                Name = string.IsNullOrWhiteSpace(design.Name)
                    ? type == WardrobePresetType.Character ? "Glamourer Character" : "Glamourer Outfit"
                    : design.Name.Trim(),
                GlamourerState = design.State,
                OutfitAppearanceJson = type == WardrobePresetType.Outfit ? design.OutfitJson : string.Empty,
                CharacterAppearanceJson = type == WardrobePresetType.Character ? design.CharacterJson : string.Empty,
                GlamourerDesignId = design.Id,
                GlamourerFolderPath = design.FolderPath,
                FolderId = folderId,
                EquipmentItemIds = new Dictionary<string, uint>(design.EquipmentItemIds, StringComparer.OrdinalIgnoreCase),
                Mods = design.ModAssociations.Select(CloneRule).ToList(),
            });
            added++;
            if (folderId != Guid.Empty) importedIntoFolders++;
        }

        if (added > 0 || removed > 0 || reclassified > 0 || organized > 0 || foldersCreated > 0 || synchronized > 0) persistence.Save();
        if (!config.GlamourerInitialImportCompleted)
        {
            config.GlamourerInitialImportCompleted = true;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        if (!config.GlamourerClassificationCompleted)
        {
            config.GlamourerClassificationCompleted = true;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        if (!config.GlamourerFolderImportCompleted)
        {
            config.GlamourerFolderImportCompleted = true;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        if (added > 0 || removed > 0 || reclassified > 0 || organized > 0 || foldersCreated > 0)
            notification = $"Glamourer library synchronized: imported {added}, removed {removed}, reclassified {reclassified}, updated folder placement for {organized + importedIntoFolders}.";
    }

    private static WardrobeModRule CloneRule(WardrobeModRule source) => new()
    {
        Directory = source.Directory,
        Name = source.Name,
        Priority = source.Priority,
        Enabled = source.Enabled,
        AssociationState = source.AssociationState,
        Options = source.Options.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase),
    };

    private static bool ModRulesEqual(IReadOnlyList<WardrobeModRule> left,
        IReadOnlyList<WardrobeModRule> right)
    {
        if (left.Count != right.Count) return false;
        return left.OrderBy(rule => rule.Directory, StringComparer.OrdinalIgnoreCase)
            .Zip(right.OrderBy(rule => rule.Directory, StringComparer.OrdinalIgnoreCase))
            .All(pair => pair.First.Directory.Equals(pair.Second.Directory, StringComparison.OrdinalIgnoreCase)
                && pair.First.AssociationState == pair.Second.AssociationState
                && pair.First.Priority == pair.Second.Priority
                && Newtonsoft.Json.JsonConvert.SerializeObject(pair.First.Options)
                    .Equals(Newtonsoft.Json.JsonConvert.SerializeObject(pair.Second.Options), StringComparison.Ordinal));
    }

    private Guid ResolveGlamourerFolder(string path, ref int created)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/').Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return Guid.Empty;
        var folder = persistence.Data.Folders.FirstOrDefault(item => item.GlamourerPath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (folder is not null) return folder.Id;

        var displayName = string.Join(" / ", normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        folder = persistence.Data.Folders.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.GlamourerPath) && item.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            folder = new WardrobeFolder { Name = displayName };
            persistence.Data.Folders.Add(folder);
            created++;
        }
        folder.GlamourerPath = normalized;
        return folder.Id;
    }

    private void RescanImages(string folder, string? successPrefix = null)
    {
        try
        {
            var relinked = persistence.RescanImages(folder);
            foreach (var item in relinked) textures.Invalidate(item.OldPath);
            var result = relinked.Count == 0
                ? "No missing preset portraits could be matched in that folder."
                : $"Relinked {relinked.Count} preset portrait{(relinked.Count == 1 ? string.Empty : "s")}.";
            notification = string.IsNullOrWhiteSpace(successPrefix) ? result : successPrefix + " " + result;
        }
        catch (Exception ex)
        {
            notification = "WardrobeManager could not rescan images: " + ex.Message;
        }
    }

    private void DrawOptionEditor(WardrobeModRule rule, bool openByDefault, bool marksOutfitDirty)
    {
        var groups = integrations.GetAvailableOptions(rule);
        if (groups.Count == 0) return;
        if (openByDefault) ImGui.SetNextItemOpen(true, ImGuiCond.FirstUseEver);
        if (!ImGui.TreeNode("Penumbra options")) return;
        foreach (var group in groups)
        {
            ImGui.PushID(group.Name);
            rule.Options.TryGetValue(group.Name, out var selected);
            selected ??= [];
            var preview = selected.Count == 0 ? "Choose option" : string.Join(", ", selected);
            ImGui.TextWrapped(group.Name);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##option", preview))
            {
                foreach (var choice in group.Choices)
                {
                    var chosen = selected.Contains(choice, StringComparer.OrdinalIgnoreCase);
                    var flags = group.AllowsMultiple ? ImGuiSelectableFlags.DontClosePopups : ImGuiSelectableFlags.None;
                    if (!ImGui.Selectable(choice, chosen, flags)) continue;
                    if (group.AllowsMultiple)
                    {
                        if (chosen) selected.RemoveAll(x => x.Equals(choice, StringComparison.OrdinalIgnoreCase));
                        else selected.Add(choice);
                    }
                    else
                    {
                        selected = [choice];
                    }

                    rule.Options[group.Name] = selected;
                    if (marksOutfitDirty) outfitDirty = true;
                    persistence.Save();
                }
                ImGui.EndCombo();
            }
            ImGui.PopID();
        }
        ImGui.TreePop();
    }

    private void DrawTypeTab(WardrobePresetType type)
    {
        var selected = !foldersVisible && activeType == type;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, TabletAppTheme.Px(7f));
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? TabletAppTheme.Accent : TabletAppTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TabletAppTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, TabletAppTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Vector4.One : TabletAppTheme.MutedText);
        if (ImGui.Button(TypeLabel(type), TabletAppTheme.Px(new Vector2(112f, 34f))))
        {
            activeType = type;
            foldersVisible = false;
            activeFolderId = Guid.Empty;
        }
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
    }

    private void DrawFolderTab()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, TabletAppTheme.Px(7f));
        ImGui.PushStyleColor(ImGuiCol.Button, foldersVisible ? TabletAppTheme.Accent : TabletAppTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TabletAppTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, TabletAppTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, foldersVisible ? Vector4.One : TabletAppTheme.MutedText);
        if (ImGui.Button("Folders", TabletAppTheme.Px(new Vector2(112f, 34f))))
        {
            foldersVisible = true;
            activeFolderId = Guid.Empty;
        }
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
    }

    private void CreatePreset(WardrobePresetType type)
    {
        var collection = integrations.GetYourselfCollection();
        var preset = new WardrobePreset
        {
            Type = type,
            FolderId = type == WardrobePresetType.Outfit && foldersVisible ? activeFolderId : Guid.Empty,
            Name = type switch { WardrobePresetType.Outfit => "New Outfit", WardrobePresetType.Character => "New Character", _ => "New Emote" },
            PenumbraCollectionId = type == WardrobePresetType.Character ? collection?.Id ?? Guid.Empty : Guid.Empty,
            PenumbraCollectionName = type == WardrobePresetType.Character ? collection?.Name ?? string.Empty : string.Empty,
        };
        persistence.Data.Presets.Add(preset);
        persistence.Save();
        BeginEdit(preset);
    }

    private void RequestApply(WardrobePreset preset)
    {
        if (!config.ConfirmBeforeApply) { Apply(preset); return; }
        pendingApply = preset;
        TabletAppTheme.OpenCenteredModal("Apply WardrobeManager preset?");
    }

    private void Apply(WardrobePreset preset)
    {
        ApplyResolved(preset);
    }

    private void ApplyResolved(WardrobePreset preset)
    {
        var result = integrations.Apply(preset);
        if (result.Success && preset.Type == WardrobePresetType.Outfit)
        {
            quickSelectionId = preset.GlamourerDesignId;
            integrations.SelectQuickDesign(preset);
        }
        notification = result.Message;
    }

    private bool QueueManualHonorificSave(WardrobePreset preset)
    {
        if (honorificSavePhase != HonorificSavePhase.None)
        {
            notification = "Honorific is already saving another new title.";
            return false;
        }
        if (!integrations.StageManualHonorificTitle(preset, out var error))
        {
            notification = error;
            return false;
        }
        pendingHonorificSave = preset;
        honorificSavePhase = HonorificSavePhase.WaitingForUnload;
        honorificUnloadedSince = DateTime.MinValue;
        honorificConfigWriteBeforeUnload = integrations.HonorificConfigLastWriteUtc();
        DalamudServices.CommandManager.ProcessCommand("/xldisableplugin \"Honorific\"");
        return true;
    }

    private void ProcessHonorificSave()
    {
        if (honorificSavePhase == HonorificSavePhase.None || pendingHonorificSave is null) return;
        var now = DateTime.UtcNow;
        if (honorificSavePhase == HonorificSavePhase.WaitingForUnload)
        {
            // The new title was added to Honorific's live typed config before
            // disabling it. Honorific persists that exact object at the end of
            // Dispose, so wait for both unload signals and its config-file write.
            if (integrations.IsHonorificLoaded() || integrations.IsHonorificReady()
                || integrations.HonorificConfigLastWriteUtc() <= honorificConfigWriteBeforeUnload)
            {
                honorificUnloadedSince = DateTime.MinValue;
                return;
            }
            if (honorificUnloadedSince == DateTime.MinValue)
            {
                honorificUnloadedSince = now;
                return;
            }
            // Dalamud can report the plugin as unloaded before its assembly-load context
            // has finished disposing. Leave a quiet period after the write before the
            // single re-enable so the loader cannot race Honorific's final save.
            if (now - honorificUnloadedSince < TimeSpan.FromSeconds(2)) return;

            var preset = pendingHonorificSave;
            if (!integrations.IsHonorificTitlePersisted(preset))
            {
                pendingHonorificSave = null;
                honorificSavePhase = HonorificSavePhase.None;
                DalamudServices.CommandManager.ProcessCommand("/xlenableplugin \"Honorific\"");
                notification = $"Honorific did not persist {preset.HonorificTitleName.Trim()} while shutting down.";
                return;
            }
            persistence.Save();
            honorificSavePhase = HonorificSavePhase.WaitingForReload;
            honorificReadySince = DateTime.MinValue;
            DalamudServices.CommandManager.ProcessCommand("/xlenableplugin \"Honorific\"");
            return;
        }

        if (!integrations.IsHonorificReady())
        {
            honorificReadySince = DateTime.MinValue;
            return;
        }
        if (honorificReadySince == DateTime.MinValue)
        {
            honorificReadySince = now;
            return;
        }
        if (now - honorificReadySince < TimeSpan.FromMilliseconds(1500)) return;

        var savedPreset = pendingHonorificSave;
        if (!integrations.IsHonorificTitleAvailable(savedPreset)
            || !integrations.IsHonorificTitlePersisted(savedPreset))
        {
            pendingHonorificSave = null;
            honorificSavePhase = HonorificSavePhase.None;
            notification = $"Honorific reloaded, but {savedPreset.HonorificTitleName.Trim()} was not retained in both its live title list and saved configuration.";
            return;
        }
        pendingHonorificSave = null;
        honorificSavePhase = HonorificSavePhase.None;
        nextCharacterIntegrationRefresh = DateTime.MinValue;
        notification = $"Saved {savedPreset.HonorificTitleName.Trim()} to Honorific.";
    }

    private void DrawApplyConfirmation()
    {
        if (pendingApply is null) return;
        if (!TabletAppTheme.BeginCenteredModal("Apply WardrobeManager preset?")) return;
        ImGui.TextWrapped(pendingApply.Type == WardrobePresetType.Outfit
            ? $"Apply {pendingApply.Name} through Glamourer? Glamourer will handle its saved appearance, mod associations, automation behavior, and redraw settings."
            : $"Apply {pendingApply.Name}?");
        if (ImGui.Button("Apply", TabletAppTheme.Px(new Vector2(110f, 0f)))) { var preset = pendingApply; pendingApply = null; TabletAppTheme.CloseCenteredModal(); Apply(preset); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110f, 0f)))) { pendingApply = null; TabletAppTheme.CloseCenteredModal(); }
        TabletAppTheme.EndCenteredModal();
    }

    private void RequestDesignSync(WardrobePreset preset, bool closeAfter)
    {
        pendingDesignSync = preset;
        closeEditorAfterSync = closeAfter;
        var kind = preset.Type == WardrobePresetType.Character ? "character" : "outfit";
        TabletAppTheme.OpenCenteredModal(preset.GlamourerDesignId == Guid.Empty
            ? $"Create Glamourer {kind}?" : $"Replace linked Glamourer {kind}?");
    }

    private void DrawDesignSyncConfirmation()
    {
        if (pendingDesignSync is null) return;
        var existing = pendingDesignSync.GlamourerDesignId != Guid.Empty;
        var character = pendingDesignSync.Type == WardrobePresetType.Character;
        var kind = character ? "character" : "outfit";
        var title = existing ? $"Replace linked Glamourer {kind}?" : $"Create Glamourer {kind}?";
        if (!TabletAppTheme.BeginCenteredModal(title)) return;
        ImGui.TextWrapped(character
            ? existing
                ? $"Replace {pendingDesignSync.Name} in its current Glamourer folder? Only regular Customizations, Customize Parameters, and mod associations are saved. Equipment, weapons, accessories, dyes, crests, materials, and advanced dyes are excluded."
                : $"Create {pendingDesignSync.Name} as a Glamourer character design? Only regular Customizations, Customize Parameters, and mod associations are saved."
            : existing
                ? $"Save changes to {pendingDesignSync.Name} in Glamourer? Glamourer's public API replaces the design with a new design ID, then removes the old design. Mod associations, priorities, options, Quick Design visibility, folder metadata, appearance, materials, and advanced dyes are copied. Glamourer automation rules that reference the old design ID may need to be pointed at the replacement afterward."
                : $"Create {pendingDesignSync.Name} as a Glamourer outfit? Its captured appearance and manual mod associations will be written to Glamourer.");
        if (ImGui.Button(existing ? "Replace" : "Create", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            var preset = pendingDesignSync;
            var closeAfter = closeEditorAfterSync;
            var selectedFolder = character ? null : persistence.Data.Folders.FirstOrDefault(folder => folder.Id == preset.FolderId);
            var folderPath = selectedFolder?.GlamourerPath?.Trim() ?? string.Empty;
            if (!character && selectedFolder is not null && string.IsNullOrWhiteSpace(folderPath))
            {
                // Folders created by older WardrobeManager versions were local-only.
                // Promote their display name to a Glamourer path the first time an
                // outfit is saved so existing user folders gain the new behavior.
                folderPath = selectedFolder.Name.Replace('\\', '/').Trim('/').Trim();
                selectedFolder.GlamourerPath = folderPath;
                persistence.Save();
            }
            var synchronized = character
                ? integrations.SyncCharacterToGlamourer(preset, out var message)
                : integrations.SyncOutfitToGlamourer(preset, folderPath, out message);
            if (synchronized)
            {
                outfitDirty = false;
                if (!character) quickSelectionId = preset.GlamourerDesignId;
                nextQuickDesignRefresh = DateTime.MinValue;
                persistence.Save();
                if (closeAfter) CloseEditor();
            }
            notification = message;
            pendingDesignSync = null;
            closeEditorAfterSync = false;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (closeEditorAfterSync && ImGui.Button("Do Not Save", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            var preset = pendingDesignSync;
            pendingDesignSync = null;
            closeEditorAfterSync = false;
            TabletAppTheme.CloseCenteredModal();
            DiscardEditorChanges(preset!);
            TabletAppTheme.EndCenteredModal();
            return;
        }
        if (closeEditorAfterSync) ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            pendingDesignSync = null;
            closeEditorAfterSync = false;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private void DrawDeleteConfirmation()
    {
        if (pendingDelete is null) return;
        var deletingDesign = pendingDelete.Type is WardrobePresetType.Outfit or WardrobePresetType.Character;
        var title = deletingDesign ? "Delete linked Glamourer design?" : "Delete WardrobeManager preset?";
        if (!TabletAppTheme.BeginCenteredModal(title)) return;
        ImGui.TextWrapped(deletingDesign && pendingDelete.GlamourerDesignId != Guid.Empty
            ? $"Delete {pendingDelete.Name} from both WardrobeManager and Glamourer? This permanently removes the linked Glamourer design, removes it from Glamourer's Quick Design list, and leaves any Glamourer automation rule that referenced it without that design. Penumbra mods are not deleted."
            : $"Delete {pendingDelete.Name}? This removes its WardrobeManager preset. Penumbra mods are not deleted.");
        if (ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            var deleted = pendingDelete;
            if (deletingDesign && !integrations.DeleteLinkedGlamourerDesign(deleted, out var error))
            {
                notification = error;
                TabletAppTheme.EndCenteredModal();
                return;
            }
            persistence.Data.Presets.Remove(deleted);
            persistence.Save();
            pendingDelete = null;
            CloseEditor();
            nextQuickDesignRefresh = DateTime.MinValue;
            notification = deletingDesign
                ? $"Deleted {deleted.Name} from WardrobeManager and Glamourer."
                : $"Deleted {deleted.Name}.";
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110f, 0f)))) { pendingDelete = null; TabletAppTheme.CloseCenteredModal(); }
        TabletAppTheme.EndCenteredModal();
    }

    private void DrawCreateFolderModal()
    {
        if (!TabletAppTheme.BeginCenteredModal("Create outfit folder")) return;
        ImGui.TextWrapped("Create a folder for organizing outfit presets.");
        ImGui.SetNextItemWidth(TabletAppTheme.Px(320f));
        ImGui.InputText("Folder name", ref newFolderName, 80);
        var valid = !string.IsNullOrWhiteSpace(newFolderName);
        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button("Create", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            var folderName = newFolderName.Trim();
            var glamourerPath = folderName.Replace('\\', '/').Trim('/').Trim();
            var folder = new WardrobeFolder { Name = folderName, GlamourerPath = glamourerPath };
            persistence.Data.Folders.Add(folder);
            persistence.Save();
            var createdInGlamourer = integrations.EnsureGlamourerFolder(glamourerPath, out var folderError);
            activeType = WardrobePresetType.Outfit;
            activeFolderId = folder.Id;
            newFolderName = string.Empty;
            notification = createdInGlamourer
                ? $"Created {folderName} in WardrobeManager. Glamourer will show the empty folder after its next reload, or immediately when an outfit is saved there."
                : $"Created {folderName} in WardrobeManager, but {folderError}";
            TabletAppTheme.CloseCenteredModal();
        }
        if (!valid) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            newFolderName = string.Empty;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private void DrawFolderRemovalConfirmation()
    {
        if (pendingFolderRemoval is null || !TabletAppTheme.BeginCenteredModal("Remove WardrobeManager folder?")) return;
        var count = persistence.Data.Presets.Count(preset => preset.FolderId == pendingFolderRemoval.Id);
        ImGui.TextWrapped($"Remove {pendingFolderRemoval.Name}? Its {count} outfit{(count == 1 ? string.Empty : "s")} will move to Unfiled and linked Glamourer designs will move to Glamourer's root. No outfits will be deleted. The empty Glamourer folder will disappear after Glamourer next reloads.");
        if (ImGui.Button("Remove Folder", TabletAppTheme.Px(new Vector2(145f, 0f))))
        {
            var removed = pendingFolderRemoval;
            var glamourerPath = string.IsNullOrWhiteSpace(removed.GlamourerPath)
                ? removed.Name.Replace('\\', '/').Trim('/').Trim()
                : removed.GlamourerPath;
            var folderPresets = persistence.Data.Presets.Where(preset => preset.FolderId == removed.Id).ToList();
            foreach (var preset in folderPresets.Where(preset => preset.Type == WardrobePresetType.Outfit && preset.GlamourerDesignId != Guid.Empty))
            {
                if (integrations.SyncOutfitToGlamourer(preset, string.Empty, out var syncMessage)) continue;
                notification = $"Folder removal stopped: {syncMessage}";
                TabletAppTheme.EndCenteredModal();
                return;
            }
            if (!integrations.RemovePersistedGlamourerFolder(glamourerPath, out var folderError))
            {
                notification = folderError;
                TabletAppTheme.EndCenteredModal();
                return;
            }
            foreach (var preset in folderPresets) preset.FolderId = Guid.Empty;
            persistence.Data.Folders.Remove(removed);
            persistence.Save();
            if (activeFolderId == removed.Id) activeFolderId = Guid.Empty;
            pendingFolderRemoval = null;
            notification = $"Removed folder {removed.Name}. Its outfits were moved to Unfiled and linked Glamourer designs were moved to Glamourer's root. Glamourer's empty folder record was removed and will disappear after Glamourer reloads; no outfits were deleted.";
            if (config.ReloadGlamourerAfterFolderDelete)
            {
                DalamudServices.CommandManager.ProcessCommand("/xldisableplugin \"Glamourer\"");
                glamourerEnablePending = true;
                glamourerEnableAt = DateTime.UtcNow.AddSeconds(1);
                notification = $"Removed folder {removed.Name}. Its outfits were moved to Unfiled and Glamourer is being reloaded to refresh its folder list.";
            }
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            pendingFolderRemoval = null;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private void DrawImageCleanupConfirmation()
    {
        if (!imageCleanupRequested || !TabletAppTheme.BeginCenteredModal("Delete unused WardrobeManager images?")) return;
        ImGui.TextWrapped("Permanently delete managed portrait files that no preset uses and older identifiable selfie captures when a newer capture exists for the same preset? Unknown files and each preset's newest exported selfie are kept.");
        if (ImGui.Button("Delete Unused", TabletAppTheme.Px(new Vector2(145f, 0f))))
        {
            try
            {
                var folder = string.IsNullOrWhiteSpace(config.SelfieFolder) ? DefaultSelfieFolder() : config.SelfieFolder;
                var result = persistence.DeleteUnusedImages(folder);
                notification = result.TotalDeleted == 0
                    ? "No unused WardrobeManager images were found."
                    : $"Deleted {result.TotalDeleted} unused image{(result.TotalDeleted == 1 ? string.Empty : "s")}.";
            }
            catch (Exception ex)
            {
                notification = "WardrobeManager could not delete unused images: " + ex.Message;
            }
            imageCleanupRequested = false;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            imageCleanupRequested = false;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private void DrawDevelopmentWarning()
    {
        if (config.LastAcknowledgedDevelopmentVersion.Equals(WardrobeManagerVersion, StringComparison.OrdinalIgnoreCase))
        {
            developmentWarningOpened = false;
            return;
        }
        if (!developmentWarningOpened)
        {
            developmentWarningOpened = true;
            TabletAppTheme.OpenCenteredModal(DevelopmentWarningModal);
        }
        if (!TabletAppTheme.BeginCenteredModal(DevelopmentWarningModal,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(420f));
        ImGui.TextWrapped("WardrobeManager is still in active development and may contain bugs. Review important Glamourer, Penumbra, Customize+, and Honorific changes after applying a preset, and keep backups of your plugin configurations while testing.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("Acknowledge", TabletAppTheme.Px(new Vector2(150f, 0f))))
        {
            config.LastAcknowledgedDevelopmentVersion = WardrobeManagerVersion;
            DalamudServices.PluginInterface.SavePluginConfig(config);
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.Dummy(new Vector2(0f, TabletAppTheme.Px(10f)));
        TabletAppTheme.EndCenteredModal();
    }

    private void RefreshMods() => availableMods = integrations.GetMods().ToList();
    private static string TypeLabel(WardrobePresetType type) => type switch { WardrobePresetType.Outfit => "Outfits", WardrobePresetType.Character => "Characters", _ => "Emotes" };

    private static void DrawPortrait(Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap texture, Vector2 size)
    {
        var sourceAspect = texture.Width / (float)Math.Max(1, texture.Height);
        const float targetAspect = 9f / 16f;
        var uv0 = Vector2.Zero;
        var uv1 = Vector2.One;
        if (sourceAspect > targetAspect)
        {
            var visibleWidth = targetAspect / sourceAspect;
            uv0.X = (1f - visibleWidth) * 0.5f;
            uv1.X = uv0.X + visibleWidth;
        }
        else if (sourceAspect < targetAspect)
        {
            var visibleHeight = sourceAspect / targetAspect;
            uv0.Y = (1f - visibleHeight) * 0.5f;
            uv1.Y = uv0.Y + visibleHeight;
        }
        ImGui.Image(texture.Handle, size, uv0, uv1);
    }

    private static void TextColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    private static void DrawCard(string title, float height, bool allowScroll, Action content, bool showHeader = true)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TabletAppTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.48f));
        var flags = ImGuiWindowFlags.NoSavedSettings;
        if (!allowScroll) flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.BeginChild($"##wardrobe-editor-card-{title}", new Vector2(-1f, height), true, flags);
        if (showHeader)
        {
            ImGui.TextColored(TabletAppTheme.AccentHover, title);
            ImGui.Separator();
        }
        content();
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    public void Dispose()
    {
        integrations.Dispose();
        selfieCamera.Dispose();
        imageDialog.Dispose();
        textures.Dispose();
    }
}

internal sealed class ManualHonorificDraft
{
    public string Title = string.Empty;
    public bool IsPrefix = true;
    public bool UseTextColor;
    public Vector3 TextColor = Vector3.One;
    public bool UseGlow;
    public Vector3 GlowColor;
    public int EffectPalette = -2;
    public WardrobeHonorificAnimation EffectAnimation = WardrobeHonorificAnimation.Static;
    public Vector3 EffectColor2 = Vector3.One;
    public WardrobeHonorificCondition Condition;
    public int ConditionParam;
    public uint TerritoryId;

    public static ManualHonorificDraft FromPreset(WardrobePreset preset) => new()
    {
        Title = preset.HonorificUsesExistingTitle ? string.Empty : preset.HonorificTitleName,
        IsPrefix = preset.HonorificCustomIsPrefix,
        UseTextColor = preset.HonorificUseColor,
        TextColor = new Vector3(preset.HonorificColorR, preset.HonorificColorG, preset.HonorificColorB),
        UseGlow = preset.HonorificUseGlow,
        GlowColor = new Vector3(preset.HonorificGlowR, preset.HonorificGlowG, preset.HonorificGlowB),
        EffectPalette = preset.HonorificEffectPalette,
        EffectAnimation = preset.HonorificEffectAnimation,
        EffectColor2 = new Vector3(preset.HonorificEffectColor2R, preset.HonorificEffectColor2G,
            preset.HonorificEffectColor2B),
        Condition = preset.HonorificCondition,
        ConditionParam = preset.HonorificConditionParam,
        TerritoryId = preset.HonorificTerritoryId,
    };

    public void ApplyTo(WardrobePreset preset, IReadOnlyList<HonorificTitle> knownTitles)
    {
        if (preset.HonorificUsesExistingTitle || string.IsNullOrWhiteSpace(preset.HonorificTitleId)
            || knownTitles.Any(title => title.Id.Equals(preset.HonorificTitleId, StringComparison.Ordinal)))
            preset.HonorificTitleId = "uid:wm" + preset.Id.ToString("N")[..12];
        preset.HonorificTitleConfigured = true;
        preset.HonorificUsesExistingTitle = false;
        preset.HonorificTitleName = Title.Trim();
        preset.HonorificCustomIsPrefix = IsPrefix;
        preset.HonorificUseColor = UseTextColor;
        preset.HonorificColorR = TextColor.X;
        preset.HonorificColorG = TextColor.Y;
        preset.HonorificColorB = TextColor.Z;
        preset.HonorificUseGlow = EffectPalette == -1 || UseGlow;
        preset.HonorificGlowR = GlowColor.X;
        preset.HonorificGlowG = GlowColor.Y;
        preset.HonorificGlowB = GlowColor.Z;
        preset.HonorificEffectPalette = EffectPalette;
        preset.HonorificEffectAnimation = EffectAnimation;
        preset.HonorificEffectColor2R = EffectColor2.X;
        preset.HonorificEffectColor2G = EffectColor2.Y;
        preset.HonorificEffectColor2B = EffectColor2.Z;
        preset.HonorificCondition = Condition;
        preset.HonorificConditionParam = ConditionParam;
        preset.HonorificTerritoryId = TerritoryId;
    }
}
