using System.Numerics;
using System.Text.RegularExpressions;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed class AppearanceEditor
{
    private const string Modal = "Appearance Studio##WardrobeManager";
    private readonly AppearanceCatalog catalog = new();
    private JObject? original;
    private JObject? draft;
    private WardrobePreset? preset;
    private string section = "Customize";
    private string selected = "Race";
    private string search = string.Empty;
    private string error = string.Empty;
    private bool showChoices;
    private string hexOwner = string.Empty;
    private string hexText = string.Empty;
    private string lastHexColor = string.Empty;
    private bool hexInvalid;

    public void Open(WardrobePreset owner, JObject design)
    {
        preset = owner;
        original = (JObject)design.DeepClone();
        draft = (JObject)design.DeepClone();
        section = "Customize";
        selected = "Race";
        search = error = string.Empty;
        showChoices = false;
        hexOwner = string.Empty;
        TabletAppTheme.OpenCenteredModal(Modal);
    }

    public void Draw(Func<WardrobePreset, JObject, JObject, string?> save)
    {
        if (preset is null || draft is null || original is null || !TabletAppTheme.BeginCenteredModal(Modal, preferredWidth: 900f)) return;
        ImGui.TextWrapped(preset.Name);
        ImGui.TextWrapped("Choose a feature, edit its value, then save to the linked Glamourer design.");
        if (ImGui.Button("Customization")) SelectSection("Customize");
        ImGui.SameLine();
        if (ImGui.Button("Advanced Customization")) SelectSection("Parameters");
        ImGui.Separator();
        var bodyHeight = MathF.Max(TabletAppTheme.Px(90f), ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - TabletAppTheme.Px(12f));
        if (ImGui.BeginChild("##appearance-body", new Vector2(0, bodyHeight), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (ImGui.BeginTable("##appearance-columns", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Features", ImGuiTableColumnFlags.WidthStretch, 0.40f);
                ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch, 0.60f);
                ImGui.TableNextColumn();
                var paneHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().CellPadding.Y * 2);
                if (ImGui.BeginChild("##appearance-fields", new Vector2(0, paneHeight), true)) DrawFields();
                ImGui.EndChild();
                ImGui.TableNextColumn();
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, TabletAppTheme.Px(new Vector2(18f, 12f)));
                if (ImGui.BeginChild("##appearance-value", new Vector2(0, paneHeight), false, ImGuiWindowFlags.AlwaysUseWindowPadding)) DrawSelected();
                ImGui.EndChild();
                ImGui.PopStyleVar();
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();
        if (ImGui.Button("Save to Glamourer"))
        {
            error = save(preset, original, draft) ?? string.Empty;
            if (error.Length == 0) { TabletAppTheme.CloseCenteredModal(); preset = null; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { TabletAppTheme.CloseCenteredModal(); preset = null; }
        TabletAppTheme.EndCenteredModal();
    }

    private void SelectSection(string value)
    {
        section = value;
        selected = string.Empty;
        search = string.Empty;
        showChoices = false;
        hexOwner = string.Empty;
    }

    private void DrawFields()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Find a feature...", ref search, 100);
        if (draft![section] is not JObject fields) { ImGui.TextWrapped("This design does not contain these settings."); return; }
        foreach (var property in fields.Properties())
        {
            if (property.Value is not JObject) continue;
            var label = Label(property.Name);
            if (!label.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (selected.Length == 0) selected = property.Name;
            // Wrapped selectable labels keep long field names inside the column.
            var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
            var height = Math.Max(ImGui.GetFrameHeight(), ImGui.CalcTextSize(label, false, width - 8).Y + 8);
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable("##" + property.Name, selected == property.Name, ImGuiSelectableFlags.None, new Vector2(width, height)))
            {
                selected = property.Name;
                showChoices = false;
                hexOwner = string.Empty;
            }
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), pos + new Vector2(4, 4),
                ImGui.GetColorU32(ImGuiCol.Text), label, width - 8);
        }
    }

    private void DrawSelected()
    {
        if (draft![section]?[selected] is not JObject entry) { ImGui.TextWrapped("Select a feature to edit."); return; }
        ImGui.TextWrapped(Label(selected));
        var apply = entry.Value<bool?>("Apply") ?? false;
        ImGui.BeginDisabled(section == "Customize" && selected == "BodyType");
        if (TabletAppTheme.VisibleCheckbox("Apply with this preset", ref apply))
        {
            entry["Apply"] = apply;
            if (section == "Customize" && selected is "Race" or "Clan")
            {
                SetApply("Race", apply);
                SetApply("Clan", apply);
            }
        }
        ImGui.EndDisabled();
        if (section == "Customize" && selected == "BodyType") ImGui.TextWrapped("Body-type application is managed by Glamourer.");
        ImGui.TextWrapped("Unchecked values are saved but do not replace the character's current value when applied.");
        ImGui.Separator();
        if (section == "Parameters") DrawParameter(entry);
        else DrawCustomization(entry);
        if (section == "Customize" && entry["Value"]?.Type == JTokenType.Integer && selected is not ("Race" or "Gender" or "Clan" or "BodyType"))
        {
            if (ImGui.TreeNode("Custom value (advanced)"))
            {
                ImGui.TextWrapped("For NPC or modded options, like Glamourer's manual override. Invalid values may be rejected by Glamourer; use standard choices unless you know the required value.");
                var raw = entry.Value<int>("Value");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##raw", ref raw)) entry["Value"] = Math.Clamp(raw, 0, 255);
                ImGui.TreePop();
            }
        }
        ImGui.Spacing();
        if (ImGui.Button("Reset this feature"))
        {
            // Race/clan/face changes can reconcile dependent fields; restore the
            // group together rather than leave an impossible combination.
            if (section == "Customize" && selected is "Race" or "Clan" or "Gender" or "Face")
                draft[section] = original![section]!.DeepClone();
            else if (original![section]?[selected] is JObject saved) draft[section]![selected] = saved.DeepClone();
            hexOwner = string.Empty;
        }
        if (error.Length > 0) ImGui.TextWrapped(error);
        if (catalog.Error.Length > 0) ImGui.TextWrapped(catalog.Error);
        ImGui.Spacing();
        ImGui.TextWrapped("Saving uses Glamourer's replacement-design API. It also saves this preset's current mod associations. Automation rules referencing the old design ID may need updating.");
    }

    private void DrawParameter(JObject entry)
    {
        if (entry["Red"] is not null && entry["Green"] is not null && entry["Blue"] is not null)
        {
            var color = new Vector3(entry.Value<float>("Red"), entry.Value<float>("Green"), entry.Value<float>("Blue"));
            var hasAlpha = entry["Alpha"] is not null;
            var alpha = hasAlpha ? entry.Value<float>("Alpha") : 1f;
            if (DrawColorControls("advanced", ref color, ref alpha, hasAlpha))
            {
                entry["Red"] = color.X; entry["Green"] = color.Y; entry["Blue"] = color.Z;
                if (hasAlpha) entry["Alpha"] = alpha;
            }
            ImGui.TextWrapped("RGB values (HDR values are supported)");
        }
        foreach (var field in entry.Properties().ToArray())
        {
            if (field.Name == "Apply" || field.Value.Type is not (JTokenType.Float or JTokenType.Integer)) continue;
            var value = field.Value.Value<float>();
            ImGui.TextWrapped(Label(field.Name));
            ImGui.SetNextItemWidth(-1);
            if (field.Name == "Percentage")
            {
                value *= 100;
                if (ImGui.SliderFloat("##" + field.Name, ref value, -100, 300, "%.2f%%")) field.Value = value / 100;
            }
            else if (ImGui.InputFloat("##" + field.Name, ref value, 0.01f, 0.1f) && float.IsFinite(value)) field.Value = value;
        }
    }

    private bool DrawColorControls(string id, ref Vector3 color, ref float alpha, bool hasAlpha,
        Func<Vector3, Vector3>? snap = null)
    {
        var owner = $"{preset?.Id}/{section}/{selected}/{id}";
        var available = ImGui.GetContentRegionAvail().X;
        var gap = TabletAppTheme.Px(16f);
        var previewWidth = Math.Min(TabletAppTheme.Px(140f), available * 0.43f);
        var wheelWidth = Math.Min(TabletAppTheme.Px(220f), Math.Max(1f, available - previewWidth - gap));
        var changed = false;
        ImGui.PushID(id);
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(wheelWidth);
        if (ImGui.ColorPicker3("##wheel", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoOptions
                | ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.PickerHueWheel))
        {
            if (snap is not null) color = snap(color);
            changed = true;
        }
        ImGui.EndGroup();
        ImGui.SameLine(0, gap);
        // A child constrains text, input and buttons to the preview column.
        // It scrolls on very small layouts instead of spilling over the pane.
        var previewHeight = TabletAppTheme.Px(58f) + ImGui.GetTextLineHeightWithSpacing() * (hexInvalid ? 5 : 2)
            + ImGui.GetFrameHeightWithSpacing() * 4;
        if (ImGui.BeginChild("##color-preview", new Vector2(previewWidth, Math.Max(wheelWidth, previewHeight)), false))
        {
            var formatted = AppearanceColorHex.Format(color, alpha, hasAlpha);
            if (owner != hexOwner || formatted != lastHexColor || changed)
            {
                hexOwner = owner;
                hexText = formatted;
                lastHexColor = formatted;
                hexInvalid = false;
            }
            ImGui.TextWrapped("Selected color");
            ImGui.ColorButton("##swatch", new Vector4(Vector3.Clamp(color, Vector3.Zero, Vector3.One), Math.Clamp(alpha, 0f, 1f)),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), TabletAppTheme.Px(46f)));
            ImGui.TextUnformatted(hasAlpha ? "Hex (RGBA)" : "Hex (RGB)");
            ImGui.SetNextItemWidth(-1f);
            var inputAccent = TabletAppTheme.RememberedAccent;
            var inputHover = TabletAppTheme.RememberedAccentHover;
            var inputSurface = TabletAppTheme.RememberedSurfaceRaised;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Lerp(inputSurface, inputAccent, 0.22f) with { W = 1f });
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Lerp(inputSurface, inputHover, 0.55f) with { W = 1f });
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Lerp(inputSurface, inputAccent, 0.4f) with { W = 1f });
            ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Lerp(inputHover, Vector4.One, 0.22f) with { W = 1f });
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Math.Max(1f, TabletAppTheme.Px(1.5f)));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, TabletAppTheme.Px(3f));
            var applyHex = ImGui.InputText("##hex", ref hexText, 32, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);
            applyHex |= ImGui.Button("Apply hex", new Vector2(-1, 0));
            if (ImGui.Button("Copy", new Vector2(-1, 0))) ImGui.SetClipboardText(formatted);
            if (ImGui.Button("Paste", new Vector2(-1, 0)))
            {
                hexText = ImGui.GetClipboardText();
                applyHex = true;
            }
            if (applyHex)
            {
                hexInvalid = !AppearanceColorHex.TryParse(hexText, hasAlpha, out var parsed, out var parsedAlpha);
                if (!hexInvalid)
                {
                    color = snap is null ? parsed : snap(parsed);
                    if (parsedAlpha.HasValue) alpha = parsedAlpha.Value;
                    hexText = lastHexColor = AppearanceColorHex.Format(color, alpha, hasAlpha);
                    changed = true;
                }
            }
            if (hexInvalid) ImGui.TextWrapped(hasAlpha ? "Use #RRGGBB or #RRGGBBAA." : "Use #RRGGBB.");
        }
        ImGui.EndChild();
        ImGui.PopID();
        if (color.X < 0 || color.Y < 0 || color.Z < 0 || color.X > 1 || color.Y > 1 || color.Z > 1)
            ImGui.TextWrapped("Hex shows the 0-1 color range; HDR values remain unchanged unless you apply a new color.");
        return changed;
    }

    private void DrawCustomization(JObject entry)
    {
        if (entry["Value"]?.Type == JTokenType.Boolean)
        {
            var value = entry.Value<bool>("Value");
            if (TabletAppTheme.VisibleCheckbox("Enabled", ref value)) entry["Value"] = value;
            return;
        }
        var current = entry.Value<int?>("Value") ?? 0;
        var choices = catalog.Choices(draft!, selected);
        if (AppearanceCatalog.ToggleMask(selected) is var mask && mask != 0)
        {
            DrawIcon(choices.FirstOrDefault()?.Icon ?? 0, 68);
            var value = current != 0;
            if (TabletAppTheme.VisibleCheckbox("Enabled", ref value)) entry["Value"] = value ? mask : 0;
            return;
        }
        if (selected is "Height" or "MuscleMass" or "BustSize" && choices.Count > 0)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderInt("##percentage", ref current, 0, 100, "%d%%")) entry["Value"] = current;
            return;
        }
        if (choices.Count == 0)
        {
            ImGui.TextWrapped($"Stored value: {current}. This feature has no standard choices for the current character. The saved value is preserved.");
            return;
        }
        var chosen = choices.FirstOrDefault(c => c.Value == current);
        DrawIcon(chosen?.Icon ?? 0, 76);
        ImGui.TextWrapped(chosen?.Label ?? $"Stored value {current} (non-standard)");
        if (choices[0].Color is not null)
        {
            var color = chosen?.Color is { } rgba ? new Vector3(rgba.X, rgba.Y, rgba.Z) : Vector3.One;
            var alpha = 1f;
            Vector3 Snap(Vector3 requested)
            {
                var match = choices.MinBy(c => Vector3.DistanceSquared(requested, new Vector3(c.Color!.Value.X, c.Color.Value.Y, c.Color.Value.Z)))!;
                var c = match.Color!.Value;
                return new Vector3(c.X, c.Y, c.Z);
            }
            if (DrawColorControls("palette", ref color, ref alpha, false, Snap))
            {
                entry["Value"] = choices.MinBy(c => Vector3.DistanceSquared(color, new Vector3(c.Color!.Value.X, c.Color.Value.Y, c.Color.Value.Z)))!.Value;
            }
            ImGui.TextWrapped("Snaps to the nearest game palette color. Use Advanced Customization for unrestricted colors.");
        }
        if (ImGui.Button(showChoices ? "Hide choices" : "Choose an option", new Vector2(-1, 0))) showChoices = !showChoices;
        if (!showChoices) return;
        // This is an owned, scrollable choice panel, never a free-floating combo
        // window. It cannot extend beyond the modal or tablet boundaries.
        if (ImGui.BeginChild("##choices", new Vector2(0, TabletAppTheme.Px(220)), true))
        {
            var tile = TabletAppTheme.Px(64);
            var columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (tile + ImGui.GetStyle().ItemSpacing.X)));
            var visualChoices = choices.Any(c => c.Icon != 0 || c.Color is not null);
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                if (!visualChoices)
                {
                    if (ImGui.Selectable(choice.Label + "##" + choice.Value, choice.Value == current))
                    {
                        entry["Value"] = choice.Value;
                        if (selected is "Race" or "Clan" or "Gender" or "Face") ReconcileDependencies(selected);
                    }
                    continue;
                }
                if (i % columns != 0) ImGui.SameLine();
                ImGui.PushID(choice.Value);
                ImGui.BeginGroup();
                var pos = ImGui.GetCursorScreenPos();
                var picked = ImGui.Selectable("##tile", choice.Value == current, ImGuiSelectableFlags.None, new Vector2(tile, tile));
                if (choice.Color is { } c) ImGui.GetWindowDrawList().AddRectFilled(pos + new Vector2(5), pos + new Vector2(tile - 5), ImGui.ColorConvertFloat4ToU32(c), 6);
                else if (GetIcon(choice.Icon) is { } texture) ImGui.GetWindowDrawList().AddImage(texture.Handle, pos + new Vector2(4), pos + new Vector2(tile - 4));
                else ImGui.GetWindowDrawList().AddText(pos + new Vector2(8), ImGui.GetColorU32(ImGuiCol.Text), choice.Value.ToString());
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(choice.Label);
                ImGui.TextUnformatted(choice.Value.ToString());
                ImGui.EndGroup();
                ImGui.PopID();
                if (!picked) continue;
                entry["Value"] = choice.Value;
                hexOwner = string.Empty;
                if (selected is "Race" or "Clan" or "Gender" or "Face") ReconcileDependencies(selected);
            }
        }
        ImGui.EndChild();
    }

    private void SetApply(string name, bool value)
    {
        if (draft!["Customize"]?[name] is JObject entry) entry["Apply"] = value;
    }

    private void ReconcileDependencies(string changed)
    {
        void Set(string key, int value)
        {
            if (draft!["Customize"]?[key] is JObject field) field["Value"] = value;
        }
        if (changed == "Race") Set("Clan", (AppearanceCatalog.Value(draft!, "Race") - 1) * 2 + 1);
        if (changed == "Clan") Set("Race", (AppearanceCatalog.Value(draft!, "Clan") + 1) / 2);
        if (draft!["Customize"] is not JObject fields) return;
        // Face is reconciled first; Hrothgar hair choices depend on it.
        foreach (var property in fields.Properties().OrderBy(p => p.Name == "Face" ? 0 : 1))
        {
            if (property.Value is not JObject entry || entry["Value"]?.Type != JTokenType.Integer) continue;
            var options = catalog.Choices(draft, property.Name);
            if (options.Count > 0 && options.All(c => c.Value != entry.Value<int>("Value"))) entry["Value"] = options[0].Value;
        }
    }

    private static Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? GetIcon(uint icon)
    {
        if (icon == 0) return null;
        try { return DalamudServices.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrDefault(); }
        catch { return null; }
    }

    private static void DrawIcon(uint icon, float size)
    {
        if (GetIcon(icon) is { } texture) ImGui.Image(texture.Handle, new Vector2(Math.Min(TabletAppTheme.Px(size), ImGui.GetContentRegionAvail().X)));
    }

    private static string Label(string name) => name switch
    {
        "Hairstyle" => "Hair style", "Face" => "Face / head style", "MuscleMass" => "Muscle tone / tail / ear length",
        "TailShape" => "Tail / ear shape", "TattooColor" => "Facial feature color", "SmallIris" => "Small iris",
        "SkinDiffuse" => "Skin color", "HairDiffuse" => "Hair color", "LipDiffuse" => "Lip color and opacity",
        "DecalColor" => "Face paint color and opacity", "Percentage" => "Strength (%)",
        _ => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1"),
    };
}
