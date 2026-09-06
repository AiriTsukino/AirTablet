using System.Globalization;
using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private string advancedDyeSlot = string.Empty;
    private int advancedDyePage;
    private int pendingMaterialSlot = -1;
    private string materialDiscoveryError = string.Empty;

    private static bool DrawPaletteButton()
    {
        var size = ImGui.GetFrameHeight();
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##advanced-palette", new Vector2(size));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(size), ImGui.ColorConvertFloat4ToU32(ImGui.IsItemHovered() ? TabletAppTheme.Accent : TabletAppTheme.SurfaceRaised), 5f);
        var center = pos + new Vector2(size * 0.5f);
        draw.AddCircleFilled(center, size * 0.35f, ImGui.GetColorU32(ImGuiCol.Text));
        Vector4[] colors = [new(0.8f, 0.2f, 0.4f, 1), new(0.4f, 0.2f, 0.8f, 1), new(0.2f, 0.6f, 0.8f, 1)];
        for (var i = 0; i < colors.Length; i++)
            draw.AddCircleFilled(center + new Vector2((i - 1) * size * 0.17f, -size * 0.13f), size * 0.06f, ImGui.ColorConvertFloat4ToU32(colors[i]));
        draw.AddCircleFilled(center + new Vector2(size * 0.15f, size * 0.14f), size * 0.1f, ImGui.ColorConvertFloat4ToU32(TabletAppTheme.SurfaceRaised));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Advanced dyes for this equipment slot");
        return clicked;
    }

    private void DrawAdvancedDyePopup()
    {
        if (pendingMaterialSlot >= 0)
        {
            var slot = pendingMaterialSlot;
            pendingMaterialSlot = -1;
            materialDiscoveryError = string.Empty;
            try { materialTargets.AddRange(ReadMaterialTargets(slot)); }
            catch (Exception ex)
            {
                materialDiscoveryError = "Could not read worn materials: " + ex.Message;
                DalamudServices.Log.Warning(ex, "WardrobeManager material discovery failed for slot {Slot}.", advancedDyeSlot);
            }
        }
        ImGui.TextColored(TabletAppTheme.AccentHover, Label(advancedDyeSlot) + " · Advanced dyes");
        ImGui.TextWrapped("Targets use the worn item. Hover a row's target button to identify its region; edit colours here and save the preset in Appearance Studio.");
        if (materialTargets.Count == 0) ImGui.TextWrapped("No supported dye materials were found. Wear this item before reopening its palette.");
        if (materialDiscoveryError.Length > 0) ImGui.TextWrapped(materialDiscoveryError);
        if (ImGui.SmallButton("Refresh worn materials")) { OpenEquipmentDyes(advancedDyeSlot); return; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-read materials after equipping or redrawing the item. Keeps your saved draft overrides.");
        if (materialTargets.Count > 0)
        {
            if (ImGui.BeginTabBar("##materials"))
            {
                for (var i = 0; i < materialTargets.Count; i++)
                {
                    var target = materialTargets[i];
                    var materialNumber = (int)((target.Key >> 8) & 255);
                    var tabOpen = ImGui.BeginTabItem($"Mat {(char)('A' + materialNumber)}##{target.Key}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(target.Path);
                    if (tabOpen)
                    {
                        if (materialTargetIndex != i) { materialTargetIndex = i; advancedDyePage = 0; }
                        DrawAdvancedMaterialRows(target);
                        ImGui.EndTabItem();
                    }
                }
                ImGui.EndTabBar();
            }
        }
    }

    private void DrawAdvancedMaterialRows(MaterialTarget target)
    {
        if (captureTarget != target)
        {
            materialHover.Dispose();
            materialReadback.Dispose();
            capturedMaterialColors = null;
            captureTarget = target;
            var slot = (target.Key >> 24) switch { 2 => 18, 3 => 19, _ => (int)((target.Key >> 16) & 255) };
            capturedMaterialTexture = materialScanCharacter == DalamudServices.PlayerState.ContentId
                && ReadMaterialTargets(slot).Any(candidate => candidate == target) ? GetMaterialTexture(target.Key) : 0;
            materialReadback.Begin(capturedMaterialTexture);
        }
        if (materialReadback.Pending) capturedMaterialColors = materialReadback.Poll() ?? capturedMaterialColors;
        if (capturedMaterialColors is null)
        {
            ImGui.TextWrapped(materialReadback.Pending ? "Reading live colours…" : "Live colours unavailable. Existing saved rows can still be edited.");
            if (!materialReadback.Pending && ImGui.Button("Retry capture")) captureTarget = null;
        }
        if (ImGui.Button(target.Mode == "Dawntrail" ? "Row pairs 1–8" : "Rows 1–16")) advancedDyePage = 0;
        if (target.Rows > 16) { ImGui.SameLine(); if (ImGui.Button(target.Mode == "Dawntrail" ? "Row pairs 9–16" : "Rows 17–32")) advancedDyePage = 1; }
        var hasSheen = target.Mode == "Dawntrail";
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, TabletAppTheme.Px(new Vector2(5, 4)));
        if (!ImGui.BeginTable("##dye-rows", hasSheen ? 6 : 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders))
        { ImGui.PopStyleVar(); return; }
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(32));
        ImGui.TableSetupColumn("Row", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(44));
        ImGui.TableSetupColumn("Colours", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(92));
        ImGui.TableSetupColumn(hasSheen ? "Base\nRoughness / Metalness" : "Base\nGloss / Specular", ImGuiTableColumnFlags.WidthStretch, 2);
        if (hasSheen) ImGui.TableSetupColumn("Sheen\nStrength / Tint / Aperture", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(82));
        ImGui.TableHeadersRow();
        for (var row = advancedDyePage * 16; row < Math.Min(target.Rows, (advancedDyePage + 1) * 16); row++)
        {
            var key = (target.Key | (uint)row).ToString("X8", CultureInfo.InvariantCulture);
            var stored = draft!["Materials"]?[key] as JObject;
            JObject? entry = stored;
            if (entry is null && capturedMaterialColors is not null)
            {
                try { entry = MaterialRowDraft.FromLiveColors(target.Mode, capturedMaterialColors.AsSpan(row * 32, 32)); }
                catch (ArgumentException) { }
            }
            ImGui.PushID(key);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            ImGui.Button("+##target");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Highlight this material row on the worn character while hovered.");
            if (ImGui.IsItemHovered() && capturedMaterialColors is not null && materialScanCharacter == DalamudServices.PlayerState.ContentId)
                materialHover.Show(target.Key, row, capturedMaterialColors, capturedMaterialTexture);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(target.Mode == "Dawntrail" ? $"{row / 2 + 1}{(row % 2 == 0 ? "A" : "B")}" : (row + 1).ToString());
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(entry is null);
            var names = new[] { "Diffuse", "Specular", "Emissive" };
            for (var channel = 0; channel < names.Length; channel++)
            {
                if (channel > 0) ImGui.SameLine();
                var name = names[channel];
                var color = entry is null ? Vector4.Zero : new Vector4(entry.Value<float>(name + "R"), entry.Value<float>(name + "G"), entry.Value<float>(name + "B"), 1);
                if (ImGui.ColorButton(name, color, ImGuiColorEditFlags.NoTooltip, new Vector2(TabletAppTheme.Px(26)))) ImGui.OpenPopup(name + "##edit");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(name switch { "Diffuse" => "Diffuse: the surface's base colour.", "Specular" => "Specular: the colour of reflected highlights.", _ => "Emissive: self-lit colour, independent of scene lighting." });
                PrepareSelectionPopup(new Vector2(450, 380));
                if (ImGui.BeginPopup(name + "##edit"))
                {
                    if (entry is not null)
                    {
                        var rgb = new Vector3(color.X, color.Y, color.Z); var alpha = 1f;
                        selected = key;
                        if (DrawColorControls(name, ref rgb, ref alpha, false))
                        { entry[name + "R"] = rgb.X; entry[name + "G"] = rgb.Y; entry[name + "B"] = rgb.Z; StoreMaterialRow(key, entry); }
                    }
                    ImGui.EndPopup();
                }
            }
            ImGui.TableNextColumn();
            DrawMaterialScalars(key, entry, hasSheen ? ["Roughness", "Metalness"] : ["Gloss", "SpecularA"]);
            if (hasSheen) { ImGui.TableNextColumn(); DrawMaterialScalars(key, entry, ["Sheen", "SheenTint", "SheenAperture"]); }
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(entry is null);
            if (ImGui.SmallButton("C") && entry is not null) ImGui.SetClipboardText(MaterialRowClipboard.Copy(entry));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this colour row to the clipboard.");
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("P"))
            {
                if (MaterialRowClipboard.TryPaste(ImGui.GetClipboardText(), target.Mode, out var pasted)) StoreMaterialRow(key, pasted);
                else error = "Clipboard does not contain a valid colour row for this material type.";
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Paste a colour row from the clipboard. Material types must match.");
            ImGui.SameLine();
            if (ImGui.SmallButton("R"))
            {
                if (original!["Materials"]?[key] is JObject initial) StoreMaterialRow(key, (JObject)initial.DeepClone(), false);
                else (draft!["Materials"] as JObject)?.Remove(key);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset this row to the preset as it was when Appearance Studio opened. Removes a newly added override.");
            ImGui.PopID();
        }
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private void DrawMaterialScalars(string key, JObject? entry, string[] fields)
    {
        var width = Math.Max(1, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * (fields.Length - 1)) / fields.Length);
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (index > 0) ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.SetNextItemWidth(width);
            var value = entry?.Value<float?>(field) ?? (field == "Roughness" ? .5f : field == "Gloss" ? 20f : field == "SpecularA" ? 1f : 0f);
            if (ImGui.InputFloat("##" + field, ref value, 0, 0, "%.2f") && float.IsFinite(value) && entry is not null)
            { entry[field] = Math.Clamp(value, field == "SheenAperture" ? .001f : 0f, 65504); StoreMaterialRow(key, entry); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Label(field));
            ImGui.EndGroup();
        }
    }

    private void StoreMaterialRow(string key, JObject row, bool activate = true)
    {
        if (activate) { row["Enabled"] = true; row["Revert"] = false; }
        if (draft!["Materials"] is not JObject materials) draft["Materials"] = materials = new JObject();
        materials[key] = row;
    }
}
