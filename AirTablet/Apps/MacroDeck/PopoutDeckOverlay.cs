using System.Numerics;
using AirTablet.Services;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace MacroDeck;

internal sealed class PopoutDeckOverlay
{
    private const int Columns = 8;
    private const int Rows = 4;
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly MacroIconCatalog icons;
    private readonly TextureCache textures;
    private readonly Func<DeckEntry, Task> executeMacro;
    private readonly List<Guid> folderPath = [];
    private bool wasOpen;
    private bool positionDirty;

    public PopoutDeckOverlay(
        Configuration config,
        PersistenceService persistence,
        MacroIconCatalog icons,
        TextureCache textures,
        Func<DeckEntry, Task> executeMacro)
    {
        this.config = config;
        this.persistence = persistence;
        this.icons = icons;
        this.textures = textures;
        this.executeMacro = executeMacro;
    }

    public void ResetFolder() => folderPath.Clear();

    public void Draw()
    {
        if (!config.PopoutEnabled)
        {
            wasOpen = false;
            return;
        }

        var scale = Math.Clamp(config.PopoutScale, 0.65f, 1.50f);
        var margin = 12f * scale;
        var gap = 6f * scale;
        var keySize = new Vector2(42f, 42f) * scale;
        var headerHeight = 39f * scale;
        var footerHeight = 10f * scale;
        var size = new Vector2(
            margin * 2f + keySize.X * Columns + gap * (Columns - 1),
            margin * 2f + headerHeight + keySize.Y * Rows + gap * (Rows - 1) + footerHeight);

        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (!wasOpen || !config.PopoutPositionInitialized)
        {
            var initialPosition = config.PopoutPositionInitialized
                ? config.PopoutPosition
                : ImGui.GetMainViewport().WorkPos + new Vector2(36f, 80f);
            ImGui.SetNextWindowPos(initialPosition, ImGuiCond.Always);
        }

        var flags = ImGuiWindowFlags.NoTitleBar |
                    ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoDocking;
        if (config.PopoutPositionLocked)
            flags |= ImGuiWindowFlags.NoMove;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 17f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        var visible = ImGui.Begin("MacroDeck Popout###macrodeck-popout", flags);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        wasOpen = true;
        ImGui.SetWindowFontScale(scale);

        if (visible)
            DrawDevice(scale, size, margin, gap, keySize, headerHeight);

        var position = ImGui.GetWindowPos();
        if (!config.PopoutPositionLocked && Vector2.DistanceSquared(position, config.PopoutPosition) > 0.25f)
        {
            config.PopoutPosition = position;
            config.PopoutPositionInitialized = true;
            positionDirty = true;
        }
        if (positionDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            positionDirty = false;
            persistence.SaveNow();
        }
        ImGui.End();
    }

