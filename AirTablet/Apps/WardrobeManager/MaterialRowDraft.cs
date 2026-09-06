using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal static class MaterialRowDraft
{
    public static Half[] HighlightRow(Half[] captured, int row)
    {
        if (captured.Length != 1024 || row is < 0 or >= 32) throw new ArgumentException("Invalid colour-table preview target.");
        var highlighted = (Half[])captured.Clone();
        highlighted[row * 32 + 8] = (Half)4f;
        highlighted[row * 32 + 9] = (Half)0.1f;
        highlighted[row * 32 + 10] = (Half)4f;
        return highlighted;
    }

    public static JObject FromLiveColors(string mode, ReadOnlySpan<Half> row)
    {
        if (row.Length != 32) throw new ArgumentException("Expected one 32-component colour-table row.", nameof(row));
        var draft = Create(mode);
        string[] channels = ["Diffuse", "Specular", "Emissive"];
        string[] components = ["R", "G", "B"];
        for (var channel = 0; channel < 3; channel++)
            for (var component = 0; component < 3; component++)
            {
                var value = (float)row[channel * 4 + component];
                if (!float.IsFinite(value)) throw new ArgumentException("The live colour table contains invalid values.", nameof(row));
                // The game stores squared colour values; the design stores the
                // editable colour-space values. Optional scalar overrides stay unset.
                draft[channels[channel] + components[component]] = MathF.Sqrt(MathF.Abs(value));
            }
        return draft;
    }

    public static JObject Create(string mode)
    {
        if (mode is not ("Legacy" or "Dawntrail")) throw new ArgumentOutOfRangeException(nameof(mode));
        return new JObject
        {
            ["Enabled"] = false, ["Mode"] = mode,
            ["DiffuseR"] = 1f, ["DiffuseG"] = 1f, ["DiffuseB"] = 1f,
            ["SpecularR"] = 1f, ["SpecularG"] = 1f, ["SpecularB"] = 1f,
            ["EmissiveR"] = 0f, ["EmissiveG"] = 0f, ["EmissiveB"] = 0f,
        };
    }
}
