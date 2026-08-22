using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using AirTablet.Services;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;

namespace MacroDeck;

internal sealed class Plugin : IDisposable
{
    private readonly record struct FolderActionOverlay(
        List<DeckEntry> Entries,
        DeckEntry Folder,
        Vector2 KeyMin,
        Vector2 KeySize,
        bool DragMode);

    private const int DeckSize = 32;
    private const int MaximumControlCenterPads = 18;
    private static readonly Regex InlineWaitRegex = new(@"<wait\.(?<seconds>\d+(?:\.\d+)?)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TextCommandRegex = new(@"^/(?:[A-Za-z][A-Za-z0-9]*|\?)(?:\s.*)?$", RegexOptions.Compiled);
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly DialogService dialogs = new();
    private readonly ChatCommandService chat = new();
    private readonly TextureCache textures = new();
    private readonly MacroIconCatalog macroIcons;
    private readonly PopoutDeckOverlay popout;
    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly List<Guid> folderPath = [];
    private bool editMode;
    private bool editorOpen;
    private bool profilesOpen;
    private bool settingsOpen;
    private bool iconPickerOpen;
    private bool deleteConfirmationPending;
    private DeckEntry? editingEntry;
    private int editingSlot;
    private string editTitle = string.Empty;
    private string editImage = string.Empty;
    private int editGameIconId;
    private int iconPickerPage;
    private int iconCategoryIndex;
    private string iconSearch = string.Empty;
    private bool forceIconCategorySelection;
    private string editScript = string.Empty;
    private string editorValidation = string.Empty;
    private DeckEntryKind editKind;
    private string newVenueName = "New Venue";
    private string profileName = string.Empty;
    private string status = "Ready";
    private string hoveredDeckKeyId = string.Empty;
    private double deckKeyHoverStartedAt;
    private int deckKeyHoverFrame;
    private Guid? draggedDeckEntryId;
    private Guid? dragFolderTargetId;
    private Guid? folderActionMenuId;
    private string? pendingNotification;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        persistence = new PersistenceService(config, pluginInterface);
        macroIcons = new MacroIconCatalog();
        popout = new PopoutDeckOverlay(config, persistence, macroIcons, textures, ExecuteMacroAsync);
        config.Version = Math.Max(config.Version, 3);
        config.PopoutScale = Math.Clamp(config.PopoutScale, 0.65f, 1.50f);
        persistence.SaveNow();
    }

    public void Draw()
    {
        if (settingsOpen)
        {
            DrawSettingsScreen();
            return;
        }

        var venue = persistence.ActiveVenue;
        var overlayMin = ImGui.GetCursorScreenPos();
        var overlaySize = ImGui.GetContentRegionAvail();
        DrawToolbar(venue);
        ImGui.Separator();
        ImGui.SetCursorPosY(MathF.Max(
            0f,
            ImGui.GetCursorPosY() - TabletAppTheme.Px(4f)));
        DrawDeck(venue, CurrentEntries(venue));
        if (editorOpen || profilesOpen)
            DrawContainedOverlay(venue, overlayMin, overlaySize);
    }

    public void Tick() => popout.Draw();

    public bool CanNavigateBack() => editorOpen || profilesOpen || settingsOpen;

    public bool NavigateBack()
    {
        if (folderActionMenuId is not null)
        {
            folderActionMenuId = null;
            return true;
        }
        if (iconPickerOpen)
        {
            iconPickerOpen = false;
            return true;
        }
        if (editorOpen || profilesOpen || settingsOpen)
        {
            editorOpen = false;
            profilesOpen = false;
            settingsOpen = false;
            deleteConfirmationPending = false;
            return true;
        }
        return false;
    }

