using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class IntegrationService
{
    private bool SaveLinkedDesign(WardrobePreset preset, WardrobePresetType type, string folder, out string message)
    {
        message = string.Empty;
        if (preset.Type != type) { message = "The preset type does not match the requested save."; return false; }
        try
        {
            var id = preset.GlamourerDesignId;
            var previous = id == Guid.Empty ? null : ParseDesignObject(getDesignJObject.InvokeFunc(id));
            if (id != Guid.Empty && previous is null)
            { message = "The linked Glamourer design is missing or unavailable. It was not recreated."; return false; }
            var capture = type == WardrobePresetType.Character ? preset.CharacterAppearanceJson : preset.OutfitAppearanceJson;
            var bridge = GlamourerLiveDesign.Connect();
            var design = !string.IsNullOrWhiteSpace(capture) ? JObject.Parse(capture)
                : !string.IsNullOrWhiteSpace(preset.GlamourerState) ? bridge.Decode(preset.GlamourerState)
                : previous is not null ? (JObject)previous.DeepClone() : null;
            if (design is null) { message = "Capture an appearance before saving to Glamourer."; return false; }
            if (previous is not null)
                foreach (var property in new[] { "Description", "ForcedRedraw", "ResetTemporarySettings", "Color", "QuickDesign", "Tags", "Links", "ResetAdvancedDyes", "RevertAdvancedDyes", "FileSystemFolder", "SortOrderName" })
                    if (previous[property] is { } value) design[property] = value.DeepClone();
            else if (type == WardrobePresetType.Character) design["QuickDesign"] = false;
            design["Name"] = preset.Name.Trim();
            design["FileSystemFolder"] = folder.Trim();
            design["Mods"] = new JArray(preset.Mods.Select(SerializeModAssociation));
            OutfitAppearancePolicy.PreserveAndApply(design, previous, preset.OutfitAppearanceOverrides, preset.AppearanceValueOverrides);
            JObject? stored = null;
            bool Verify()
            {
                stored = ParseDesignObject(getDesignJObject.InvokeFunc(id));
                return stored is not null
                    && OutfitAppearancePolicy.MatchesSavedAppearance(design, stored)
                    && OutfitAppearancePolicy.MatchesEditorFields(design, stored, preset.AppearanceValueOverrides)
                    && JToken.DeepEquals(NormalizedMods(design), NormalizedMods(stored))
                    && getDesignList.Invoke().TryGetValue(id, out var name) && name == preset.Name.Trim()
                    && TryVerifyDesignFolder(id, folder, out _);
            }
            if (id != Guid.Empty)
                bridge.Update(id, design, folder, Verify);
            else
            {
                var result = addDesign.Invoke(design.ToString(Newtonsoft.Json.Formatting.None), DesignImportName(folder, preset.Name), out id);
                if (result != GlamourerApiEc.Success || id == Guid.Empty)
                { message = $"Glamourer could not create the design ({result})."; return false; }
                if (!Verify())
                {
                    // Cleanup is limited to the design created by this call.
                    deleteDesign.Invoke(id);
                    message = "Glamourer did not retain the new design's requested settings. The new design was removed; your draft remains.";
                    return false;
                }
            }
            preset.GlamourerDesignId = id;
            preset.GlamourerState = getDesignBase64.Invoke(id) ?? preset.GlamourerState;
            if (type == WardrobePresetType.Character) preset.CharacterAppearanceJson = stored!.ToString(Newtonsoft.Json.Formatting.None);
            else preset.OutfitAppearanceJson = stored!.ToString(Newtonsoft.Json.Formatting.None);
            preset.GlamourerFolderPath = folder.Trim();
            preset.AppearanceValueOverrides.Clear(); preset.OutfitAppearanceOverrides.Clear();
            if (type == WardrobePresetType.Outfit) SelectQuickDesign(preset);
            message = $"Saved {preset.Name} to Glamourer.";
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "WardrobeManager could not save a Glamourer design in place.");
            message = "Glamourer save failed: " + (ex is AggregateException ? ex.Message : ex.GetBaseException().Message) + " Your draft has been kept. No existing design was deleted or recreated.";
            return false;
        }
    }

    private static JArray NormalizedMods(JObject design)
        => new(ParseAssociatedMods(design).Select(ToWardrobeRule).OrderBy(rule => rule.Directory, StringComparer.Ordinal)
            .ThenBy(rule => rule.Name, StringComparer.Ordinal).Select(SerializeModAssociation));
}
