using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private void DrawCustomizationLayout(JObject fields)
    {
        var left = ImGui.GetCursorPosX();
        if (fields["Gender"] is JObject gender && fields["Clan"] is JObject clan)
        {
            ImGui.PushID("identity"); selected = "Gender";
            DrawGenderToggle(gender, gender.Value<int>("Value"));
            ImGui.SameLine(); ImGui.BeginGroup(); selected = "Clan";
            DrawNamedChoice(clan, Unit(267));
            DrawApply(gender, "gender"); ImGui.SameLine();
            var previousApply = clan.Value<bool?>("Apply") ?? false;
            DrawApply(clan, "clan");
            if (previousApply != (clan.Value<bool?>("Apply") ?? false)) SetApply("Race", clan.Value<bool>("Apply"));
            ImGui.SameLine(); ImGui.TextUnformatted("Gender & Clan");
            ImGui.EndGroup(); ImGui.PopID();
        }
        foreach (var name in new[] { "Height", "MuscleMass", "BustSize" })
        {
            if (fields[name] is not JObject entry) continue;
            selected = name; ImGui.PushID(name);
            var value = entry.Value<int>("Value");
            var ready = compactChoices.TryGetValue(name, out var choices) && choices.Count > 0;
            ImGui.BeginDisabled(!ready); ImGui.SetNextItemWidth(Unit(212));
            if (ImGui.SliderInt("##slider", ref value, 0, 100)) entry["Value"] = value;
            ImGui.SameLine(); DrawChoiceNumber(entry, choices); ImGui.EndDisabled();
            ImGui.SameLine(); DrawApply(entry, CustomizeLabel(name));
            ImGui.SameLine(); ImGui.TextUnformatted(CustomizeLabel(name)); ImGui.PopID();
        }
        DrawVisualPair(fields, "Face", "Hairstyle", left);
        DrawVisualPair(fields, "TailShape", "FacePaint", left);
        DrawReferenceFacialFeatures(fields, left);
        foreach (var name in new[] { "Eyebrows", "EyeShape", "Nose", "Jaw", "Mouth" })
        {
            if (fields[name] is not JObject entry) continue;
            selected = name; ImGui.PushID(name); DrawNamedChoice(entry, Unit(212));
            ImGui.SameLine(); compactChoices.TryGetValue(name, out var choices); DrawChoiceNumber(entry, choices);
            ImGui.SameLine(); DrawApply(entry, CustomizeLabel(name));
            ImGui.SameLine(); ImGui.TextUnformatted(CustomizeLabel(name)); ImGui.PopID();
        }
        DrawVisualPair(fields, "SkinColor", "TattooColor", left);
        DrawVisualPair(fields, "HairColor", "HighlightsColor", left);
        DrawVisualPair(fields, "EyeColorLeft", "EyeColorRight", left);
        DrawVisualPair(fields, "LipColor", "FacePaintColor", left);
        foreach (var pair in new[] { new[] { "Highlights", "SmallIris" }, new[] { "Lipstick", "FacePaintReversed" }, new[] { "Wetness" } })
            for (var i = 0; i < pair.Length; i++)
            {
                var name = pair[i]; if (fields[name] is not JObject entry) continue;
                if (i > 0) { ImGui.SameLine(); ImGui.SetCursorPosX(left + Unit(170)); }
                ImGui.PushID(name); DrawAttribute(entry, CustomizeLabel(name), mask: Math.Max(1, AppearanceCatalog.ToggleMask(name)), booleanValue: name == "Wetness"); ImGui.PopID();
            }
    }

    private string CustomizeLabel(string name) => name switch
    {
        "Face" => "Face", "Hairstyle" => "Hairstyle", "TailShape" => "Tail Shape",
        "MuscleMass" => (draft?["Customize"]?["Race"]?.Value<int?>("Value") ?? 0) switch
        {
            4 or 6 or 7 => "Tail Length",
            2 or 3 or 8 => "Ear Length",
            _ => "Muscle Tone",
        }, "Highlights" => "Enable Highlights",
        "Lipstick" => "Enable Lipstick", "FacePaintReversed" => "Reverse Face Paint", "Wetness" => "Force Wetness",
        "EyeColorLeft" => "Left Eye", "EyeColorRight" => "Right Eye", "TattooColor" => "Tattoo Color", _ => Label(name),
    };

    private void SetChoiceValue(JObject entry, int value)
    {
        entry["Value"] = value;
        if (selected is "Race" or "Clan" or "Gender" or "Face") ReconcileDependencies(selected);
    }

    private void DrawChoiceNumber(JObject entry, IReadOnlyList<AppearanceCatalog.Choice>? choices)
    {
        var value = entry.Value<int>("Value");
        ImGui.BeginDisabled(choices is null || choices.Count == 0); ImGui.SetNextItemWidth(Unit(52));
        if (ImGui.InputInt("##number", ref value, 0) && choices?.Any(c => c.Value == value) == true) SetChoiceValue(entry, value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Option ID. Only supported values for this character can be selected.");
        ImGui.SameLine();
        var index = choices is null ? -1 : choices.ToList().FindIndex(c => c.Value == entry.Value<int>("Value"));
        if (ImGui.Button("-", new Vector2(Unit(20), 0)) && choices is { Count: > 0 }) SetChoiceValue(entry, choices[Math.Max(0, index - 1)].Value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Previous supported option"); ImGui.SameLine();
        if (ImGui.Button("+", new Vector2(Unit(20), 0)) && choices is { Count: > 0 }) SetChoiceValue(entry, choices[Math.Min(choices.Count - 1, index + 1)].Value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Next supported option"); ImGui.EndDisabled();
    }

    private void DrawNamedChoice(JObject entry, float width)
    {
        compactChoices.TryGetValue(selected, out var choices); var value = entry.Value<int>("Value");
        var chosen = choices?.FirstOrDefault(c => c.Value == value); ImGui.SetNextItemWidth(width);
        if (!ImGui.BeginCombo("##choice", chosen?.Label ?? $"Option {value}")) return;
        try
        {
            if (choices is null) ImGui.TextDisabled("Loading…");
            else foreach (var option in choices)
                if (ImGui.Selectable(option.Label + "##" + option.Value, option.Value == value)) SetChoiceValue(entry, option.Value);
        }
        finally { ImGui.EndCombo(); }
    }

    private void DrawVisualPair(JObject fields, string first, string second, float left)
    {
        DrawVisualFeature(fields, first); ImGui.SameLine(); ImGui.SetCursorPosX(left + Unit(166)); DrawVisualFeature(fields, second);
    }

    private void DrawVisualFeature(JObject fields, string name)
    {
        if (fields[name] is not JObject entry) { ImGui.Dummy(new Vector2(Unit(162), Unit(44))); return; }
        selected = name; ImGui.PushID(name); ImGui.BeginGroup(); compactChoices.TryGetValue(name, out var choices);
        var chosen = choices?.FirstOrDefault(c => c.Value == entry.Value<int>("Value"));
        var size = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y; var pos = ImGui.GetCursorScreenPos();
        if (ImGui.Button("##visual", new Vector2(size))) ImGui.OpenPopup("##visual-choices");
        DrawChoiceImage(chosen, pos, size);
        if (ImGui.IsItemHovered()) DrawChoiceTooltip(chosen?.Icon ?? 0, CustomizeLabel(name) + ": " + (chosen?.Label ?? "loading") + " — click to choose");
        var palette = choices?.FirstOrDefault()?.Color is not null; var tile = Unit(palette ? 20 : 40);
        var columns = Math.Min(8, Math.Max(1, choices?.Count ?? 1));
        var popupWidth = columns * tile + (columns - 1) * ImGui.GetStyle().ItemSpacing.X
            + ImGui.GetStyle().WindowPadding.X * 2 + ImGui.GetStyle().ScrollbarSize + Unit(4);
        PrepareSelectionPopup(new Vector2(popupWidth / TabletAppTheme.Scale, 450));
        if (ImGui.BeginPopup("##visual-choices"))
        {
            try
            {
                if (choices is null) ImGui.TextDisabled("Loading…");
                else for (var i = 0; i < choices.Count; i++)
                {
                    if (i % columns != 0) ImGui.SameLine(); var choice = choices[i]; var start = ImGui.GetCursorScreenPos();
                    if (ImGui.Selectable("##" + choice.Value, choice.Value == entry.Value<int>("Value"), ImGuiSelectableFlags.None, new Vector2(tile)))
                    { SetChoiceValue(entry, choice.Value); ImGui.CloseCurrentPopup(); }
                    DrawChoiceImage(choice, start, tile);
                    if (ImGui.IsItemHovered()) DrawChoiceTooltip(choice.Icon, choice.Label);
                }
            }
            finally { ImGui.EndPopup(); }
        }
        ImGui.SameLine(); ImGui.BeginGroup(); DrawChoiceNumber(entry, choices);
        DrawApply(entry, CustomizeLabel(name)); ImGui.SameLine(); ImGui.TextUnformatted(CustomizeLabel(name));
        ImGui.EndGroup(); ImGui.EndGroup(); ImGui.PopID();
    }

    private static void DrawChoiceImage(AppearanceCatalog.Choice? choice, Vector2 pos, float size)
    {
        if (choice?.Color is { } color) ImGui.GetWindowDrawList().AddRectFilled(pos + new Vector2(2), pos + new Vector2(size - 2), ImGui.ColorConvertFloat4ToU32(color), Unit(2));
        else if (choice is not null && GetIcon(choice.Icon) is { } texture) ImGui.GetWindowDrawList().AddImage(texture.Handle, pos + new Vector2(2), pos + new Vector2(size - 2));
    }

    private void DrawReferenceFacialFeatures(JObject fields, float left)
    {
        string[] names = ["FacialFeature1", "FacialFeature2", "FacialFeature3", "FacialFeature4", "FacialFeature5", "FacialFeature6", "FacialFeature7", "LegacyTattoo"];
        var size = Unit(44); ImGui.BeginGroup();
        for (var row = 0; row < 2; row++) for (var col = 0; col < 4; col++)
        {
            var name = names[row * 4 + col]; if (col != 0) ImGui.SameLine(); ImGui.PushID("visual-" + name);
            var entry = fields[name] as JObject; compactChoices.TryGetValue(name, out var choices); var choice = choices?.FirstOrDefault();
            ImGui.BeginDisabled(entry is null || choice?.Icon is not > 0); var enabled = entry?.Value<int?>("Value") is > 0; var pos = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable("##icon", enabled, ImGuiSelectableFlags.DontClosePopups, new Vector2(size)) && entry is not null) entry["Value"] = enabled ? 0 : AppearanceCatalog.ToggleMask(name);
            DrawChoiceImage(choice, pos, size);
            if (!enabled) ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(size), ImGui.ColorConvertFloat4ToU32(new Vector4(.4f, 0, 0, .35f)));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) DrawChoiceTooltip(choice?.Icon ?? 0, Label(name) + (enabled ? " — on" : " — off"));
            ImGui.EndDisabled(); ImGui.PopID();
        }
        ImGui.EndGroup(); ImGui.SameLine(); ImGui.SetCursorPosX(left + Unit(216)); ImGui.BeginGroup();
        for (var i = 0; i < 4; i++) { if (i != 0) ImGui.SameLine(); DrawFeatureApply(fields, names[i]); }
        var bits = names.Aggregate(0, (value, name) => value | (fields[name]?.Value<int?>("Value") ?? 0));
        ImGui.SetNextItemWidth(Unit(100));
        if (ImGui.InputInt("##features-mask", ref bits, 0) && bits is >= 0 and <= 255)
            foreach (var name in names) if (fields[name] is JObject entry) entry["Value"] = bits & AppearanceCatalog.ToggleMask(name);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Combined facial-feature and tattoo flags (0–255).");
        ImGui.TextUnformatted("Facial Features & Tattoos");
        for (var i = 4; i < 8; i++) { if (i != 4) ImGui.SameLine(); DrawFeatureApply(fields, names[i]); }
        ImGui.EndGroup();
    }

    private static void DrawFeatureApply(JObject fields, string name)
    {
        ImGui.PushID("apply-" + name);
        if (fields[name] is JObject entry) DrawApply(entry, Label(name)); else ImGui.Dummy(new Vector2(ImGui.GetFrameHeight()));
        ImGui.PopID();
    }
}
