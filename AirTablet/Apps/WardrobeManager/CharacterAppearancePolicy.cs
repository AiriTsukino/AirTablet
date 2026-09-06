using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal static class CharacterAppearancePolicy
{
    internal static void Prepare(JObject design, bool initializeApply = false)
    {
        // Saved/imported characters may include clothing and advanced dyes.
        // Only a new physical capture initializes the previous default flags.
        if (!initializeApply) return;
        foreach (var section in new[] { "Equipment", "Bonus" })
            if (design[section] is JObject entries)
                foreach (var property in entries.DescendantsAndSelf().OfType<JProperty>()
                             .Where(p => p.Name.StartsWith("Apply", StringComparison.OrdinalIgnoreCase)))
                    property.Value = false;
        if (design["Customize"] is JObject customize)
            foreach (var property in customize.Properties())
                if (property.Value is JObject entry)
                    entry["Apply"] = !property.Name.Equals("Wetness", StringComparison.OrdinalIgnoreCase);
        if (design["Parameters"] is JObject parameters)
            foreach (var entry in parameters.Properties().Select(p => p.Value).OfType<JObject>())
                entry["Apply"] = true;
        design.Remove("Materials");
    }
}
