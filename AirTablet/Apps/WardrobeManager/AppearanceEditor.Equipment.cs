using System.Globalization;
using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private string equipmentQuery = string.Empty;
    private string equipmentSearchKey = string.Empty;
    private IEnumerator<Item>? equipmentSearch;
    private readonly List<(uint Id, string Name, uint Icon)> equipmentMatches = [];

    private void DrawEquipment(JObject entry)
    {
        DrawFlag(entry, "Apply", "Apply this equipment setting");
        if (entry["ItemId"] is not null)
        {
            var id = entry.Value<ulong>("ItemId");
            Item? item = id <= uint.MaxValue && DalamudServices.DataManager.GetExcelSheet<Item>().TryGetRow((uint)id, out var found)
                ? found : null;
            ImGui.TextWrapped(item?.Name.ExtractText() ?? $"Stored equipment #{id}");
            if (item is { } equipped) DrawIcon(equipped.Icon, 64);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##equipment-search", "Search equipment by name or item ID...", ref equipmentQuery, 100);
            var searchKey = selected + "/" + equipmentQuery.Trim();
            if (searchKey != equipmentSearchKey)
            {
                equipmentSearchKey = searchKey;
                equipmentSearch?.Dispose();
                equipmentSearch = equipmentQuery.Trim().Length >= 2
                    ? DalamudServices.DataManager.GetExcelSheet<Item>().GetEnumerator() : null;
                equipmentMatches.Clear();
            }
            // Bound sheet work and result rendering even for the full game item list.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; equipmentSearch is not null && i < 256; i++)
            {
                if (!equipmentSearch.MoveNext()) { equipmentSearch.Dispose(); equipmentSearch = null; break; }
                var candidate = equipmentSearch.Current;
                if (FitsSlot(candidate, selected))
                {
                    var name = candidate.Name.ExtractText();
                    if (name.Contains(equipmentQuery.Trim(), StringComparison.OrdinalIgnoreCase)
                        || candidate.RowId.ToString(CultureInfo.InvariantCulture) == equipmentQuery.Trim())
                        equipmentMatches.Add((candidate.RowId, name, candidate.Icon));
                }
                if (equipmentMatches.Count >= 40)
                { equipmentSearch.Dispose(); equipmentSearch = null; break; }
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 1) break;
            }
            if (equipmentSearch is not null) ImGui.TextUnformatted("Searching...");
            if (equipmentMatches.Count > 0 && ImGui.BeginChild("##equipment-results", new Vector2(0, TabletAppTheme.Px(180)), true))
            {
                foreach (var match in equipmentMatches)
                    if (ImGui.Selectable($"{match.Name}##{match.Id}", id == match.Id)) entry["ItemId"] = match.Id;
                ImGui.EndChild();
            }
            else if (equipmentMatches.Count > 0) ImGui.EndChild();
            if (equipmentMatches.Count == 40) ImGui.TextWrapped("Showing the first 40 matches. Refine the search for more specific results.");
            DrawFlag(entry, "ApplyStain", "Apply dyes");
            var dyeCount = item?.DyeCount ?? 0;
            if (dyeCount == 0) ImGui.TextWrapped("This item has no standard dye channels.");
            ImGui.BeginDisabled(dyeCount == 0);
            // Use the actual export shape; unsupported layouts remain untouched.
            if (entry["Stains"] is JArray stains)
            {
                for (var channel = 0; channel < Math.Min(stains.Count, dyeCount); channel++)
                {
                    var index = channel;
                    DrawStain($"Dye channel {channel + 1}", stains[channel].Value<int>(), value => stains[index] = value);
                }
            }
            else if (entry["Stain"] is JArray dyeChannels)
            {
                for (var channel = 0; channel < Math.Min(dyeChannels.Count, dyeCount); channel++)
                {
                    var index = channel;
                    DrawStain($"Dye channel {channel + 1}", dyeChannels[channel].Value<int>(), value => dyeChannels[index] = value);
                }
            }
            else if (entry["Stain"]?.Type == JTokenType.Integer)
                DrawStain("Dye channel 1", entry.Value<int>("Stain"), value => entry["Stain"] = value);
            if (dyeCount > 1 && entry["Stain2"]?.Type == JTokenType.Integer)
                DrawStain("Dye channel 2", entry.Value<int>("Stain2"), value => entry["Stain2"] = value);
            ImGui.EndDisabled();
            if (item?.IsCrestWorthy == true && (entry["Crest"] is not null || entry["ApplyCrest"] is not null))
            {
                DrawFlag(entry, "ApplyCrest", "Apply company crest setting");
                DrawFlag(entry, "Crest", "Show company crest");
            }
        }
        else
        {
            foreach (var field in new[] { "Show", "IsToggled" })
                if (entry[field] is not null) DrawFlag(entry, field, field == "Show" ? "Visible" : "Visor toggled");
        }
        DrawSectionReset();
    }

    private static bool FitsSlot(Item item, string slot)
    {
        if (!item.EquipSlotCategory.IsValid) return false;
        var category = item.EquipSlotCategory.Value;
        return slot switch
        {
            "MainHand" => category.MainHand > 0, "OffHand" => category.OffHand > 0,
            "Head" => category.Head > 0, "Body" => category.Body > 0,
            "Hands" => category.Gloves > 0, "Legs" => category.Legs > 0, "Feet" => category.Feet > 0,
            "Ears" => category.Ears > 0, "Neck" => category.Neck > 0, "Wrists" => category.Wrists > 0,
            "RFinger" => category.FingerR > 0, "LFinger" => category.FingerL > 0,
            _ => false,
        };
    }

    private void DrawStain(string label, int current, Action<int> set, float width = -1, bool showLabel = true)
    {
        var sheet = DalamudServices.DataManager.GetExcelSheet<Stain>();
        var preview = current == 0 ? "No dye" : sheet.TryGetRow((uint)current, out var stain) ? stain.Name.ExtractText() : $"Dye {current}";
        if (showLabel) ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(width);
        var swatch = TabletAppTheme.Px(25f);
        var paletteWidth = 8 * (swatch + ImGui.GetStyle().ItemSpacing.X) + ImGui.GetStyle().WindowPadding.X * 2 + ImGui.GetStyle().ScrollbarSize + TabletAppTheme.Px(4);
        if (!BeginSelectionPopup("##" + label, preview, width, new Vector2(paletteWidth / TabletAppTheme.Scale, 480)))
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(label + ": " + preview + ". Choose a standard game dye for this channel.");
            return;
        }
        if (ImGui.Selectable("No dye", current == 0)) { set(0); ImGui.CloseCurrentPopup(); }
        const int columns = 8;
        var index = 0;
        foreach (var option in sheet.OrderBy(stain => DyeColorOrder.Key(stain.Color)).ThenBy(stain => stain.RowId))
        {
            var name = option.Name.ExtractText();
            if (option.RowId == 0 || string.IsNullOrWhiteSpace(name)) continue;
            if (index++ % columns != 0) ImGui.SameLine();
            var color = new Vector4(((option.Color >> 16) & 255) / 255f, ((option.Color >> 8) & 255) / 255f, (option.Color & 255) / 255f, 1f);
            if (ImGui.ColorButton($"##dye-{option.RowId}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(swatch)))
            { set((int)option.RowId); ImGui.CloseCurrentPopup(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(name + (option.IsMetallic ? " (Metallic)" : string.Empty));
            if (current == option.RowId)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.Text), 2f, ImDrawFlags.None, 2f);
        }
        ImGui.EndPopup();
    }

    private static void DrawFlag(JObject entry, string key, string label)
    {
        var enabled = entry.Value<bool?>(key) ?? false;
        if (TabletAppTheme.VisibleCheckbox(label, ref enabled)) entry[key] = enabled;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(key switch
        {
            "ApplyStain" => "Apply this item's standard dyes when the preset is applied. Unchecked keeps the character's current dyes.",
            "ApplyCrest" => "Apply the saved company crest visibility for this item.",
            "Crest" => "Show the character's free company crest on this supported item.",
            "Show" => "Whether this equipment is visible when this setting is applied.",
            "IsToggled" => "Toggle the visor state. Only helmets with a visor will change appearance.",
            _ => label,
        });
    }

    private void DrawSectionReset()
    {
        if (section == "Materials" && original![section]?[selected] is null)
        {
            if (ImGui.Button("Remove new row"))
            {
                ((JObject)draft![section]!).Remove(selected);
                selected = string.Empty;
            }
            if (error.Length > 0) ImGui.TextWrapped(error);
            return;
        }
        if (ImGui.Button("Reset this entry") && original![section]?[selected] is JObject saved)
            draft![section]![selected] = saved.DeepClone();
        if (error.Length > 0) ImGui.TextWrapped(error);
    }

    private static string MaterialLabel(string key)
    {
        if (!uint.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)) return key;
        var slot = (address >> 16) & 255;
        string[] slots = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "Right ring", "Left ring",
            "Hair", "Face", "Tail / ears", "Connector 1", "Connector 2", "Body 3", "Glasses"];
        var target = (address >> 24) switch { 2 => "Main hand", 3 => "Off hand", _ => slot < slots.Length ? slots[slot] : $"Slot {slot}" };
        return $"{target} · Material {((address >> 8) & 255) + 1} · Row {(address & 255) + 1}";
    }

    private void DrawMaterial(JObject entry)
    {
        ImGui.TextWrapped(MaterialLabel(selected));
        DrawFlag(entry, "Enabled", "Apply this advanced dye row");
        DrawFlag(entry, "Revert", "Revert this row to its original appearance");
        ImGui.TextWrapped("Rows identify material regions; they are separate from the two standard dye channels. To identify a region on your worn character, scan and capture its material in the left pane, then hover the highlight button for that row.");
        var mode = entry.Value<string>("Mode") ?? "Legacy";
        ImGui.TextUnformatted($"Material format: {mode}");
        ImGui.BeginDisabled(entry.Value<bool?>("Revert") == true);
        foreach (var channel in new[] { "Diffuse", "Specular", "Emissive" })
        {
            if (entry[channel + "R"] is null || entry[channel + "G"] is null || entry[channel + "B"] is null) continue;
            var title = channel switch { "Diffuse" => "Base colour", "Specular" => "Reflection colour", _ => "Glow colour" };
            if (!ImGui.TreeNode(title)) continue;
            var color = new Vector3(entry.Value<float>(channel + "R"), entry.Value<float>(channel + "G"), entry.Value<float>(channel + "B"));
            var alpha = 1f;
            if (DrawColorControls(channel, ref color, ref alpha, false))
            { entry[channel + "R"] = color.X; entry[channel + "G"] = color.Y; entry[channel + "B"] = color.Z; }
            ImGui.TreePop();
        }
        var fields = mode == "Dawntrail"
            ? new[] { "Roughness", "Metalness", "Sheen", "SheenTint", "SheenAperture" }
            : new[] { "SpecularA", "Gloss" };
        foreach (var field in fields)
        {
            ImGui.PushID(field);
            var use = entry[field] is not null;
            var title = field switch { "SpecularA" => "Reflection strength", "SheenAperture" => "Sheen roughness", _ => Label(field) };
            if (TabletAppTheme.VisibleCheckbox("Override " + title, ref use))
            {
                if (use) entry[field] = field == "Gloss" ? 20f : field == "SheenAperture" ? 5f : 0.5f;
                else entry.Remove(field);
            }
            if (use)
            {
                var value = entry.Value<float>(field);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("##value", ref value, 0.01f, 0.1f) && float.IsFinite(value))
                    entry[field] = Math.Clamp(value, field == "SheenAperture" ? 0.001f : 0f, 65504f);
            }
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        DrawSectionReset();
    }
}
