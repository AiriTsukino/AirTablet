using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private readonly Dictionary<string, IReadOnlyList<AppearanceCatalog.Choice>> compactChoices = [];
    private string compactIdentity = string.Empty;

    private void DrawCompactOverview()
    {
        if (draft![section] is not JObject fields) return;
        if (section == "Equipment") { DrawEquipmentLayout(fields); return; }
        if (section == "Parameters") { DrawParameterLayout(fields); return; }
        var identity = $"{AppearanceCatalog.Value(draft, "Race")}/{AppearanceCatalog.Value(draft, "Clan")}/{AppearanceCatalog.Value(draft, "Gender")}/{AppearanceCatalog.Value(draft, "Face")}";
        if (compactIdentity != identity) { compactIdentity = identity; compactChoices.Clear(); }
        if (section == "Customize")
        {
            // Prepare at most one feature per frame; don't load every choice
            // catalogue at once when opening the compact overview.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var field in fields.Properties().Where(p => p.Name != "BodyType" && !compactChoices.ContainsKey(p.Name)))
            {
                compactChoices[field.Name] = catalog.Choices(draft, field.Name);
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) break;
            }
            DrawCustomizationLayout(fields);
            return;
        }
        string? dyesFor = null;
        var facialFeaturesDrawn = false;
        ImGui.TextDisabled("Apply toggles control what the preset changes. Values remain saved when unchecked.");
        if (!ImGui.BeginTable("##compact-appearance", 3, ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Apply", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(38));
        ImGui.TableSetupColumn("Feature", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(150));
        ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthStretch);
        var displayedFields = fields.Properties().AsEnumerable();
        if (section == "Equipment" && draft["Bonus"] is JObject bonus)
            displayedFields = displayedFields.Concat(bonus.Properties().Where(p => p.Name == "Glasses"));
        foreach (var property in displayedFields.OrderBy(p => AppearanceLayoutOrder.Rank(section, p.Name)).ToArray())
        {
            if (property.Value is not JObject entry || (section == "Customize" && property.Name == "BodyType")) continue;
            if (section == "Customize" && IsFacialFeature(property.Name))
            {
                if (!facialFeaturesDrawn) DrawFacialFeatureIcons(fields);
                facialFeaturesDrawn = true;
                continue;
            }
            if (section == "Equipment" && property.Name == "OffHand")
            {
                var offhandId = entry.Value<ulong?>("ItemId") ?? 0;
                if (offhandId > uint.MaxValue || !DalamudServices.DataManager.GetExcelSheet<Item>().TryGetRow((uint)offhandId, out var offhand)
                    || string.IsNullOrWhiteSpace(offhand.Name.ExtractText()) || !FitsSlot(offhand, "OffHand")) continue;
            }
            selected = property.Name;
            ImGui.PushID(selected);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var apply = entry.Value<bool?>("Apply") ?? false;
            if (TabletAppTheme.VisibleCheckbox("##apply", ref apply))
            {
                entry["Apply"] = apply;
                if (section == "Customize" && selected is "Race" or "Clan") { SetApply("Race", apply); SetApply("Clan", apply); }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apply " + Label(selected));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(Label(selected));
            ImGui.TableNextColumn();
            if (section == "Equipment" && selected == "Glasses") DrawFacewearSelector(entry);
            else if (section == "Equipment") DrawCompactEquipment(entry, ref dyesFor);
            else if (section == "Parameters") DrawCompactParameter(entry);
            else DrawCompactCustomization(entry);
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (dyesFor is not null) OpenEquipmentDyes(dyesFor);
        if (error.Length > 0) ImGui.TextWrapped(error);
    }

    private void DrawCompactEquipment(JObject entry, ref string? dyesFor)
    {
        var paletteX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(27);
        if (entry["ItemId"] is null)
        {
            foreach (var flag in new[] { "Show", "IsToggled" }) if (entry[flag] is not null) DrawFlag(entry, flag, flag == "Show" ? "Visible" : "Visor toggled");
            return;
        }
        var id = entry.Value<ulong>("ItemId");
        Item? item = id <= uint.MaxValue && DalamudServices.DataManager.GetExcelSheet<Item>().TryGetRow((uint)id, out var found) ? found : null;
        if (item is { } gear) { DrawIcon(gear.Icon, 40); ImGui.SameLine(); }
        DrawItemSelector(entry, item);
        if (item?.DyeCount > 0)
        {
            var width = Math.Max(1, (ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(52)) / 2);
            ImGui.PushItemWidth(width);
            DrawStain("Dye 1", ReadCompactStain(entry, 0), value => SetCompactStain(entry, 0, value), width, false);
            if (item?.DyeCount > 1) { ImGui.SameLine(); DrawStain("Dye 2", ReadCompactStain(entry, 1), value => SetCompactStain(entry, 1, value), width, false); }
            ImGui.PopItemWidth();
            ImGui.SameLine();
            ImGui.SetCursorPosX(paletteX);
            if (DrawPaletteButton()) dyesFor = selected;
            DrawFlag(entry, "ApplyStain", "Apply dyes");
        }
        else { ImGui.SetCursorPosX(paletteX); if (DrawPaletteButton()) dyesFor = selected; }
        if (item?.IsCrestWorthy == true) { DrawFlag(entry, "Crest", "Crest"); ImGui.SameLine(); DrawFlag(entry, "ApplyCrest", "Apply crest"); }
    }

    private void DrawCompactCustomization(JObject entry)
    {
        if (entry["Value"]?.Type == JTokenType.Boolean) { var flag = entry.Value<bool>("Value"); if (TabletAppTheme.VisibleCheckbox("Enabled", ref flag)) entry["Value"] = flag; return; }
        var value = entry.Value<int?>("Value") ?? 0;
        if (selected == "Gender")
        {
            DrawGenderToggle(entry, value);
            return;
        }
        var mask = AppearanceCatalog.ToggleMask(selected);
        if (mask != 0) { var flag = value != 0; if (TabletAppTheme.VisibleCheckbox("Enabled", ref flag)) entry["Value"] = flag ? mask : 0; return; }
        if (!compactChoices.TryGetValue(selected, out var choices)) { ImGui.TextDisabled($"Loading choices… (saved: {value})"); return; }
        if (choices.Count == 0) { ImGui.TextDisabled($"Stored value: {value}; no standard choices available."); return; }
        if (selected is "Height" or "MuscleMass" or "BustSize")
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderInt("##value", ref value, 0, 100, "%d%%")) entry["Value"] = value;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Label(selected) + " within this character's supported range (0–100%).");
            return;
        }
        var chosen = choices.FirstOrDefault(c => c.Value == value);
        if (chosen?.Color is { } chosenColor)
        {
            ImGui.ColorButton("##current-colour", chosenColor, ImGuiColorEditFlags.NoTooltip, new Vector2(TabletAppTheme.Px(38)));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(chosen.Label);
            ImGui.SameLine();
        }
        else if (chosen is { Icon: > 0 } && GetIcon(chosen.Icon) is { } chosenTexture)
        {
            ImGui.Image(chosenTexture.Handle, new Vector2(TabletAppTheme.Px(38)));
            if (ImGui.IsItemHovered()) DrawChoiceTooltip(chosen.Icon, chosen.Label);
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(-1);
        if (choices[0].Color is not null)
        {
            var paletteWidth = 8 * (TabletAppTheme.Px(20) + ImGui.GetStyle().ItemSpacing.X) + ImGui.GetStyle().WindowPadding.X * 2;
            ImGui.SetNextWindowSizeConstraints(new Vector2(paletteWidth, 0), new Vector2(paletteWidth, TabletAppTheme.Px(600)));
        }
        if (!ImGui.BeginCombo("##choice", chosen?.Label ?? $"Stored value {value}")) return;
        var visual = choices.Any(choice => choice.Icon != 0 || choice.Color is not null);
        var palette = choices[0].Color is not null;
        var tile = TabletAppTheme.Px(palette ? 20 : 38);
        var columns = palette ? 8 : Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (tile + ImGui.GetStyle().ItemSpacing.X)));
        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            bool picked;
            if (visual)
            {
                if (index % columns != 0) ImGui.SameLine();
                var pos = ImGui.GetCursorScreenPos();
                picked = ImGui.Selectable("##choice-" + choice.Value, choice.Value == value, ImGuiSelectableFlags.None, new Vector2(tile));
                if (choice.Color is { } color) ImGui.GetWindowDrawList().AddRectFilled(pos + new Vector2(3), pos + new Vector2(tile - 3), ImGui.ColorConvertFloat4ToU32(color));
                else if (GetIcon(choice.Icon) is { } texture) ImGui.GetWindowDrawList().AddImage(texture.Handle, pos + new Vector2(3), pos + new Vector2(tile - 3));
                if (ImGui.IsItemHovered()) DrawChoiceTooltip(choice.Icon, choice.Label);
            }
            else picked = ImGui.Selectable(choice.Label + "##" + choice.Value, choice.Value == value);
            if (!picked) continue;
            entry["Value"] = choice.Value;
            if (selected is "Race" or "Clan" or "Gender" or "Face") ReconcileDependencies(selected);
        }
        ImGui.EndCombo();
    }

    private void DrawGenderToggle(JObject entry, int value)
    {
        var size = TabletAppTheme.Px(34);
        var origin = ImGui.GetCursorScreenPos();
        var valid = value is 0 or 1;
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button("##gender-toggle", new Vector2(size)))
        {
            entry["Value"] = value == 0 ? 1 : 0;
            ReconcileDependencies("Gender");
            value = entry.Value<int>("Value");
            compactChoices.Clear();
        }
        ImGui.EndDisabled();
        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(valid ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        var center = origin + new Vector2(size * .43f, size * .43f);
        var radius = size * .19f;
        var thickness = TabletAppTheme.Px(1.8f);
        draw.AddCircle(center, radius, color, 24, thickness);
        if (value == 0)
        {
            var tip = origin + new Vector2(size * .77f, size * .16f);
            draw.AddLine(center + new Vector2(radius * .7f, -radius * .7f), tip, color, thickness);
            draw.AddLine(tip, tip + new Vector2(-size * .18f, 0), color, thickness);
            draw.AddLine(tip, tip + new Vector2(0, size * .18f), color, thickness);
        }
        else if (value == 1)
        {
            draw.AddLine(center + new Vector2(0, radius), center + new Vector2(0, size * .39f), color, thickness);
            draw.AddLine(center + new Vector2(-size * .13f, size * .28f), center + new Vector2(size * .13f, size * .28f), color, thickness);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(valid
                ? $"{(value == 0 ? "Male" : "Female")} — click to switch to {(value == 0 ? "female" : "male")}. Dependent appearance choices will be revalidated."
                : $"Unsupported saved gender value: {value}. Preserved unchanged.");
    }

    private static bool IsFacialFeature(string name) => name is "FacialFeature1" or "FacialFeature2"
        or "FacialFeature3" or "FacialFeature4" or "FacialFeature5" or "FacialFeature6" or "FacialFeature7" or "LegacyTattoo";

    private void DrawFacialFeatureIcons(JObject fields, bool tableRow = true)
    {
        if (tableRow) { ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TableNextColumn(); }
        ImGui.TextWrapped("Facial features");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click an icon to toggle that feature. The checkbox underneath controls whether the preset applies it.");
        if (tableRow) ImGui.TableNextColumn();
        var tile = TabletAppTheme.Px(40);
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / (tile + ImGui.GetStyle().ItemSpacing.X)), 1, 4);
        var index = 0;
        foreach (var property in fields.Properties().Where(p => IsFacialFeature(p.Name)).OrderBy(p => AppearanceLayoutOrder.Rank("Customize", p.Name)))
        {
            if (property.Value is not JObject entry) continue;
            var ready = compactChoices.TryGetValue(property.Name, out var choices);
            var icon = choices?.FirstOrDefault()?.Icon ?? 0;
            // Zero-icon facial options are unavailable for this face. Preserve
            // their saved data without presenting a nonfunctional control.
            if (ready && icon == 0) continue;
            if (index++ % columns != 0) ImGui.SameLine();
            ImGui.PushID(property.Name);
            ImGui.BeginGroup();
            var enabled = (entry.Value<int?>("Value") ?? 0) != 0;
            var position = ImGui.GetCursorScreenPos();
            ImGui.BeginDisabled(!ready);
            if (ImGui.Selectable("##feature", enabled, ImGuiSelectableFlags.DontClosePopups, new Vector2(tile)))
                entry["Value"] = enabled ? 0 : AppearanceCatalog.ToggleMask(property.Name);
            if (GetIcon(icon) is { } texture)
                ImGui.GetWindowDrawList().AddImage(texture.Handle, position + new Vector2(3), position + new Vector2(tile - 3));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                DrawChoiceTooltip(icon, ready ? $"{Label(property.Name)}: {(enabled ? "On" : "Off")}. Click to toggle." : "Loading feature icon…");
            var apply = entry.Value<bool?>("Apply") ?? false;
            if (TabletAppTheme.VisibleCheckbox("##apply-feature", ref apply)) entry["Apply"] = apply;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apply " + Label(property.Name) + ". Unchecked preserves the character's current feature.");
            ImGui.EndDisabled();
            ImGui.EndGroup();
            ImGui.PopID();
        }
    }

    private static void DrawChoiceTooltip(uint icon, string label)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(label);
        if (GetIcon(icon) is { } texture)
        {
            var size = TabletAppTheme.Px(192);
            ImGui.Image(texture.Handle, new Vector2(size));
        }
        ImGui.EndTooltip();
    }

    private static int ReadCompactStain(JObject entry, int channel)
    {
        var array = entry["Stains"] as JArray ?? entry["Stain"] as JArray;
        return array is not null ? channel < array.Count ? array[channel].Value<int>() : 0
            : entry.Value<int?>(channel == 0 ? "Stain" : "Stain2") ?? 0;
    }

    private static void SetCompactStain(JObject entry, int channel, int value)
    {
        var array = entry["Stains"] as JArray ?? entry["Stain"] as JArray;
        if (array is null) entry[channel == 0 ? "Stain" : "Stain2"] = value;
        else if (channel < array.Count) array[channel] = value;
    }

    private void DrawCompactParameter(JObject entry)
    {
        var numeric = entry.Properties().Where(p => p.Name != "Apply" && p.Value.Type is JTokenType.Float or JTokenType.Integer).ToArray();
        var width = Math.Max(1, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * Math.Max(0, numeric.Length - 1)) / Math.Max(1, numeric.Length));
        for (var i = 0; i < numeric.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var field = numeric[i];
            var value = field.Value.Value<float>();
            ImGui.SetNextItemWidth(width);
            if (ImGui.InputFloat("##" + field.Name, ref value, 0, 0, "%.3f") && float.IsFinite(value)) field.Value = value;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Label(field.Name));
        }
        if (entry["Red"] is not null)
        {
            var color = new Vector4(entry.Value<float>("Red"), entry.Value<float>("Green"), entry.Value<float>("Blue"), entry.Value<float?>("Alpha") ?? 1f);
            if (ImGui.ColorButton("##picker", color)) ImGui.OpenPopup("##parameter-picker");
            if (ImGui.BeginPopup("##parameter-picker")) { DrawParameter(entry); ImGui.EndPopup(); }
        }
    }

    private void OpenEquipmentDyes(string slot)
    {
        SelectSection("Materials");
        advancedDyeSlot = slot;
        advancedDyePage = 0;
        materialTargets.Clear();
        captureTarget = null;
        capturedMaterialColors = null;
        materialReadback.Dispose();
        materialHover.Dispose();
        materialScanCharacter = DalamudServices.PlayerState.ContentId;
        string[] slots = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];
        var index = slot == "MainHand" ? 18 : slot == "OffHand" ? 19 : Array.IndexOf(slots, slot);
        // Show the page before attempting native material discovery. A failed
        // scan must never consume the palette click or strand navigation.
        pendingMaterialSlot = index;
        materialTargetIndex = 0;
        materialScanStep = -1;
        materialScanStatus = $"Advanced dyes: {Label(slot)}. Targets use your worn appearance.";
    }
}
