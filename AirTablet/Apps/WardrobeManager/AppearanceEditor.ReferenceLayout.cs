using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private static float Unit(float value) => TabletAppTheme.Px(value);

    private static void DrawApply(JObject entry, string label, string key = "Apply")
    {
        var apply = entry.Value<bool?>(key) ?? false;
        if (ImGui.Checkbox("##" + key, ref apply)) entry[key] = apply;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apply " + label + ". Unchecked keeps the character's current setting.");
    }

    private static void DrawAttribute(JObject entry, string label, string valueKey = "Value", string applyKey = "Apply", int mask = 1, bool booleanValue = false)
    {
        var value = entry[valueKey];
        var enabled = value?.Type == JTokenType.Boolean ? value.Value<bool>() : (value?.Value<int>() ?? 0) != 0;
        var apply = entry.Value<bool?>(applyKey) ?? false;
        var size = ImGui.GetFrameHeight();
        var start = ImGui.GetCursorScreenPos();
        if (ImGui.Button("##state-" + label, new Vector2(size)))
        {
            AppearanceAttributeState.Cycle(entry, valueKey, applyKey, mask, booleanValue);
            enabled = AppearanceAttributeState.Enabled(entry, valueKey);
            apply = entry.Value<bool?>(applyKey) ?? false;
        }
        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.ColorConvertFloat4ToU32(!apply ? new Vector4(.8f, .8f, .8f, 1) : enabled ? new Vector4(.25f, .9f, .35f, 1) : new Vector4(1, .22f, .22f, 1));
        var thickness = Unit(2);
        if (!apply) draw.AddCircleFilled(start + new Vector2(size / 2), size * .24f, color);
        else if (enabled)
        {
            draw.AddLine(start + new Vector2(size * .2f, size * .5f), start + new Vector2(size * .43f, size * .73f), color, thickness);
            draw.AddLine(start + new Vector2(size * .43f, size * .73f), start + new Vector2(size * .8f, size * .23f), color, thickness);
        }
        else
        {
            draw.AddLine(start + new Vector2(size * .23f), start + new Vector2(size * .77f), color, thickness);
            draw.AddLine(start + new Vector2(size * .23f, size * .77f), start + new Vector2(size * .77f, size * .23f), color, thickness);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(label + ": " + (!apply ? "keep as is" : enabled ? "enable" : "disable") + ". Click to cycle: keep → enable → disable → keep.");
        ImGui.SameLine(); ImGui.TextUnformatted(label);
    }

    private void DrawEquipmentLayout(JObject fields)
    {
        string? openDyes = null;
        var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();
        foreach (var property in fields.Properties().Where(p => p.Value is JObject item && item["ItemId"] is not null)
                     .OrderBy(p => AppearanceLayoutOrder.Rank("Equipment", p.Name)))
        {
            selected = property.Name;
            var entry = (JObject)property.Value;
            var id = entry.Value<ulong>("ItemId");
            Item? item = id <= uint.MaxValue && sheet.TryGetRow((uint)id, out var row) ? row : null;
            if (selected == "OffHand" && (item is null || !FitsSlot(item.Value, "OffHand") || string.IsNullOrWhiteSpace(item.Value.Name.ExtractText()))) continue;
            ImGui.PushID(selected);
            ImGui.BeginGroup();
            var iconSize = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y;
            if (item is { } gear && GetIcon(gear.Icon) is { } texture) ImGui.Image(texture.Handle, new Vector2(iconSize));
            else ImGui.Dummy(new Vector2(iconSize));
            ImGui.SameLine();
            ImGui.BeginGroup();
            var itemWidth = Unit(280);
            DrawItemSelector(entry, item, itemWidth);
            ImGui.SameLine(); DrawApply(entry, Label(selected));
            ImGui.SameLine(); ImGui.TextUnformatted(Label(selected));
            for (var channel = 0; channel < 2; channel++)
            {
                if (channel > 0) ImGui.SameLine();
                var index = channel;
                var width = (itemWidth - ImGui.GetStyle().ItemSpacing.X) / 2;
                if (item?.DyeCount > channel) DrawStain("Dye " + (channel + 1), ReadCompactStain(entry, channel), value => SetCompactStain(entry, index, value), width, false);
                else { ImGui.BeginDisabled(); ImGui.Button("##no-dye-" + channel, new Vector2(width, ImGui.GetFrameHeight())); ImGui.EndDisabled(); }
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(item?.DyeCount is not > 0);
            DrawApply(entry, "standard dyes", "ApplyStain");
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (DrawPaletteButton()) openDyes = selected;
            ImGui.EndGroup();
            ImGui.EndGroup();
            ImGui.PopID();
        }
        if (draft!["Bonus"]?["Glasses"] is JObject facewear)
        {
            selected = "Glasses";
            ImGui.PushID("Glasses");
            var size = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y;
            if (FacewearId.TryRead(facewear, out var facewearId) && DalamudServices.DataManager.GetExcelSheet<Glasses>().TryGetRow(facewearId, out var glasses) && GetIcon((uint)glasses.Icon) is { } icon)
                ImGui.Image(icon.Handle, new Vector2(size));
            else ImGui.Dummy(new Vector2(size));
            ImGui.SameLine(); DrawFacewearSelector(facewear, Unit(280));
            ImGui.SameLine(); DrawApply(facewear, "facewear"); ImGui.SameLine(); ImGui.TextUnformatted("Facewear");
            ImGui.PopID();
        }
        ImGui.Spacing();
        var first = true;
        foreach (var (key, label, valueKey) in new[] { ("Hat", "Hat Visible", "Show"), ("Visor", "Visor Toggled", "IsToggled"), ("Weapon", "Weapon Visible", "Show"), ("VieraEars", "Ears Visible", "Show") })
        {
            if (fields[key] is not JObject entry) continue;
            if (!first) ImGui.SameLine(); first = false;
            ImGui.PushID(key); DrawAttribute(entry, label, valueKey); ImGui.PopID();
        }
        first = true;
        foreach (var (key, label) in new[] { ("Head", "Head Crest"), ("Body", "Chest Crest"), ("OffHand", "Shield Crest") })
        {
            if (fields[key] is not JObject entry) continue;
            if (!first) ImGui.SameLine(); first = false;
            ImGui.PushID("crest-" + key); DrawAttribute(entry, label, "Crest", "ApplyCrest"); ImGui.PopID();
        }
        // Navigation happens after all row IDs/groups are closed.
        if (openDyes is not null) OpenEquipmentDyes(openDyes);
    }

    private JObject? copiedParameter;
    private void DrawParameterLayout(JObject fields)
    {
        var colorAreaWidth = Math.Clamp(ImGui.GetContentRegionAvail().X - Unit(220), Unit(350), Unit(590));
        foreach (var property in fields.Properties().OrderBy(p => AppearanceLayoutOrder.Rank("Parameters", p.Name)))
        {
            if (property.Value is not JObject entry) continue;
            selected = property.Name;
            ImGui.PushID(selected);
            if (entry["Red"] is not null)
            {
                if (ImGui.SmallButton("C")) copiedParameter = (JObject)entry.DeepClone();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy " + Label(selected));
                ImGui.SameLine(); ImGui.BeginDisabled(copiedParameter?["Red"] is null);
                if (ImGui.SmallButton("P") && copiedParameter is not null)
                    foreach (var key in new[] { "Red", "Green", "Blue", "Alpha" })
                        if (entry[key] is not null && copiedParameter[key] is not null) entry[key] = copiedParameter[key]!.DeepClone();
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Paste colour channels; keep this row's Apply setting.");
                ImGui.SameLine();
                foreach (var key in new[] { "Red", "Green", "Blue", "Alpha" })
                {
                    if (entry[key] is null) continue;
                    var value = entry.Value<float>(key);
                    ImGui.SetNextItemWidth((colorAreaWidth - Unit(64)) / (entry["Alpha"] is null ? 3 : 4) - ImGui.GetStyle().ItemSpacing.X);
                    if (ImGui.InputFloat("##" + key, ref value, 0, 0, key[..1] + ":%.3f") && float.IsFinite(value)) entry[key] = value;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(key + " channel");
                    ImGui.SameLine();
                }
                var color = new Vector4(entry.Value<float>("Red"), entry.Value<float>("Green"), entry.Value<float>("Blue"), entry.Value<float?>("Alpha") ?? 1);
                if (ImGui.ColorButton("##colour", color, ImGuiColorEditFlags.None, new Vector2(ImGui.GetFrameHeight()))) ImGui.OpenPopup("##parameter-colour");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open colour picker, hex entry, and colour copy/paste.");
                PrepareSelectionPopup(new Vector2(450, 550));
                if (ImGui.BeginPopup("##parameter-colour")) { DrawParameter(entry); ImGui.EndPopup(); }
            }
            else
            {
                var field = entry.Properties().FirstOrDefault(p => p.Name != "Apply" && p.Value.Type is JTokenType.Integer or JTokenType.Float);
                if (field is not null)
                {
                    var value = field.Value.Value<float>();
                    ImGui.SetNextItemWidth(colorAreaWidth);
                    if (field.Name == "Percentage")
                    {
                        var percent = value * 100f;
                        if (ImGui.SliderFloat("##value", ref percent, 0, 100, "%.2f%%")) field.Value = percent / 100f;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Label(selected) + ": 0–100%. Ctrl-click for direct entry.");
                    }
                    else if (ImGui.InputFloat("##value", ref value, .01f, .1f, "%.3f") && float.IsFinite(value)) field.Value = value;
                }
            }
            ImGui.SameLine(); DrawApply(entry, Label(selected));
            ImGui.SameLine(); ImGui.TextUnformatted(Label(selected));
            ImGui.PopID();
        }
    }
}
