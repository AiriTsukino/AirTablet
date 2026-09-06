using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal static class MaterialRowClipboard
{
    private static readonly string[] Colors = ["DiffuseR", "DiffuseG", "DiffuseB", "SpecularR", "SpecularG", "SpecularB", "EmissiveR", "EmissiveG", "EmissiveB"];
    private static readonly string[] Scalars = ["Roughness", "Metalness", "Sheen", "SheenTint", "SheenAperture", "Gloss", "SpecularA"];
    public static string Copy(JObject row) => row.ToString(Formatting.None);

    public static bool TryPaste(string? text, string mode, out JObject row)
    {
        row = new JObject();
        if (text is null || text.Length > 16384 || mode is not ("Dawntrail" or "Legacy")) return false;
        try
        {
            var source = JObject.Parse(text);
            if (source.Value<string>("Mode") != mode) return false;
            row["Mode"] = mode;
            foreach (var key in Colors.Concat(Scalars))
            {
                var value = source[key];
                if (value is null) { if (Colors.Contains(key)) return false; continue; }
                if (value.Type is not (JTokenType.Integer or JTokenType.Float)) return false;
                var number = value.Value<float>();
                if (!float.IsFinite(number) || Math.Abs(number) > 65504) return false;
                if (key == "SheenAperture" && number < .001f) return false;
                row[key] = number;
            }
            row["Enabled"] = true; row["Revert"] = false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or ArgumentException) { return false; }
    }
}