    private void DrawDevice(float scale, Vector2 size, float margin, float gap, Vector2 keySize, float headerHeight)
    {
        var origin = ImGui.GetWindowPos();
        var draw = ImGui.GetWindowDrawList();
        var accent = TabletAppTheme.RememberedAccent;
        var accentHover = TabletAppTheme.RememberedAccentHover;
        var max = origin + size;

        draw.AddRectFilled(origin, max, ImGui.GetColorU32(new Vector4(0.045f, 0.048f, 0.062f, 0.995f)), 17f * scale);
        draw.AddRect(origin, max, ImGui.GetColorU32(new Vector4(accent.X, accent.Y, accent.Z, 0.88f)), 17f * scale, ImDrawFlags.None, 2f * scale);
        draw.AddRectFilled(origin + new Vector2(5f, 5f) * scale, max - new Vector2(5f, 5f) * scale, ImGui.GetColorU32(new Vector4(0.085f, 0.09f, 0.115f, 1f)), 13f * scale);

        var brandPos = origin + new Vector2(margin, 10f * scale);
        draw.AddText(brandPos, ImGui.GetColorU32(Vector4.One), "MACRODECK");
        var venue = persistence.ActiveVenue;
        var venueText = venue.Name.Length > 24 ? venue.Name[..21] + "..." : venue.Name;
        draw.AddText(brandPos + new Vector2(0f, 16f * scale), ImGui.GetColorU32(new Vector4(0.62f, 0.65f, 0.74f, 1f)), venueText);
        draw.AddCircleFilled(origin + new Vector2(size.X - margin - 43f * scale, 18f * scale), 3.5f * scale, ImGui.GetColorU32(accentHover));

        var lockSize = new Vector2(37f, 25f) * scale;
        var lockMin = origin + new Vector2(size.X - margin - lockSize.X, 8f * scale);
        var lockHovered = ImGui.IsMouseHoveringRect(lockMin, lockMin + lockSize);
        draw.AddRectFilled(lockMin, lockMin + lockSize, ImGui.GetColorU32(config.PopoutPositionLocked
            ? new Vector4(accent.X * 0.72f, accent.Y * 0.72f, accent.Z * 0.72f, 1f)
            : new Vector4(0.15f, 0.16f, 0.20f, lockHovered ? 1f : 0.88f)), 7f * scale);
        var lockLabel = config.PopoutPositionLocked ? "LOCK" : "MOVE";
        var lockTextSize = ImGui.CalcTextSize(lockLabel);
        draw.AddText(lockMin + (lockSize - lockTextSize) * 0.5f, ImGui.GetColorU32(Vector4.One), lockLabel);
        ImGui.SetCursorScreenPos(lockMin);
        if (ImGui.InvisibleButton("##macrodeck-popout-lock", lockSize))
        {
            config.PopoutPositionLocked = !config.PopoutPositionLocked;
            persistence.SaveNow();
        }
        DrawTooltip(config.PopoutPositionLocked ? "Unlock the popout deck position." : "Lock the popout deck at its current position.");

        var entries = CurrentEntries(venue);
        var gridOrigin = origin + new Vector2(margin, margin + headerHeight);
        for (var slot = 0; slot < Columns * Rows; slot++)
        {
            var row = slot / Columns;
            var column = slot % Columns;
            var min = gridOrigin + new Vector2(column * (keySize.X + gap), row * (keySize.Y + gap));
            var entry = entries.FirstOrDefault(candidate => candidate.Slot == slot);
            if (slot == 0 && folderPath.Count > 0)
                DrawNavigationKey(min, keySize, scale);
            else
                DrawKey(entry, slot, min, keySize, scale, accent, accentHover);
        }

        DrawScrew(draw, origin + new Vector2(9f, 9f) * scale, scale);
        DrawScrew(draw, new Vector2(max.X - 9f * scale, max.Y - 9f * scale), scale);
    }

