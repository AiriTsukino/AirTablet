using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using AirTablet.Services;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace MacroDeck;

internal sealed class Plugin : IDisposable
{
    private const int DeckSize = 32;
    private const int MaximumControlCenterPads = 18;
    private static readonly Regex InlineWaitRegex = new(@"<wait\.(?<seconds>\d+(?:\.\d+)?)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TextCommandRegex = new(@"^/(?:[A-Za-z][A-Za-z0-9]*|\?)(?:\s.*)?$", RegexOptions.Compiled);
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly DialogService dialogs = new();
    private readonly ChatCommandService chat = new();
    private readonly TextureCache textures = new();
    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly List<Guid> folderPath = [];
    private bool editMode;
    private bool editorOpen;
    private bool profilesOpen;
    private bool deleteConfirmationPending;
    private DeckEntry? editingEntry;
    private int editingSlot;
    private string editTitle = string.Empty;
    private string editImage = string.Empty;
    private string editScript = string.Empty;
    private string editorValidation = string.Empty;
    private DeckEntryKind editKind;
    private string newVenueName = "New Venue";
    private string profileName = string.Empty;
    private string status = "Ready";
    private string hoveredDeckKeyId = string.Empty;
    private double deckKeyHoverStartedAt;
    private int deckKeyHoverFrame;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        persistence = new PersistenceService(config, pluginInterface);
    }

    public void Draw()
    {
        var venue = persistence.ActiveVenue;
        var overlayMin = ImGui.GetCursorScreenPos();
        var overlaySize = ImGui.GetContentRegionAvail();
        DrawToolbar(venue);
        ImGui.Separator();
        DrawDeck(CurrentEntries(venue));
        if (editorOpen || profilesOpen)
            DrawContainedOverlay(venue, overlayMin, overlaySize);
    }

    public void Tick() { }

    public bool CanNavigateBack() => false;

    public bool NavigateBack()
    {
        return false;
    }

    public IReadOnlyList<ControlCenterWidget> GetControlCenterWidgets() =>
        Enumerable.Range(0, MaximumControlCenterPads)
            .Select(index => CreateMacroPadWidget(index == 0 ? "macrodeck.pad" : $"macrodeck.pad.{index + 1}"))
            .ToList();

    private ControlCenterWidget CreateMacroPadWidget(string padId) =>
        new(
            padId, "MacroDeck", "Macro pad",
            "Four quick macros from the active MacroDeck venue profile.",
            ControlCenterWidgetKind.MacroPad, ControlCenterWidgetSize.Compact,
            () => new(persistence.ActiveVenue.Name, "4 quick macro slots"),
            ReadMacroPad: () => ReadMacroPad(padId),
            ActivateMacro: id =>
            {
                var macro = FlattenMacros(persistence.ActiveVenue.Buttons).FirstOrDefault(entry => entry.Id.ToString().Equals(id, StringComparison.OrdinalIgnoreCase));
                if (macro is not null) _ = ExecuteMacroAsync(macro);
            },
            AssignMacro: (slot, id) =>
            {
                var venue = persistence.ActiveVenue;
                if (slot is < 0 or >= 4) return;
                var slots = GetPadSlots(venue, padId);
                if (Guid.TryParse(id, out var parsed) && venue.ControlCenterPads
                    .SelectMany(pair => pair.Value.Select((value, index) => (pair.Key, Index: index, Value: value)))
                    .Any(assignment => assignment.Value == parsed &&
                        (!assignment.Key.Equals(padId, StringComparison.OrdinalIgnoreCase) || assignment.Index != slot)))
                    return;
                slots[slot] = Guid.TryParse(id, out parsed) ? parsed : null;
                persistence.SaveNow();
            },
            RepeatableGroup: "macrodeck.pad",
            Removed: () =>
            {
                foreach (var venue in persistence.Venues)
                    venue.ControlCenterPads.Remove(padId);
                persistence.SaveNow();
            });