    public string? ConsumeNotification()
    {
        var notification = pendingNotification;
        pendingNotification = null;
        return notification;
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
        if (!ImGui.BeginTable("##macrodeck-toolbar", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            return;
        ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(180f));
        ImGui.TableSetupColumn("Popout", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(76f));
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(210f));
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(82f));
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##macrodeck-venue", venue.Name))
        {
            foreach (var candidate in persistence.Venues)
            {
                if (ImGui.Selectable(candidate.Name, candidate.Id == venue.Id))
                {
                    config.ActiveVenueId = candidate.Id;
                    folderPath.Clear();
                    popout.ResetFolder();
                    folderActionMenuId = null;
                    persistence.SaveNow();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.TableNextColumn();
        var popoutEnabled = config.PopoutEnabled;
        if (ImGui.Checkbox("Popout", ref popoutEnabled))
        {
            config.PopoutEnabled = popoutEnabled;
            persistence.SaveNow();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(260f));
            ImGui.TextWrapped("Show or hide the detachable MacroDeck quick-access deck.");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        ImGui.TableNextColumn();
        if (ImGui.Button("Profiles", TabletAppTheme.Px(new Vector2(88, 0))))
        {
            profileName = venue.Name;
            profilesOpen = true;
            editorOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button(editMode ? "Finish Editing" : "Edit Deck", TabletAppTheme.Px(new Vector2(112, 0))))
        {
            editMode = !editMode;
            folderActionMenuId = null;
            dragFolderTargetId = null;
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(TabletAppTheme.MutedText, editMode ? "Click a key to configure it" : status);

        ImGui.TableNextColumn();
        if (ImGui.Button("Settings", new Vector2(-1f, 0f)))
        {
            settingsOpen = true;
            profilesOpen = false;
            editorOpen = false;
        }
        ImGui.EndTable();
    }

    private void DrawDeck(VenueProfile venue, List<DeckEntry> entries)
    {
        if (ImGui.GetDragDropPayload().IsNull)
        {
            draggedDeckEntryId = null;
            dragFolderTargetId = null;
        }
        const int columns = 8;
        var gap = TabletAppTheme.Px(7f);
        var available = ImGui.GetContentRegionAvail();
        var width = MathF.Max(TabletAppTheme.Px(64f), (available.X - gap * (columns - 1)) / columns);
        var verticalGap = ImGui.GetStyle().ItemSpacing.Y;
        var bottomSafety = TabletAppTheme.Px(18f);
        var height = MathF.Max(
            TabletAppTheme.Px(68f),
            (available.Y - verticalGap * 3f - bottomSafety) / 4f);
        var actionOverlays = new List<FolderActionOverlay>(2);
        for (var slot = 0; slot < DeckSize; slot++)
        {
            if (slot % columns != 0) ImGui.SameLine(0, gap);
            if (slot == 0 && folderPath.Count > 0)
                DrawNavigationKey(venue, entries, new Vector2(width, height));
            else
                DrawDeckButton(
                    entries,
                    entries.FirstOrDefault(candidate => candidate.Slot == slot),
                    slot,
                    new Vector2(width, height),
                    actionOverlays);
        }

        // Submit overlay interactions only after the complete deck grid has been laid out.
        // Absolute-positioned items submitted between keys alter ImGui's SameLine state and
        // make later keys appear resized or displaced even when their rectangles do not overlap.
        var deckEndCursor = ImGui.GetCursorScreenPos();
        foreach (var overlay in actionOverlays)
        {
            DrawFolderActionChoices(
                overlay.Entries,
                overlay.Folder,
                overlay.KeyMin,
                overlay.KeySize,
                overlay.DragMode);
        }
        ImGui.SetCursorScreenPos(deckEndCursor);
    }

    private void DrawNavigationKey(VenueProfile venue, List<DeckEntry> currentEntries, Vector2 size)
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
        var baseHovered = ImGui.IsItemHovered();
        var moveOutPreview = false;
        var parentEntries = GetParentEntries(venue);
        var parentSlot = parentEntries is null
            ? null
            : FindFirstAvailableSlot(parentEntries, folderPath.Count > 1 ? 1 : 0);
        if (editMode && ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("MACRODECK_KEY", ImGuiDragDropFlags.AcceptBeforeDelivery);
            moveOutPreview = !payload.IsNull;
            if (!payload.IsNull && payload.IsDelivery() && draggedDeckEntryId is { } draggedId)
            {
                var source = currentEntries.FirstOrDefault(candidate => candidate.Id == draggedId);
                if (source is not null && parentEntries is not null && parentSlot.HasValue)
                {
                    currentEntries.Remove(source);
                    source.Slot = parentSlot.Value;
                    parentEntries.Add(source);
                    status = $"Moved {source.Title} out one folder";
                    draggedDeckEntryId = null;
                    dragFolderTargetId = null;
                    folderActionMenuId = null;
                    persistence.SaveNow();
                }
                else if (source is not null && !parentSlot.HasValue)
                {
                    status = "The parent page is full";
                    CancelFullDestinationDrop("The destination page is full and has no space for another key.");
                }
            }
            ImGui.EndDragDropTarget();
        }
        if (moveOutPreview)
        {
            var actionLabel = parentSlot.HasValue ? "Move Out" : "Parent Full";
            var actionSize = ImGui.CalcTextSize(actionLabel) + TabletAppTheme.Px(new Vector2(24f, 12f));
            var actionMin = min + (size - actionSize) * 0.5f;
            draw.AddRectFilled(actionMin, actionMin + actionSize, ImGui.GetColorU32(parentSlot.HasValue
                ? new Vector4(accent.X * 0.75f, accent.Y * 0.75f, accent.Z * 0.75f, 0.98f)
                : new Vector4(0.20f, 0.18f, 0.20f, 0.98f)), TabletAppTheme.Px(7f));
            draw.AddRect(actionMin, actionMin + actionSize, ImGui.GetColorU32(parentSlot.HasValue ? TabletAppTheme.AccentHover : TabletAppTheme.MutedText), TabletAppTheme.Px(7f), ImDrawFlags.None, TabletAppTheme.Px(1.2f));
            draw.AddText(actionMin + (actionSize - ImGui.CalcTextSize(actionLabel)) * 0.5f, ImGui.GetColorU32(TabletAppTheme.Text), actionLabel);
        }
        if (ImGui.IsItemClicked())
        {
            folderActionMenuId = null;
            dragFolderTargetId = null;
            if (folderPath.Count == 1) folderPath.Clear();
            else if (folderPath.Count > 1) folderPath.RemoveAt(folderPath.Count - 1);
        }
        if (baseHovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(moveOutPreview
                ? parentSlot.HasValue
                    ? "Drop to move this key out one folder level. The protected navigation key remains in place."
                    : "The parent page has no available configurable slots."
                : folderPath.Count == 1
                    ? "Return to deck home. In Edit Deck mode, drag a key here to move it out to the root deck."
                    : "Return to the previous folder. In Edit Deck mode, drag a key here to move it out one level.");
            ImGui.EndTooltip();
        }
    }

    private void DrawDeckButton(
        List<DeckEntry> entries,
        DeckEntry? entry,
        int slot,
        Vector2 size,
        List<FolderActionOverlay> actionOverlays)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        var accent = TabletAppTheme.Accent;
        var fill = entry is null ? new Vector4(0.10f, 0.105f, 0.14f, hovered ? 0.90f : 0.62f) : new Vector4(0.16f + accent.X * 0.12f, 0.16f + accent.Y * 0.12f, 0.20f + accent.Z * 0.12f, 0.98f);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), TabletAppTheme.Px(10f));
        draw.AddRect(min, max, ImGui.GetColorU32(hovered ? TabletAppTheme.AccentHover : new Vector4(accent.X, accent.Y, accent.Z, entry is null ? 0.20f : 0.65f)), TabletAppTheme.Px(10f), ImDrawFlags.None, TabletAppTheme.Px(1.4f));
        if (entry is not null && entry.Kind == DeckEntryKind.Macro)
        {
            var texture = ResolveKeyArtwork(entry, true);
            if (texture is not null)
            {
                var imageBoxMin = min + TabletAppTheme.Px(new Vector2(4, 4));
                var imageBoxMax = max - TabletAppTheme.Px(new Vector2(4, 22));
                DrawFittedImage(draw, texture, imageBoxMin, imageBoxMax);
            }
        }
        if (entry?.Kind == DeckEntryKind.Folder)
            DrawFolderKey(draw, entry, min, size);
        var label = entry?.Title ?? (editMode ? "+" : string.Empty);
        DrawDeckKeyTitle(draw, label, min, size, hovered, entry?.Id.ToString() ?? $"empty-{slot}", ImGui.GetColorU32(entry is null ? TabletAppTheme.MutedText : TabletAppTheme.Text));
        var folderChoicesWereVisible = entry?.Kind == DeckEntryKind.Folder &&
                                       (dragFolderTargetId == entry.Id || folderActionMenuId == entry.Id);
        // Keep the normal deck item in the layout while the folder choices are visible.
        // Replacing it with extra layout items changes the row bounds and can create a
        // scrollbar. The choices are drawn later as overlapping controls inside this item.
        ImGui.InvisibleButton($"##macrodeck-slot-{slot}", size);
        if (folderChoicesWereVisible)
            ImGui.SetItemAllowOverlap();
        var clicked = ImGui.IsItemClicked();
        var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        var baseItemHovered = ImGui.IsItemHovered();
        // A release is a key click only when this same key owned the original press.
        // This prevents the Enter folder action from carrying its still-held mouse
        // input into the newly displayed folder and opening the key underneath it.
        var editClickReleased = editMode &&
                                !folderChoicesWereVisible &&
                                baseItemHovered &&
                                ImGui.IsItemDeactivated();
        var dragDropHandled = editMode && !folderChoicesWereVisible && HandleDeckKeyDragDrop(entries, entry, slot);
        if (entry is null && editClickReleased && !dragDropHandled)
        {
            folderActionMenuId = null;
            OpenEditor(null, slot);
        }
        else if (entry?.Kind == DeckEntryKind.Folder && editMode && !dragDropHandled)
        {
            if (rightClicked)
            {
                folderActionMenuId = null;
                OpenEditor(entry, slot);
            }
            else if (editClickReleased)
            {
                folderActionMenuId = entry.Id;
            }
        }
        else if (entry is not null && (rightClicked || editClickReleased && !dragDropHandled))
        {
            folderActionMenuId = null;
            OpenEditor(entry, slot);
        }
        else if (!editMode && entry?.Kind == DeckEntryKind.Folder && clicked) folderPath.Add(entry.Id);
        else if (!editMode && entry is not null && clicked) _ = ExecuteMacroAsync(entry);
        var folderChoicesVisible = entry?.Kind == DeckEntryKind.Folder &&
                                   (dragFolderTargetId == entry.Id || folderActionMenuId == entry.Id);
        if (baseItemHovered && !folderChoicesVisible)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(editMode
                ? entry is null
                    ? "Click to create a key, or drop another key here to move it."
                    : entry.Kind == DeckEntryKind.Folder
                        ? "Click for Edit or Enter, right-click to edit directly, or drag to rearrange."
                        : "Click to edit, or drag this key to rearrange the deck."
                : entry is null
                    ? "Empty key"
                    : entry.Kind == DeckEntryKind.Folder
                        ? "Open folder; right-click to edit"
                        : $"Run {entry.Title}; right-click to edit");
            ImGui.EndTooltip();
        }
        if (entry?.Kind == DeckEntryKind.Folder && folderChoicesVisible)
        {
            actionOverlays.Add(new FolderActionOverlay(
                entries,
                entry,
                min,
                size,
                dragFolderTargetId == entry.Id));
        }
    }

    private bool HandleDeckKeyDragDrop(List<DeckEntry> entries, DeckEntry? entry, int targetSlot)
    {
        var handled = false;
        if (entry is not null && ImGui.BeginDragDropSource())
        {
            draggedDeckEntryId = entry.Id;
            ImGui.SetDragDropPayload("MACRODECK_KEY", new byte[] { 1 }, ImGuiCond.Once);
            ImGui.TextUnformatted($"Move {entry.Title}");
            ImGui.EndDragDropSource();
        }

        if (!ImGui.BeginDragDropTarget())
            return handled;
        var flags = entry?.Kind == DeckEntryKind.Folder
            ? ImGuiDragDropFlags.AcceptBeforeDelivery
            : ImGuiDragDropFlags.None;
        var payload = ImGui.AcceptDragDropPayload("MACRODECK_KEY", flags);
        if (!payload.IsNull && entry?.Kind == DeckEntryKind.Folder)
        {
            if (draggedDeckEntryId != entry.Id)
                dragFolderTargetId = entry.Id;
            ImGui.EndDragDropTarget();
            return false;
        }
        if (!payload.IsNull)
            dragFolderTargetId = null;
        if (!payload.IsNull && payload.IsDelivery() && draggedDeckEntryId is { } draggedId)
        {
            var source = entries.FirstOrDefault(candidate => candidate.Id == draggedId);
            if (source is not null)
            {
                var sourceSlot = source.Slot;
                if (sourceSlot != targetSlot)
                {
                    if (entry is null)
                    {
                        source.Slot = targetSlot;
                        status = $"Moved {source.Title}";
                    }
                    else
                    {
                        entry.Slot = sourceSlot;
                        source.Slot = targetSlot;
                        status = $"Swapped {source.Title} and {entry.Title}";
                    }
                    persistence.SaveNow();
                }
                handled = true;
            }
            draggedDeckEntryId = null;
        }
        ImGui.EndDragDropTarget();
        return handled;
    }

    private void DrawFolderActionChoices(
        List<DeckEntry> entries,
        DeckEntry folder,
        Vector2 keyMin,
        Vector2 keySize,
        bool dragMode)
    {
        var savedCursor = ImGui.GetCursorScreenPos();
        var margin = TabletAppTheme.Px(7f);
        var gap = TabletAppTheme.Px(6f);
        var availableHeight = MathF.Max(TabletAppTheme.Px(44f), keySize.Y - margin * 2f - gap);
        var boxHeight = MathF.Min(TabletAppTheme.Px(30f), availableHeight * 0.5f);
        var boxWidth = MathF.Min(
            MathF.Max(TabletAppTheme.Px(70f), keySize.X * 0.72f),
            keySize.X - margin * 2f);
        var stackHeight = boxHeight * 2f + gap;
        var firstMin = keyMin + new Vector2(
            (keySize.X - boxWidth) * 0.5f,
            (keySize.Y - stackHeight) * 0.5f);
        var secondMin = firstMin + new Vector2(0f, boxHeight + gap);
        var destinationSlot = FindFirstFolderSlot(folder);

        DrawFolderActionChoice(
            entries,
            folder,
            firstMin,
            new Vector2(boxWidth, boxHeight),
            dragMode ? "Swap" : "Edit",
            dragMode,
            true,
            moveIntoFolder: false);
        DrawFolderActionChoice(
            entries,
            folder,
            secondMin,
            new Vector2(boxWidth, boxHeight),
            dragMode ? destinationSlot.HasValue ? "Move In" : "Full" : "Enter",
            dragMode,
            !dragMode || destinationSlot.HasValue,
            moveIntoFolder: true);
        ImGui.SetCursorScreenPos(savedCursor);
    }

    private void DrawFolderActionChoice(
        List<DeckEntry> entries,
        DeckEntry folder,
        Vector2 min,
        Vector2 size,
        string label,
        bool dragMode,
        bool enabled,
        bool moveIntoFolder)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##macrodeck-folder-action-{folder.Id}-{label}", size);
        var hovered = ImGui.IsItemHovered();
        var clicked = enabled && ImGui.IsItemClicked();
        var draw = ImGui.GetWindowDrawList();
        var accent = TabletAppTheme.Accent;
        var fill = enabled
            ? hovered
                ? new Vector4(accent.X * 0.82f, accent.Y * 0.82f, accent.Z * 0.82f, 0.98f)
                : new Vector4(0.10f + accent.X * 0.25f, 0.10f + accent.Y * 0.25f, 0.13f + accent.Z * 0.25f, 0.96f)
            : new Vector4(0.12f, 0.12f, 0.15f, 0.92f);
        draw.AddRectFilled(min, min + size, ImGui.GetColorU32(fill), TabletAppTheme.Px(6f));
        draw.AddRect(min, min + size, ImGui.GetColorU32(enabled ? TabletAppTheme.AccentHover : TabletAppTheme.MutedText), TabletAppTheme.Px(6f), ImDrawFlags.None, TabletAppTheme.Px(1.2f));
        var textSize = ImGui.CalcTextSize(label);
        draw.AddText(min + (size - textSize) * 0.5f, ImGui.GetColorU32(enabled ? TabletAppTheme.Text : TabletAppTheme.MutedText), label);

        if (dragMode && ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("MACRODECK_KEY", ImGuiDragDropFlags.None);
            if (!payload.IsNull && payload.IsDelivery())
            {
                if (enabled)
                    ApplyFolderDrop(entries, folder, moveIntoFolder);
                else
                    CancelFullDestinationDrop("This folder is full and has no space for another key.");
            }
            ImGui.EndDragDropTarget();
        }
        else if (!dragMode && clicked)
        {
            folderActionMenuId = null;
            if (moveIntoFolder)
                folderPath.Add(folder.Id);
            else
                OpenEditor(folder, folder.Slot);
        }

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(!enabled
                ? "This folder has no available configurable slots."
                : dragMode
                ? moveIntoFolder
                    ? "Move the dragged key into the first available slot in this folder."
                    : "Swap the dragged key with this folder on the current page."
                : moveIntoFolder
                    ? "Open this folder without leaving Edit Deck mode."
                    : "Edit this folder's name and artwork.");
            ImGui.EndTooltip();
        }
    }

    private void CancelFullDestinationDrop(string message)
    {
        pendingNotification = message;
        draggedDeckEntryId = null;
        dragFolderTargetId = null;
        folderActionMenuId = null;
    }

    private void ApplyFolderDrop(List<DeckEntry> entries, DeckEntry folder, bool moveIntoFolder)
    {
        if (draggedDeckEntryId is not { } draggedId)
            return;
        var source = entries.FirstOrDefault(candidate => candidate.Id == draggedId);
        if (source is null || source.Id == folder.Id)
            return;

        if (moveIntoFolder)
        {
            var destinationSlot = FindFirstFolderSlot(folder);
            if (!destinationSlot.HasValue)
                return;
            entries.Remove(source);
            source.Slot = destinationSlot.Value;
            folder.Children.Add(source);
            status = $"Moved {source.Title} into {folder.Title}";
        }
        else
        {
            var sourceSlot = source.Slot;
            source.Slot = folder.Slot;
            folder.Slot = sourceSlot;
            status = $"Swapped {source.Title} and {folder.Title}";
        }

        draggedDeckEntryId = null;
        dragFolderTargetId = null;
        folderActionMenuId = null;
        persistence.SaveNow();
    }

    private static int? FindFirstFolderSlot(DeckEntry folder)
    {
        folder.Children ??= [];
        return FindFirstAvailableSlot(folder.Children, 1);
    }

    private static int? FindFirstAvailableSlot(IEnumerable<DeckEntry> entries, int firstSlot)
    {
        var usedSlots = entries.Select(entry => entry.Slot).ToHashSet();
        return Enumerable.Range(firstSlot, 32 - firstSlot)
            .Cast<int?>()
            .FirstOrDefault(slot => !usedSlots.Contains(slot!.Value));
    }

    private IDalamudTextureWrap? ResolveKeyArtwork(DeckEntry entry, bool useDefaultIcon)
    {
        IDalamudTextureWrap? texture = null;
        if (!string.IsNullOrWhiteSpace(entry.ImagePath))
            texture = textures.GetResourceIcon($"macrodeck-{entry.Id}", entry.ImagePath);
        var iconId = entry.GameIconId > 0
            ? entry.GameIconId
            : useDefaultIcon ? macroIcons.DefaultIconId : 0;
        return texture ?? macroIcons.GetTexture(iconId);
    }

    private void DrawFolderKey(ImDrawListPtr draw, DeckEntry entry, Vector2 min, Vector2 size)
    {
        var artworkHeight = MathF.Max(TabletAppTheme.Px(42f), size.Y - TabletAppTheme.Px(22f));
        var maximumFolderWidth = MathF.Max(TabletAppTheme.Px(28f), size.X - TabletAppTheme.Px(20f));
        var minimumFolderWidth = MathF.Min(TabletAppTheme.Px(64f), maximumFolderWidth);
        var folderWidth = Math.Clamp(artworkHeight * 1.15f, minimumFolderWidth, maximumFolderWidth);
        var maximumFolderHeight = MathF.Max(TabletAppTheme.Px(22f), artworkHeight - TabletAppTheme.Px(8f));
        var minimumFolderHeight = MathF.Min(TabletAppTheme.Px(46f), maximumFolderHeight);
        var folderHeight = Math.Clamp(folderWidth * 0.70f, minimumFolderHeight, maximumFolderHeight);
        var folderMin = new Vector2(
            min.X + (size.X - folderWidth) * 0.5f,
            min.Y + MathF.Max(TabletAppTheme.Px(4f), (artworkHeight - folderHeight) * 0.5f));
        var tabHeight = folderHeight * 0.17f;
        var bodyMin = folderMin + new Vector2(0f, tabHeight);
        var bodyMax = folderMin + new Vector2(folderWidth, folderHeight);
        var color = ImGui.GetColorU32(TabletAppTheme.AccentHover);
        draw.AddRectFilled(bodyMin, bodyMax, color, TabletAppTheme.Px(8f));
        draw.AddRectFilled(
            folderMin + new Vector2(folderWidth * 0.09f, 0f),
            folderMin + new Vector2(folderWidth * 0.52f, tabHeight * 1.45f),
            color,
            TabletAppTheme.Px(5f));

        var texture = ResolveKeyArtwork(entry, false);
        if (texture is null)
            return;
        var bodySize = bodyMax - bodyMin;
        var badgePadding = new Vector2(bodySize.X * 0.18f, bodySize.Y * 0.14f);
        var badgeMin = bodyMin + badgePadding;
        var badgeMax = bodyMax - badgePadding;
        var clipPadding = TabletAppTheme.Px(new Vector2(4f, 4f));
        draw.PushClipRect(bodyMin + clipPadding, bodyMax - clipPadding, true);
        DrawFittedImage(draw, texture, badgeMin, badgeMax);
        draw.PopClipRect();
    }

    private static void DrawFittedImage(ImDrawListPtr draw, IDalamudTextureWrap texture, Vector2 min, Vector2 max)
    {
        var box = Vector2.Max(Vector2.One, max - min);
        var source = new Vector2(Math.Max(1, texture.Width), Math.Max(1, texture.Height));
        var scale = MathF.Min(box.X / source.X, box.Y / source.Y);
        var imageSize = source * scale;
        var imageMin = min + (box - imageSize) * 0.5f;
        draw.AddImage(texture.Handle, imageMin, imageMin + imageSize);
    }

    private void OpenEditor(DeckEntry? entry, int slot)
    {
        folderActionMenuId = null;
        editingEntry = entry; editingSlot = slot; editKind = entry?.Kind ?? DeckEntryKind.Macro;
        editTitle = entry?.Title ?? "New Macro"; editImage = entry?.ImagePath ?? string.Empty;
        editGameIconId = entry?.GameIconId > 0 ? entry.GameIconId : macroIcons.DefaultIconId;
        editScript = entry?.Script ?? string.Empty; editorValidation = string.Empty;
        deleteConfirmationPending = false; iconPickerOpen = false; editorOpen = true; profilesOpen = false;
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
                : iconPickerOpen
                    ? TabletAppTheme.Px(new Vector2(1120, 660))
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
                else if (iconPickerOpen)
                    DrawIconPicker();
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
        DrawGameIconEditor();
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
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.45f, 0.45f, 1f));
                ImGui.TextWrapped(editorValidation);
                ImGui.PopStyleColor();
            }
        }
        if (ImGui.Button("Save", TabletAppTheme.Px(new Vector2(100, 0))))
        {
            var valid = editKind != DeckEntryKind.Macro || TryParseMacroScript(editScript, out _, out editorValidation);
            if (valid)
            {
                var entries = CurrentEntries(venue);
                var entry = editingEntry ?? new DeckEntry { Slot = editingSlot };
                entry.Kind = editKind; entry.Title = DeckEntry.NormalizeTitle(editTitle, editKind == DeckEntryKind.Folder ? "Folder" : "Macro");
                entry.ImagePath = editImage.Trim(); entry.GameIconId = Math.Max(0, editGameIconId); entry.Script = editScript.Trim(); entry.Message = string.Empty; entry.EmoteCommand = string.Empty;
                if (entry.Kind == DeckEntryKind.Macro) entry.Children.Clear();
                if (editingEntry is null) entries.Add(entry);
                persistence.SaveNow(); editorOpen = false;
            }
        }
        ImGui.SameLine();
        if (editingEntry is not null && ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(100, 0)))) deleteConfirmationPending = true;
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(100, 0)))) { editorOpen = false; iconPickerOpen = false; }
    }

    private void DrawGameIconEditor()
    {
        ImGui.TextUnformatted("Game icon");
        ImGui.SameLine();
        var previewSize = TabletAppTheme.Px(new Vector2(34f, 34f));
        var previewMin = ImGui.GetCursorScreenPos();
        var previewTexture = macroIcons.GetTexture(editGameIconId > 0 ? editGameIconId : macroIcons.DefaultIconId);
        if (previewTexture is not null)
            ImGui.Image(previewTexture.Handle, previewSize);
        else
            ImGui.Dummy(previewSize);
        ImGui.SameLine();
        if (ImGui.Button("Choose game icon", TabletAppTheme.Px(new Vector2(148f, 34f))))
        {
            iconPickerPage = 0;
            iconSearch = string.Empty;
            iconPickerOpen = true;
        }
        ImGui.SameLine();
        ImGui.TextColored(TabletAppTheme.MutedText, $"Icon {Math.Max(0, editGameIconId)}");
        if (!string.IsNullOrWhiteSpace(editImage))
        {
            ImGui.SameLine();
            ImGui.TextColored(TabletAppTheme.MutedText, "Custom image set; popout uses icon by default");
        }
        if (ImGui.IsMouseHoveringRect(previewMin, previewMin + previewSize))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(260f));
            ImGui.TextWrapped(editKind == DeckEntryKind.Folder
                ? "This icon appears as a smaller badge inside the folder so folders are easy to identify without covering the folder shape."
                : "This in-game icon is used whenever the key does not have a custom image. It is also used by default on the compact popout deck.");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private void DrawIconPicker()
    {
        ImGui.TextUnformatted("Choose an in-game icon");
        ImGui.SameLine();
        if (ImGui.Button("Back", TabletAppTheme.Px(new Vector2(76f, 0f))))
        {
            iconPickerOpen = false;
            return;
        }
        ImGui.SameLine();
        var remainingHeaderWidth = ImGui.GetContentRegionAvail().X;
        var searchWidth = MathF.Min(TabletAppTheme.Px(360f), MathF.Max(TabletAppTheme.Px(180f), remainingHeaderWidth));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, remainingHeaderWidth - searchWidth));
        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##macrodeck-icon-search", "Search icons...", ref iconSearch, 100))
        {
            iconPickerPage = 0;
            SelectBestIconSearchCategory();
        }
        ImGui.Separator();

        var categories = macroIcons.Categories;
        if (categories.Count == 0)
        {
            ImGui.TextWrapped("The FFXIV icon catalogs are unavailable. Custom image selection remains available.");
            return;
        }

        iconCategoryIndex = Math.Clamp(iconCategoryIndex, 0, categories.Count - 1);
        if (ImGui.BeginTabBar("##macrodeck-icon-categories", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
            {
                var category = categories[categoryIndex];
                var tabFlags = forceIconCategorySelection && categoryIndex == iconCategoryIndex
                    ? ImGuiTabItemFlags.SetSelected
                    : ImGuiTabItemFlags.None;
                if (!ImGui.BeginTabItem($"{category.Label}##macrodeck-icon-category-{category.Id}", tabFlags))
                    continue;
                if (!forceIconCategorySelection && iconCategoryIndex != categoryIndex)
                {
                    iconCategoryIndex = categoryIndex;
                    iconPickerPage = 0;
                }
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        forceIconCategorySelection = false;

        var selectedCategory = categories[iconCategoryIndex];
        var searchText = iconSearch.Trim();
        var all = string.IsNullOrWhiteSpace(searchText)
            ? selectedCategory.Icons.ToList()
            : selectedCategory.Icons
                .Select(icon => (Icon: icon, Score: GetIconSearchScore(icon.Name, searchText)))
                .Where(result => result.Score.HasValue)
                .OrderBy(result => result.Score!.Value)
                .ThenBy(result => result.Icon.Name, StringComparer.OrdinalIgnoreCase)
                .Select(result => result.Icon)
                .ToList();
        if (all.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(TabletAppTheme.MutedText, $"No {selectedCategory.Label.ToLowerInvariant()} match '{searchText}'.");
            return;
        }
        var gap = TabletAppTheme.Px(7f);
        var desiredKeySize = TabletAppTheme.Px(56f);
        var available = ImGui.GetContentRegionAvail();
        var columns = Math.Clamp((int)((available.X + gap) / (desiredKeySize + gap)), 6, 16);
        var keySize = MathF.Max(TabletAppTheme.Px(38f), MathF.Min(desiredKeySize, (available.X - gap * (columns - 1)) / columns));
        var footerHeight = TabletAppTheme.Px(44f);
        var rows = Math.Max(3, (int)((MathF.Max(keySize, available.Y - footerHeight) + gap) / (keySize + gap)));
        var pageSize = Math.Max(1, columns * rows);
        var pageCount = Math.Max(1, (int)Math.Ceiling(all.Count / (double)pageSize));
        iconPickerPage = Math.Clamp(iconPickerPage, 0, pageCount - 1);
        var choices = all.Skip(iconPickerPage * pageSize).Take(pageSize).ToList();
        var size = new Vector2(keySize, keySize);
        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < choices.Count; index++)
        {
            if (index % columns != 0)
                ImGui.SameLine(0f, gap);
            var choice = choices[index];
            var iconId = choice.IconId;
            var min = ImGui.GetCursorScreenPos();
            var max = min + size;
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            var selected = iconId == editGameIconId;
            draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.09f, 0.095f, 0.125f, 1f)), TabletAppTheme.Px(8f));
            draw.AddRect(min, max, ImGui.GetColorU32(selected ? TabletAppTheme.AccentHover : new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, hovered ? 0.88f : 0.35f)), TabletAppTheme.Px(8f), ImDrawFlags.None, TabletAppTheme.Px(selected ? 2f : 1f));
            var texture = macroIcons.GetTexture(iconId);
            if (texture is not null)
                draw.AddImage(texture.Handle, min + TabletAppTheme.Px(new Vector2(4f, 4f)), max - TabletAppTheme.Px(new Vector2(4f, 4f)));
            ImGui.InvisibleButton($"##macrodeck-icon-{iconId}", size);
            if (ImGui.IsItemClicked())
            {
                editGameIconId = iconId;
                iconPickerOpen = false;
            }
            if (!string.IsNullOrWhiteSpace(choice.Name) && ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(choice.Name);
                ImGui.EndTooltip();
            }
        }

        ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY(), ImGui.GetWindowHeight() - footerHeight));
        ImGui.Separator();
        if (ImGui.Button("Previous", TabletAppTheme.Px(new Vector2(90f, 0f))) && iconPickerPage > 0)
            iconPickerPage--;
        ImGui.SameLine();
        ImGui.TextUnformatted($"{selectedCategory.Label} — Page {iconPickerPage + 1} of {pageCount}");
        ImGui.SameLine();
        if (ImGui.Button("Next", TabletAppTheme.Px(new Vector2(90f, 0f))) && iconPickerPage < pageCount - 1)
            iconPickerPage++;
    }

    private void SelectBestIconSearchCategory()
    {
        var query = iconSearch.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return;

        var best = macroIcons.Categories
            .SelectMany((category, categoryIndex) => category.Icons.Select(icon => new
            {
                CategoryIndex = categoryIndex,
                Score = GetIconSearchScore(icon.Name, query),
                Name = icon.Name,
            }))
            .Where(candidate => candidate.Score.HasValue)
            .OrderBy(candidate => candidate.Score!.Value)
            .ThenBy(candidate => candidate.Name.Length)
            .ThenBy(candidate => candidate.CategoryIndex)
            .FirstOrDefault();
        if (best is null)
            return;

        iconCategoryIndex = best.CategoryIndex;
        iconPickerPage = 0;
        forceIconCategorySelection = true;
    }

    private static int? GetIconSearchScore(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(query))
            return null;

        var searchText = query.Trim();
        var candidate = name.Trim();
        if (candidate.Equals(searchText, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (candidate.StartsWith(searchText, StringComparison.OrdinalIgnoreCase))
            return 10 + Math.Min(20, candidate.Length - searchText.Length);

        var containsIndex = candidate.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        if (containsIndex >= 0)
            return 70 + containsIndex;
        return null;
    }

    private void DrawSettingsScreen()
    {
        ImGui.TextUnformatted("MacroDeck settings");
        ImGui.Separator();
        ImGui.TextColored(TabletAppTheme.AccentHover, "Popout deck");
        ImGui.TextWrapped("Configure the detachable Stream Deck-style MacroDeck controller.");
        ImGui.Spacing();

        var enabled = config.PopoutEnabled;
        if (ImGui.Checkbox("Show popout deck", ref enabled))
        {
            config.PopoutEnabled = enabled;
            persistence.SaveNow();
        }

        var locked = config.PopoutPositionLocked;
        if (ImGui.Checkbox("Lock popout position", ref locked))
        {
            config.PopoutPositionLocked = locked;
            persistence.SaveNow();
        }

        var tooltips = config.PopoutTooltipsEnabled;
        if (ImGui.Checkbox("Show key tooltips", ref tooltips))
        {
            config.PopoutTooltipsEnabled = tooltips;
            persistence.SaveNow();
        }

        var useCustomImages = config.PopoutUseCustomImages;
        if (ImGui.Checkbox("Use custom images on popout", ref useCustomImages))
        {
            config.PopoutUseCustomImages = useCustomImages;
            persistence.SaveNow();
        }
        DrawSettingsTooltip("Off by default: the compact popout uses each key's selected in-game icon for a clear, consistent layout. Turn this on to use a key's custom image on the popout whenever one is set.");

        var scale = Math.Clamp(config.PopoutScale, 0.65f, 1.50f);
        ImGui.SetNextItemWidth(TabletAppTheme.Px(280f));
        if (ImGui.SliderFloat("Popout size", ref scale, 0.65f, 1.50f, "%.2fx", ImGuiSliderFlags.AlwaysClamp))
        {
            config.PopoutScale = scale;
            persistence.SaveNow();
        }
        DrawSettingsTooltip("Scales the device frame, header, spacing, icons, text, and all 32 keys together.");

        ImGui.Spacing();
        if (ImGui.Button("Reset popout position", TabletAppTheme.Px(new Vector2(190f, 0f))))
        {
            config.PopoutPositionInitialized = false;
            config.PopoutPositionLocked = false;
            config.PopoutPosition = new Vector2(120f, 120f);
            persistence.SaveNow();
        }
        DrawSettingsTooltip("Unlock and return the popout to its default screen position.");
    }

    private static void DrawSettingsTooltip(string text)
    {
        if (!ImGui.IsItemHovered())
            return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(320f));
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
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
        if (ImGui.Button("Create Venue")) { persistence.AddVenue(newVenueName); folderPath.Clear(); popout.ResetFolder(); profileName = persistence.ActiveVenue.Name; }
        ImGui.SameLine();
        if (persistence.Venues.Count <= 1) ImGui.BeginDisabled();
        if (ImGui.Button("Delete Active")) { persistence.DeleteVenue(venue.Id); folderPath.Clear(); popout.ResetFolder(); profileName = persistence.ActiveVenue.Name; }
        if (persistence.Venues.Count <= 1) ImGui.EndDisabled();
        ImGui.Separator();
        if (ImGui.Button("Export Active")) dialogs.SaveProfile(venue.Name, path => { try { persistence.ExportVenue(venue, path); status = "Profile exported"; } catch (Exception ex) { status = ex.Message; } });
        ImGui.SameLine();
        if (ImGui.Button("Import Profile")) dialogs.ImportProfile(path => { try { persistence.ImportVenue(path); folderPath.Clear(); popout.ResetFolder(); status = "Profile imported"; } catch (Exception ex) { status = ex.Message; } });
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

    private List<DeckEntry>? GetParentEntries(VenueProfile venue)
    {
        if (folderPath.Count == 0)
            return null;
        var entries = venue.Buttons;
        for (var index = 0; index < folderPath.Count - 1; index++)
        {
            var folder = entries.FirstOrDefault(entry => entry.Id == folderPath[index] && entry.Kind == DeckEntryKind.Folder);
            if (folder is null)
                return null;
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
        persistence.SaveNow();
        dialogs.Dispose();
        textures.Dispose();
        executionGate.Dispose();
    }
}
