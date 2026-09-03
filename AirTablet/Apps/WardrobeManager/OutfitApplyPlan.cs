namespace WardrobeManager;

internal static class OutfitApplyPlan
{
    public static ApplyResult Apply(WardrobePreset preset, IEnumerable<WardrobePreset> presets, Func<WardrobePreset, ApplyResult> apply)
    {
        if (preset.Type == WardrobePresetType.Outfit && preset.CharacterPresetId != Guid.Empty)
        {
            var character = presets.FirstOrDefault(item => item.Type == WardrobePresetType.Character && item.Id == preset.CharacterPresetId);
            if (character is null)
                return ApplyResult.Fail("The outfit's selected character preset is missing. Choose another character or None before applying it.");
            if (preset.GlamourerDesignId == Guid.Empty)
                return ApplyResult.Fail("Save this outfit to Glamourer before applying it.");
            if (character.GlamourerDesignId == Guid.Empty)
                return ApplyResult.Fail("Save the selected character preset to Glamourer before applying this outfit.");
            var characterResult = apply(character);
            if (!characterResult.Success) return ApplyResult.Fail($"Outfit not applied. {characterResult.Message}");
        }
        return apply(preset);
    }
}

internal sealed record ApplyResult(bool Success, string Message)
{
    public static ApplyResult Ok(string message) => new(true, message);
    public static ApplyResult Fail(string message) => new(false, message);
}