    private ControlCenterMacroPadSnapshot ReadMacroPad(string padId)
    {
        var venue = persistence.ActiveVenue;
        var macros = FlattenMacros(venue.Buttons).ToList();
        var byId = macros.ToDictionary(entry => entry.Id);
        var padSlots = GetPadSlots(venue, padId);
        var slots = padSlots.Take(4).Select(id =>
            id is { } value && byId.TryGetValue(value, out var entry)
                ? new ControlCenterMacroButton(entry.Id.ToString(), entry.Title, entry.ImagePath)
                : null).ToList();
        var assigned = venue.ControlCenterPads.Values.SelectMany(values => values).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        var available = macros.Where(entry => !assigned.Contains(entry.Id)).Select(entry => new ControlCenterMacroButton(entry.Id.ToString(), entry.Title, entry.ImagePath)).ToList();
        return new(slots, available);
    }

    private static List<Guid?> GetPadSlots(VenueProfile venue, string padId)
    {
        if (!venue.ControlCenterPads.TryGetValue(padId, out var slots))
        {
            slots = [null, null, null, null];
            venue.ControlCenterPads[padId] = slots;
        }
        return slots;
    }

    private void DrawToolbar(VenueProfile venue)
    {
        ImGui.SetNextItemWidth(TabletAppTheme.Px(210f));
        if (ImGui.BeginCombo("##macrodeck-venue", venue.Name))
        {
            foreach (var candidate in persistence.Venues)
            {
                if (ImGui.Selectable(candidate.Name, candidate.Id == venue.Id))
                {
                    config.ActiveVenueId = candidate.Id;
                    folderPath.Clear();
                    persistence.SaveNow();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Profiles", TabletAppTheme.Px(new Vector2(94, 0))))
        {
            profileName = venue.Name;
            profilesOpen = true;
        }
        ImGui.SameLine();
        if (ImGui.Button(editMode ? "Finish Editing" : "Edit Deck", TabletAppTheme.Px(new Vector2(118, 0)))) editMode = !editMode;
        ImGui.SameLine();
        ImGui.TextColored(TabletAppTheme.MutedText, editMode ? "Click a key to configure it" : status);
    }

    private void DrawDeck(List<DeckEntry> entries)
    {
        const int columns = 8;
        var gap = TabletAppTheme.Px(7f);
        var available = ImGui.GetContentRegionAvail();
        var width = MathF.Max(TabletAppTheme.Px(64f), (available.X - gap * (columns - 1)) / columns);
        var height = MathF.Max(TabletAppTheme.Px(68f), (available.Y - gap * 3f - TabletAppTheme.Px(8f)) / 4f);
        for (var slot = 0; slot < DeckSize; slot++)
        {
            if (slot % columns != 0) ImGui.SameLine(0, gap);
            if (slot == 0 && folderPath.Count > 0)
                DrawNavigationKey(new Vector2(width, height));
            else
                DrawDeckButton(entries, entries.FirstOrDefault(candidate => candidate.Slot == slot), slot, new Vector2(width, height));
        }
    }

    private void DrawNavigationKey(Vector2 size)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        var accent = TabletAppTheme.Accent;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(accent.X * 0.30f, accent.Y * 0.30f, accent.Z * 0.30f, 0.98f)), TabletAppTheme.Px(10f));
        draw.AddRect(min, max, ImGui.GetColorU32(hovered ? TabletAppTheme.AccentHover : new Vector4(accent.X, accent.Y, accent.Z, 0.72f)), TabletAppTheme.Px(10f), ImDrawFlags.None, TabletAppTheme.Px(1.5f));
        var center = min + size * 0.5f;
        if (folderPath.Count == 1)
        {
            var roofColor = ImGui.GetColorU32(TabletAppTheme.AccentHover);
            draw.AddTriangleFilled(center - TabletAppTheme.Px(new Vector2(15, 3)), center - TabletAppTheme.Px(new Vector2(0, 15)), center + TabletAppTheme.Px(new Vector2(15, -3)), roofColor);
            draw.AddRectFilled(center - TabletAppTheme.Px(new Vector2(11, 2)), center + TabletAppTheme.Px(new Vector2(11, 13)), roofColor, TabletAppTheme.Px(2f));
        }
        else
        {
            var color = ImGui.GetColorU32(TabletAppTheme.AccentHover);
            draw.AddLine(center + TabletAppTheme.Px(new Vector2(12, -10)), center - TabletAppTheme.Px(new Vector2(9, 0)), color, TabletAppTheme.Px(3f));
            draw.AddLine(center - TabletAppTheme.Px(new Vector2(9, 0)), center + TabletAppTheme.Px(new Vector2(12, 10)), color, TabletAppTheme.Px(3f));
        }
        var label = folderPath.Count == 1 ? "Home" : "Back";
        var labelSize = ImGui.CalcTextSize(label);
        draw.AddText(new Vector2(center.X - labelSize.X * 0.5f, max.Y - TabletAppTheme.Px(19f)), ImGui.GetColorU32(TabletAppTheme.Text), label);
        ImGui.InvisibleButton("##macrodeck-protected-navigation-key", size);
        if (ImGui.IsItemClicked())
        {
            if (folderPath.Count == 1) folderPath.Clear();
            else if (folderPath.Count > 1) folderPath.RemoveAt(folderPath.Count - 1);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(folderPath.Count == 1 ? "Return to deck home. This protected key cannot be edited." : "Return to the previous folder. This protected key cannot be edited.");
            ImGui.EndTooltip();
        }
    }