    private void DrawKey(DeckEntry? entry, int slot, Vector2 min, Vector2 size, float scale, Vector4 accent, Vector4 accentHover)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = min + size;
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        draw.AddRectFilled(min + new Vector2(2f, 3f) * scale, max + new Vector2(2f, 3f) * scale, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)), 8f * scale);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(entry is null
            ? new Vector4(0.07f, 0.075f, 0.09f, 0.82f)
            : new Vector4(0.13f + accent.X * 0.10f, 0.13f + accent.Y * 0.10f, 0.16f + accent.Z * 0.10f, 1f)), 8f * scale);
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(accentHover.X, accentHover.Y, accentHover.Z, hovered ? 0.92f : entry is null ? 0.16f : 0.48f)), 8f * scale, ImDrawFlags.None, 1.2f * scale);

        if (entry is not null)
        {
            if (entry.Kind == DeckEntryKind.Folder)
                DrawFolderGlyph(draw, min, size, scale, accentHover, ResolveEntryArtwork(entry, false));
            else
                DrawEntryImage(draw, entry, min + new Vector2(4f, 4f) * scale, max - new Vector2(4f, 4f) * scale);
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##macrodeck-popout-key-{slot}", size);
        if (entry?.Kind == DeckEntryKind.Folder && ImGui.IsItemClicked())
            folderPath.Add(entry.Id);
        else if (entry is { Kind: DeckEntryKind.Macro } && ImGui.IsItemClicked())
            _ = executeMacro(entry);

        if (entry is not null)
            DrawTooltip(entry.Kind == DeckEntryKind.Folder ? $"Open {entry.Title}" : $"Run {entry.Title}");
    }

    private void DrawNavigationKey(Vector2 min, Vector2 size, float scale)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = min + size;
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        var accent = TabletAppTheme.RememberedAccent;
        var accentHover = TabletAppTheme.RememberedAccentHover;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(accent.X * 0.28f, accent.Y * 0.28f, accent.Z * 0.28f, 1f)), 8f * scale);
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(accentHover.X, accentHover.Y, accentHover.Z, hovered ? 1f : 0.65f)), 8f * scale, ImDrawFlags.None, 1.2f * scale);
        var center = min + size * 0.5f;
        var color = ImGui.GetColorU32(Vector4.One);
        draw.AddLine(center + new Vector2(8f, -8f) * scale, center - new Vector2(7f, 0f) * scale, color, 2.5f * scale);
        draw.AddLine(center - new Vector2(7f, 0f) * scale, center + new Vector2(8f, 8f) * scale, color, 2.5f * scale);
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##macrodeck-popout-back", size);
        if (ImGui.IsItemClicked())
        {
            if (folderPath.Count == 1) folderPath.Clear();
            else folderPath.RemoveAt(folderPath.Count - 1);
        }
        DrawTooltip(folderPath.Count == 1 ? "Return to deck home." : "Return to the previous folder.");
    }

    private void DrawEntryImage(ImDrawListPtr draw, DeckEntry entry, Vector2 min, Vector2 max)
    {
        var texture = ResolveEntryArtwork(entry, true);
        if (texture is null)
            return;

        DrawFittedImage(draw, texture, min, max);
    }

    private IDalamudTextureWrap? ResolveEntryArtwork(DeckEntry entry, bool useDefaultIcon)
    {
        IDalamudTextureWrap? texture = null;
        if (config.PopoutUseCustomImages && !string.IsNullOrWhiteSpace(entry.ImagePath))
            texture = textures.GetResourceIcon($"macrodeck-popout-{entry.Id}", entry.ImagePath);
        var iconId = entry.GameIconId > 0
            ? entry.GameIconId
            : useDefaultIcon ? icons.DefaultIconId : 0;
        return texture ?? icons.GetTexture(iconId);
    }

    private static void DrawFittedImage(ImDrawListPtr draw, IDalamudTextureWrap texture, Vector2 min, Vector2 max)
    {
        var box = Vector2.Max(Vector2.One, max - min);
        var source = new Vector2(Math.Max(1, texture.Width), Math.Max(1, texture.Height));
        var imageScale = MathF.Min(box.X / source.X, box.Y / source.Y);
        var imageSize = source * imageScale;
        var imageMin = min + (box - imageSize) * 0.5f;
        draw.AddImage(texture.Handle, imageMin, imageMin + imageSize);
    }

    private static void DrawFolderGlyph(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 size,
        float scale,
        Vector4 color,
        IDalamudTextureWrap? texture)
    {
        var folderMin = min + new Vector2(9f, 14f) * scale;
        var bodyMax = folderMin + new Vector2(24f, 17f) * scale;
        draw.AddRectFilled(folderMin, bodyMax, ImGui.GetColorU32(color), 3f * scale);
        draw.AddRectFilled(folderMin + new Vector2(2f, -5f) * scale, folderMin + new Vector2(14f, 2f) * scale, ImGui.GetColorU32(color), 2f * scale);
        if (texture is null)
            return;
        var badgeMin = folderMin + new Vector2(6f, 3f) * scale;
        var badgeMax = bodyMax - new Vector2(6f, 3f) * scale;
        draw.PushClipRect(folderMin + new Vector2(1.5f, 1.5f) * scale, bodyMax - new Vector2(1.5f, 1.5f) * scale, true);
        DrawFittedImage(draw, texture, badgeMin, badgeMax);
        draw.PopClipRect();
    }

    private static void DrawScrew(ImDrawListPtr draw, Vector2 center, float scale)
    {
        var color = ImGui.GetColorU32(new Vector4(0.28f, 0.29f, 0.34f, 1f));
        draw.AddCircleFilled(center, 2.7f * scale, color);
        draw.AddLine(center - new Vector2(1.5f, 0f) * scale, center + new Vector2(1.5f, 0f) * scale, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.10f, 1f)), 0.8f * scale);
    }

    private void DrawTooltip(string text)
    {
        if (!config.PopoutTooltipsEnabled || !ImGui.IsItemHovered())
            return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 260f);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private List<DeckEntry> CurrentEntries(VenueProfile venue)
    {
        var entries = venue.Buttons;
        foreach (var id in folderPath)
        {
            var folder = entries.FirstOrDefault(entry => entry.Id == id && entry.Kind == DeckEntryKind.Folder);
            if (folder is null)
            {
                folderPath.Clear();
                return venue.Buttons;
            }
            entries = folder.Children;
        }
        return entries;
    }
}
