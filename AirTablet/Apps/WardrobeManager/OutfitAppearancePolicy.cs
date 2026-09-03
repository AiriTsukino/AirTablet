using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal static class OutfitAppearancePolicy
{
    public static bool HasCapture(WardrobePreset preset) => !string.IsNullOrWhiteSpace(preset.OutfitAppearanceJson)
        || !string.IsNullOrWhiteSpace(preset.GlamourerState);

    public static bool MatchesSavedAppearance(JObject expected, JObject stored)
        => new[] { "Customize", "Parameters" }.All(section => JToken.DeepEquals(Canonical(expected[section]), Canonical(stored[section])));

    private static JToken? Canonical(JToken? token)
    {
        var copy = token?.DeepClone();
        if (copy is not JObject entries) return copy;
        // Newer Glamourer exports omit false Apply flags. Omission and false
        // are semantically identical, not a failed round-trip.
        foreach (var entry in entries.Properties().Select(p => p.Value).OfType<JObject>())
            if (entry.Value<bool?>("Apply") == false) entry.Remove("Apply");
        return copy;
    }

    // Keep the latest Glamourer customization values AND application flags.
    // Only fields explicitly changed in this editor override the latest design.
    public static void PreserveAndApply(JObject target, JObject? currentDesign, IReadOnlyDictionary<string, bool> overrides,
        IReadOnlyDictionary<string, string>? values = null)
    {
        foreach (var section in new[] { "Customize", "Parameters" })
        {
            if (currentDesign?[section] is { } current) target[section] = current.DeepClone();
            if (target[section] is not JObject entries) continue;
            foreach (var entry in entries.Properties())
            {
                if (entry.Value is not JObject value) continue;
                if (overrides.TryGetValue($"{section}/{entry.Name}", out var apply)) value["Apply"] = apply;
            }
        }
        if (values is null) return;
        foreach (var (path, json) in values)
        {
            var parts = path.Split('/');
            if (parts.Length != 3 || parts[0] is not ("Customize" or "Parameters")) continue;
            if (target[parts[0]]?[parts[1]] is not JObject entry) continue;
            var value = JToken.Parse(json);
            if (value is JValue) entry[parts[2]] = value;
        }
    }

    public static void RecordEdits(JObject original, JObject edited, IDictionary<string, string> overrides)
    {
        foreach (var section in new[] { "Customize", "Parameters" })
        {
            if (edited[section] is not JObject entries) continue;
            foreach (var entry in entries.Properties())
            {
                if (entry.Value is not JObject fields) continue;
                foreach (var field in fields.Properties())
                    if (field.Value is JValue && !JToken.DeepEquals(original[section]?[entry.Name]?[field.Name], field.Value))
                        overrides[$"{section}/{entry.Name}/{field.Name}"] = field.Value.ToString(Newtonsoft.Json.Formatting.None);
            }
        }
    }

    public static IEnumerable<(string Key, string Label, bool Apply)> Options(string json, IReadOnlyDictionary<string, bool> overrides)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JObject? design;
        try { design = JObject.Parse(json); }
        catch { yield break; }
        foreach (var section in new[] { "Customize", "Parameters" })
        {
            if (design[section] is not JObject entries) continue;
            foreach (var entry in entries.Properties())
            {
                if (entry.Value is not JObject value || value["Apply"]?.Type != JTokenType.Boolean) continue;
                var key = $"{section}/{entry.Name}";
                yield return (key, $"{entry.Name} ({section})", overrides.TryGetValue(key, out var apply) ? apply : value.Value<bool>("Apply"));
            }
        }
    }
}
