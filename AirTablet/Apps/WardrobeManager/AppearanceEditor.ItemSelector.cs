using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private Configuration? preferences;
    private System.Action? savePreferences;
    private bool favoriteItemsOnly;
    private string equipmentFilter = string.Empty;
    private bool resetEquipmentScroll;

    public void ConfigureFavorites(Configuration config, System.Action save)
    {
        preferences = config;
        preferences.EquipmentFavorites ??= [];
        preferences.FacewearFavorites ??= [];
        savePreferences = save;
    }

    private string facewearQuery = string.Empty;
    private bool facewearFavoritesOnly;
    private readonly List<(uint Id, string Name)> facewearChoices = [];
    private IEnumerator<Glasses>? facewearScan;
    private bool facewearLoaded;

    private void DrawFacewearSelector(JObject entry, float requestedWidth = -1)
    {
        // Facewear is a BonusId, not an equipment ItemId. It has no dye channels
        // or material editor; colour variants are separate facewear selections.
        if (entry["BonusId"] is null) { ImGui.TextDisabled("Unsupported facewear format; saved value preserved."); return; }
        var sheet = DalamudServices.DataManager.GetExcelSheet<Glasses>();
        var knownId = FacewearId.TryRead(entry, out var id);
        var name = !knownId ? "Custom facewear (preserved)" : id == 0 ? "Nothing" : sheet.TryGetRow(id, out var current) ? current.Name.ExtractText() : $"Facewear #{id}";
        var width = requestedWidth > 0 ? requestedWidth : Math.Max(1, ImGui.GetContentRegionAvail().X);
        ImGui.SetNextItemWidth(width);
        var popupWidth = Math.Max(width, TabletAppTheme.Px(320));
        if (!BeginSelectionPopup("##facewear", name, width, new Vector2(popupWidth / TabletAppTheme.Scale, 400), false))
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Select facewear, including its colour variant. The game does not support dyes on facewear.");
            return;
        }
        if (!facewearLoaded)
        {
            facewearScan ??= sheet.GetEnumerator();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; i < 32; i++)
            {
                if (!facewearScan.MoveNext()) { facewearScan.Dispose(); facewearScan = null; facewearLoaded = true; break; }
                var row = facewearScan.Current;
                var label = row.Name.ExtractText();
                if (row.RowId != 0 && !string.IsNullOrWhiteSpace(label)) facewearChoices.Add((row.RowId, label));
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 1) break;
            }
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search-facewear", "Search facewear…", ref facewearQuery, 100);
        TabletAppTheme.VisibleCheckbox("Favourites only", ref facewearFavoritesOnly);
        if (!facewearLoaded) ImGui.TextDisabled("Loading facewear…");
        if (ImGui.BeginChild("##facewear-results", new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y)), false))
        {
            if (ImGui.Selectable("Nothing", knownId && id == 0)) { entry["BonusId"] = FacewearId.Encode(0); ImGui.CloseCurrentPopup(); }
            foreach (var choice in facewearChoices.Where(c => c.Name.Contains(facewearQuery.Trim(), StringComparison.OrdinalIgnoreCase)
                         && (!facewearFavoritesOnly || IsFacewearFavorite(c.Id)))
                         .OrderByDescending(c => IsFacewearFavorite(c.Id)).ToArray())
            {
                ImGui.PushID((int)choice.Id);
                var favorite = IsFacewearFavorite(choice.Id);
                var shared = sharedFavorites.Facewear.Contains(choice.Id);
                if (FavoriteButton(favorite) && preferences is not null && !shared)
                {
                    if (favorite) preferences.FacewearFavorites.Remove(choice.Id); else preferences.FacewearFavorites.Add(choice.Id);
                    savePreferences?.Invoke();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(shared ? "Glamourer favourite. Manage it in Glamourer, then reopen Appearance Studio to refresh." : favorite ? "Remove from favourites" : "Add to favourites");
                ImGui.SameLine();
                if (ImGui.Selectable(choice.Name, knownId && id == choice.Id)) { entry["BonusId"] = FacewearId.Encode(choice.Id); ImGui.CloseCurrentPopup(); }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private void DrawItemSelector(JObject entry, Item? current, float requestedWidth = -1)
    {
        var width = requestedWidth > 0 ? requestedWidth : Math.Max(1, ImGui.GetContentRegionAvail().X);
        ImGui.SetNextItemWidth(width);
        var popupWidth = Math.Max(width, TabletAppTheme.Px(320));
        if (!BeginSelectionPopup("##equipment-item", entry.Value<ulong>("ItemId") == EmptyEquipmentId(selected) ? "Nothing" : current?.Name.ExtractText() ?? $"Equipment #{entry.Value<ulong>("ItemId")}", width, new Vector2(popupWidth / TabletAppTheme.Scale, 400), false))
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Choose equipment for this slot. Search by name or item ID; star items to save favourites.");
            return;
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##item-search", "Search name or item ID…", ref equipmentQuery, 100);
        TabletAppTheme.VisibleCheckbox("Favourites only", ref favoriteItemsOnly);
        var query = equipmentQuery.Trim();
        var filter = selected + "/" + query + "/" + favoriteItemsOnly;
        if (filter != equipmentFilter) { equipmentFilter = filter; resetEquipmentScroll = true; }
        var key = filter + "/" + equipmentCatalogRevision;
        if (key != equipmentSearchKey)
        {
            equipmentSearchKey = key;
            equipmentMatches.Clear();
            if (equipmentCatalog.TryGetValue(selected, out var available))
                equipmentMatches.AddRange(available.Where(item => (!favoriteItemsOnly || IsItemFavorite(item.Id))
                    && (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Id.ToString() == query))
                    .OrderByDescending(item => IsItemFavorite(item.Id)).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        }
        if (!equipmentCatalogReady) ImGui.TextDisabled("Preparing equipment catalogue…");
        if (ImGui.BeginChild("##equipment-results", new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y)), false))
        {
            if (resetEquipmentScroll) { ImGui.SetScrollY(0); resetEquipmentScroll = false; }
            if (selected != "MainHand" && ImGui.Selectable("Nothing", entry.Value<ulong>("ItemId") == EmptyEquipmentId(selected)))
            { entry["ItemId"] = EmptyEquipmentId(selected); ImGui.CloseCurrentPopup(); }
            var listTop = ImGui.GetCursorPosY();
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();
            var firstRow = Math.Clamp((int)((ImGui.GetScrollY() - listTop) / rowHeight), 0, equipmentMatches.Count);
            var lastRow = Math.Min(equipmentMatches.Count, firstRow + (int)(ImGui.GetWindowHeight() / rowHeight) + 3);
            for (var row = firstRow; row < lastRow; row++)
            {
                ImGui.SetCursorPosY(listTop + row * rowHeight);
                var match = equipmentMatches[row];
                ImGui.PushID((int)match.Id);
                var favorite = IsItemFavorite(match.Id);
                var shared = sharedFavorites.Items.Contains(match.Id);
                if (FavoriteButton(favorite) && preferences is not null && !shared)
                {
                    if (favorite) preferences.EquipmentFavorites.Remove(match.Id); else preferences.EquipmentFavorites.Add(match.Id);
                    savePreferences?.Invoke();
                    // Refresh on the next frame, outside this enumeration.
                    equipmentSearchKey = string.Empty;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(shared ? "Glamourer favourite. Manage it in Glamourer, then reopen Appearance Studio to refresh." : favorite ? "Remove from favourites" : "Add to favourites");
                ImGui.SameLine();
                if (ImGui.Selectable(match.Name, entry.Value<ulong>("ItemId") == match.Id))
                { entry["ItemId"] = match.Id; ImGui.CloseCurrentPopup(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{match.Name}\nItem ID: {match.Id}");
                ImGui.PopID();
            }
            ImGui.SetCursorPosY(listTop + equipmentMatches.Count * rowHeight);
            ImGui.Dummy(new Vector2(1, 1));
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    // Glamourer's export schema uses synthetic slot IDs for empty equipment;
    // zero is not a round-trippable item ID. Both ring slots normalize to RFinger.
    private static uint EmptyEquipmentId(string slot) => slot switch
    {
        "OffHand" => uint.MaxValue - 384 - 37,
        "Head" => uint.MaxValue - 128 - 3, "Body" => uint.MaxValue - 128 - 4,
        "Hands" => uint.MaxValue - 128 - 5, "Legs" => uint.MaxValue - 128 - 7,
        "Feet" => uint.MaxValue - 128 - 8, "Ears" => uint.MaxValue - 128 - 9,
        "Neck" => uint.MaxValue - 128 - 10, "Wrists" => uint.MaxValue - 128 - 11,
        "RFinger" or "LFinger" => uint.MaxValue - 128 - 12,
        _ => 0,
    };
}