    private void DrawDeckButton(List<DeckEntry> entries, DeckEntry? entry, int slot, Vector2 size)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        var accent = TabletAppTheme.Accent;
        var fill = entry is null ? new Vector4(0.10f, 0.105f, 0.14f, hovered ? 0.90f : 0.62f) : new Vector4(0.16f + accent.X * 0.12f, 0.16f + accent.Y * 0.12f, 0.20f + accent.Z * 0.12f, 0.98f);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), TabletAppTheme.Px(10f));
        draw.AddRect(min, max, ImGui.GetColorU32(hovered ? TabletAppTheme.AccentHover : new Vector4(accent.X, accent.Y, accent.Z, entry is null ? 0.20f : 0.65f)), TabletAppTheme.Px(10f), ImDrawFlags.None, TabletAppTheme.Px(1.4f));
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.ImagePath))
        {
            var texture = textures.GetResourceIcon($"macrodeck-{entry.Id}", entry.ImagePath);
            if (texture is not null)
            {
                var imageBoxMin = min + TabletAppTheme.Px(new Vector2(4, 4));
                var imageBoxMax = max - TabletAppTheme.Px(new Vector2(4, 22));
                var imageBoxSize = Vector2.Max(Vector2.One, imageBoxMax - imageBoxMin);
                var sourceSize = new Vector2(Math.Max(1, texture.Width), Math.Max(1, texture.Height));
                var scale = MathF.Min(imageBoxSize.X / sourceSize.X, imageBoxSize.Y / sourceSize.Y);
                var imageSize = sourceSize * scale;
                var imageMin = imageBoxMin + (imageBoxSize - imageSize) * 0.5f;
                draw.AddImage(texture.Handle, imageMin, imageMin + imageSize);
            }
        }
        if (entry?.Kind == DeckEntryKind.Folder)
        {
            var folderMin = min + TabletAppTheme.Px(new Vector2(9, 10));
            draw.AddRectFilled(folderMin, folderMin + TabletAppTheme.Px(new Vector2(24, 16)), ImGui.GetColorU32(TabletAppTheme.AccentHover), TabletAppTheme.Px(3f));
            draw.AddRectFilled(folderMin + TabletAppTheme.Px(new Vector2(2, -4)), folderMin + TabletAppTheme.Px(new Vector2(13, 2)), ImGui.GetColorU32(TabletAppTheme.AccentHover), TabletAppTheme.Px(2f));
        }
        var label = entry?.Title ?? (editMode ? "+" : string.Empty);
        DrawDeckKeyTitle(draw, label, min, size, hovered, entry?.Id.ToString() ?? $"empty-{slot}", ImGui.GetColorU32(entry is null ? TabletAppTheme.MutedText : TabletAppTheme.Text));
        ImGui.InvisibleButton($"##macrodeck-slot-{slot}", size);
        var clicked = ImGui.IsItemClicked();
        var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (entry is null && clicked && editMode) OpenEditor(null, slot);
        else if (entry is not null && (rightClicked || clicked && editMode)) OpenEditor(entry, slot);
        else if (entry?.Kind == DeckEntryKind.Folder && clicked) folderPath.Add(entry.Id);
        else if (entry is not null && clicked) _ = ExecuteMacroAsync(entry);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(entry is null ? (editMode ? "Create a macro or folder" : "Empty key") : entry.Kind == DeckEntryKind.Folder ? "Open folder; right-click to edit" : editMode ? "Edit macro" : $"Run {entry.Title}; right-click to edit");
            ImGui.EndTooltip();
        }
    }

    private void OpenEditor(DeckEntry? entry, int slot)
    {
        editingEntry = entry; editingSlot = slot; editKind = entry?.Kind ?? DeckEntryKind.Macro;
        editTitle = entry?.Title ?? "New Macro"; editImage = entry?.ImagePath ?? string.Empty;
        editScript = entry?.Script ?? string.Empty; editorValidation = string.Empty;
        deleteConfirmationPending = false; editorOpen = true;
    }

    private void DrawDeckKeyTitle(ImDrawListPtr draw, string text, Vector2 min, Vector2 size, bool hovered, string hoverId, uint color)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var padding = TabletAppTheme.Px(5f);
        var availableWidth = MathF.Max(1f, size.X - padding * 2f);
        var textSize = ImGui.CalcTextSize(text);
        var y = min.Y + size.Y - TabletAppTheme.Px(19f);
        if (textSize.X <= availableWidth)
        {
            draw.AddText(new Vector2(min.X + (size.X - textSize.X) * 0.5f, y), color, text);
            return;
        }

        var frame = ImGui.GetFrameCount();
        if (hovered)
        {
            if (!hoveredDeckKeyId.Equals(hoverId, StringComparison.Ordinal) || deckKeyHoverFrame < frame - 1)
            {
                hoveredDeckKeyId = hoverId;
                deckKeyHoverStartedAt = ImGui.GetTime();
            }
            deckKeyHoverFrame = frame;
        }
        var gap = MathF.Max(TabletAppTheme.Px(34f), availableWidth * 0.28f);
        var loopDistance = textSize.X + gap;
        var travelSeconds = Math.Max(0.85d, loopDistance / Math.Max(1f, TabletAppTheme.Px(25f)));
        var pauseSeconds = 0.18d;
        var phase = Math.Max(0d, ImGui.GetTime() - deckKeyHoverStartedAt) % (pauseSeconds + travelSeconds);
        var offset = hovered && phase >= pauseSeconds
            ? loopDistance * (float)((phase - pauseSeconds) / travelSeconds)
            : 0f;
        var clipMin = new Vector2(min.X + padding, y);
        var clipMax = new Vector2(min.X + size.X - padding, y + ImGui.GetTextLineHeight());
        draw.PushClipRect(clipMin, clipMax, true);
        draw.AddText(clipMin - new Vector2(offset, 0), color, text);
        if (hovered) draw.AddText(clipMin + new Vector2(loopDistance - offset, 0), color, text);
        draw.PopClipRect();
    }

    private void DrawContainedOverlay(VenueProfile venue, Vector2 overlayMin, Vector2 overlaySize)
    {
        var returnCursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(overlayMin);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.026f, 0.045f, 0.96f));
        var open = ImGui.BeginChild(
            "##macrodeck-contained-overlay",
            overlaySize,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (open)
        {
            var requested = deleteConfirmationPending
                ? TabletAppTheme.Px(new Vector2(440, 180))
                : editorOpen
                    ? TabletAppTheme.Px(new Vector2(620, 500))
                    : TabletAppTheme.Px(new Vector2(510, 330));
            var panelSize = Vector2.Min(requested, overlaySize - TabletAppTheme.Px(new Vector2(32, 32)));
            panelSize = Vector2.Max(panelSize, TabletAppTheme.Px(new Vector2(320, 160)));
            ImGui.SetCursorPos((overlaySize - panelSize) * 0.5f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, TabletAppTheme.Px(new Vector2(20, 18)));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, TabletAppTheme.Px(18f));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.075f, 0.078f, 0.115f, 0.995f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.AccentHover.X, TabletAppTheme.AccentHover.Y, TabletAppTheme.AccentHover.Z, 0.55f));
            if (ImGui.BeginChild(
                    "##macrodeck-contained-panel",
                    panelSize,
                    true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                if (deleteConfirmationPending)
                    DrawDeleteConfirmation(venue);
                else if (editorOpen)
                    DrawEditor(venue);
                else
                    DrawProfiles(venue);
            }
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(2);
        }
        ImGui.EndChild();
        ImGui.SetCursorScreenPos(returnCursor);
    }

    private void DrawEditor(VenueProfile venue)
    {
        ImGui.TextUnformatted(editingEntry is null ? "Create MacroDeck key" : "Edit MacroDeck key");
        ImGui.Separator();
        ImGui.InputText("Title", ref editTitle, DeckEntry.MaxTitleLength);
        var kindIndex = (int)editKind;
        if (ImGui.Combo("Key type", ref kindIndex, "Macro\0Folder\0")) editKind = (DeckEntryKind)kindIndex;
        ImGui.TextUnformatted("Image");
        ImGui.SetNextItemWidth(MathF.Max(TabletAppTheme.Px(160f), ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(92f)));
        ImGui.InputText("##macrodeck-image-path", ref editImage, 520);
        ImGui.SameLine();
        if (ImGui.Button("Browse##macrodeck-image")) dialogs.PickImage(path => editImage = path);
        if (editKind == DeckEntryKind.Macro)
        {
            ImGui.TextColored(TabletAppTheme.MutedText, "Macro script — one command per line");
            var lineCount = Math.Clamp(editScript.Count(character => character == '\n') + 2, 3, 10);
            var desiredHeight = TabletAppTheme.Px(28f + lineCount * 18f);
            var reservedHeight = TabletAppTheme.Px(string.IsNullOrWhiteSpace(editorValidation) ? 140f : 162f);
            var maximumHeight = MathF.Max(TabletAppTheme.Px(72f), ImGui.GetContentRegionAvail().Y - reservedHeight);
            ImGui.InputTextMultiline("##macrodeck-script", ref editScript, 4000, new Vector2(-1, MathF.Min(desiredHeight, maximumHeight)));
            ImGui.PushStyleColor(ImGuiCol.Text, TabletAppTheme.MutedText);
            ImGui.TextWrapped("Supports FFXIV text commands such as /random, /dice, /action, /mount, chat, emotes, and more.");
            ImGui.TextWrapped("Use /wait 1 or <wait.1> between commands. FFXIV validates command arguments and availability.");
            ImGui.PopStyleColor();
            if (!string.IsNullOrWhiteSpace(editorValidation))
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), editorValidation);
        }
        if (ImGui.Button("Save", TabletAppTheme.Px(new Vector2(100, 0))))
        {
            var valid = editKind != DeckEntryKind.Macro || TryParseMacroScript(editScript, out _, out editorValidation);
            if (valid)
            {
                var entries = CurrentEntries(venue);
                var entry = editingEntry ?? new DeckEntry { Slot = editingSlot };
                entry.Kind = editKind; entry.Title = DeckEntry.NormalizeTitle(editTitle, editKind == DeckEntryKind.Folder ? "Folder" : "Macro");
                entry.ImagePath = editImage.Trim(); entry.Script = editScript.Trim(); entry.Message = string.Empty; entry.EmoteCommand = string.Empty;
                if (entry.Kind == DeckEntryKind.Macro) entry.Children.Clear();
                if (editingEntry is null) entries.Add(entry);
                persistence.SaveNow(); editorOpen = false;
            }
        }
        ImGui.SameLine();
        if (editingEntry is not null && ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(100, 0)))) deleteConfirmationPending = true;
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(100, 0)))) editorOpen = false;
    }

    private void DrawDeleteConfirmation(VenueProfile venue)
    {
        if (editingEntry is null)
        {
            deleteConfirmationPending = false;
            return;
        }
        ImGui.TextUnformatted("Delete this key?");
        ImGui.Separator();
        ImGui.TextWrapped(editingEntry.Kind == DeckEntryKind.Folder
            ? $"Delete folder '{editingEntry.Title}' and every key inside it? This cannot be undone."
            : $"Delete macro '{editingEntry.Title}'? This cannot be undone.");
        ImGui.Spacing();
        if (ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(110, 0))))
        {
            CurrentEntries(venue).Remove(editingEntry);
            persistence.SaveNow();
            deleteConfirmationPending = false;
            editorOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110, 0))))
            deleteConfirmationPending = false;
    }

    private void DrawProfiles(VenueProfile venue)
    {
        ImGui.TextUnformatted("MacroDeck venue profiles");
        ImGui.Separator();
        ImGui.InputText("Active profile name", ref profileName, 80);
        if (ImGui.Button("Rename")) { venue.Name = string.IsNullOrWhiteSpace(profileName) ? venue.Name : profileName.Trim(); persistence.SaveNow(); }
        ImGui.InputText("New profile", ref newVenueName, 80);
        if (ImGui.Button("Create Venue")) { persistence.AddVenue(newVenueName); folderPath.Clear(); profileName = persistence.ActiveVenue.Name; }
        ImGui.SameLine();
        if (persistence.Venues.Count <= 1) ImGui.BeginDisabled();
        if (ImGui.Button("Delete Active")) { persistence.DeleteVenue(venue.Id); folderPath.Clear(); profileName = persistence.ActiveVenue.Name; }
        if (persistence.Venues.Count <= 1) ImGui.EndDisabled();
        ImGui.Separator();
        if (ImGui.Button("Export Active")) dialogs.SaveProfile(venue.Name, path => { try { persistence.ExportVenue(venue, path); status = "Profile exported"; } catch (Exception ex) { status = ex.Message; } });
        ImGui.SameLine();
        if (ImGui.Button("Import Profile")) dialogs.ImportProfile(path => { try { persistence.ImportVenue(path); folderPath.Clear(); status = "Profile imported"; } catch (Exception ex) { status = ex.Message; } });
        ImGui.Separator();
        if (ImGui.Button("Close", TabletAppTheme.Px(new Vector2(100, 0)))) profilesOpen = false;
    }

    private async Task ExecuteMacroAsync(DeckEntry macro)
    {
        if (macro.Kind != DeckEntryKind.Macro) return;
        await executionGate.WaitAsync();
        try
        {
        if (!TryParseMacroScript(macro.Script, out var steps, out var issue))
        {
            status = issue;
            AirTablet.DalamudServices.ChatGui.PrintError($"MacroDeck: {issue}");
            return;
        }
        foreach (var step in steps)
        {
            if (step.Command is not null && !await chat.SendAsync(step.Command)) { status = chat.LastError; return; }
            if (step.DelaySeconds > 0d) await Task.Delay(TimeSpan.FromSeconds(step.DelaySeconds));
        }
        status = $"Ran {macro.Title}";
        }
        finally
        {
            executionGate.Release();
        }
    }

    private static bool TryParseMacroScript(string script, out List<MacroStep> steps, out string issue)
    {
        steps = [];
        issue = string.Empty;
        var lineNumber = 0;
        foreach (var raw in (script ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            var original = raw.Trim();
            if (string.IsNullOrWhiteSpace(original)) continue;
            var matches = InlineWaitRegex.Matches(original);
            if (matches.Count > 1) { issue = $"Line {lineNumber}: use only one inline wait."; return false; }
            double? inlineWait = null;
            if (matches.Count == 1)
            {
                if (!double.TryParse(matches[0].Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 60)
                { issue = $"Line {lineNumber}: wait values must be between 0 and 60 seconds."; return false; }
                inlineWait = parsed;
            }
            else if (original.Contains("<wait", StringComparison.OrdinalIgnoreCase))
            { issue = $"Line {lineNumber}: inline wait was not recognized. Use <wait.1>."; return false; }

            var line = InlineWaitRegex.Replace(original, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (inlineWait is not null) steps.Add(new(null, inlineWait.Value));
                continue;
            }
            if (TryParseWaitCommand(line, out var waitSeconds))
            {
                steps.Add(new(null, waitSeconds));
                if (inlineWait is not null) steps.Add(new(null, inlineWait.Value));
                continue;
            }
            if (LooksLikeWaitCommand(line))
            {
                issue = $"Line {lineNumber}: wait syntax was not recognized. Use /wait 1, /wait.1, or /wait1 with a value from 0 to 60.";
                return false;
            }
            if (!IsSupportedMacroCommand(line))
            {
                issue = $"Line {lineNumber}: start the line with a valid FFXIV text command, such as /random, /action, /say, or an emote.";
                return false;
            }
            steps.Add(new(line, inlineWait ?? 0.25d));
        }
        if (steps.Count == 0) { issue = "Add at least one command to the macro script."; return false; }
        return true;
    }

    private static bool IsSupportedMacroCommand(string line) => TextCommandRegex.IsMatch(line);

    private static bool LooksLikeWaitCommand(string line) =>
        line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase) &&
        (line.Length == 5 || char.IsWhiteSpace(line[5]) || line[5] == '.' || char.IsDigit(line[5]));

    private static bool TryParseWaitCommand(string line, out double seconds)
    {
        seconds = 1d;
        if (!line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = line[5..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] != '.' && !char.IsDigit(rest[0])) return false;
        rest = rest.Trim();
        if (rest.StartsWith(".", StringComparison.Ordinal)) rest = rest[1..].Trim();
        if (string.IsNullOrWhiteSpace(rest)) return true;
        var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 1 || !double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 60) return false;
        seconds = parsed;
        return true;
    }

    private sealed record MacroStep(string? Command, double DelaySeconds);

    private List<DeckEntry> CurrentEntries(VenueProfile venue)
    {
        var entries = venue.Buttons;
        foreach (var id in folderPath)
        {
            var folder = entries.FirstOrDefault(entry => entry.Id == id && entry.Kind == DeckEntryKind.Folder);
            if (folder is null) { folderPath.Clear(); return venue.Buttons; }
            entries = folder.Children;
        }
        return entries;
    }

    private static IEnumerable<DeckEntry> FlattenMacros(IEnumerable<DeckEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Kind == DeckEntryKind.Macro) yield return entry;
            else foreach (var child in FlattenMacros(entry.Children)) yield return child;
        }
    }

    public void Dispose()
    {
        persistence.SaveNow(); dialogs.Dispose(); textures.Dispose(); executionGate.Dispose();
    }
}
