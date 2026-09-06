using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal static class AppearanceAttributeState
{
    public static bool Enabled(JObject entry, string key)
        => entry[key]?.Type == JTokenType.Boolean ? entry.Value<bool>(key) : (entry.Value<int?>(key) ?? 0) != 0;

    public static void Cycle(JObject entry, string valueKey = "Value", string applyKey = "Apply", int mask = 1, bool booleanValue = false)
    {
        var apply = entry.Value<bool?>(applyKey) ?? false;
        var enabled = Enabled(entry, valueKey);
        if (apply && !enabled) { entry[applyKey] = false; return; }
        entry[applyKey] = true;
        enabled = !apply || !enabled;
        entry[valueKey] = booleanValue || entry[valueKey]?.Type == JTokenType.Boolean || valueKey != "Value"
            ? new JValue(enabled) : new JValue(enabled ? mask : 0);
    }
}
