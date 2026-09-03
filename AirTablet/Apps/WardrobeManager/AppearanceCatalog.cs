using System.Numerics;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

// Read the same character-creation sheets and human.cmp palettes as Glamourer.
// All sheet indices are data-format indices, never UI row/selection indices.
internal sealed class AppearanceCatalog
{
    internal sealed record Choice(int Value, string Label, uint Icon = 0, Vector4? Color = null);
    private readonly Dictionary<(string, int, int, int), IReadOnlyList<Choice>> cache = [];
    private byte[]? palette;
    public string Error { get; private set; } = string.Empty;
    private static readonly Dictionary<string, int> Bytes = new(StringComparer.Ordinal)
    {
        ["Height"] = 3, ["Face"] = 5, ["Hairstyle"] = 6, ["SkinColor"] = 8,
        ["EyeColorRight"] = 9, ["HairColor"] = 10, ["HighlightsColor"] = 11,
        ["TattooColor"] = 13, ["Eyebrows"] = 14, ["EyeColorLeft"] = 15,
        ["EyeShape"] = 16, ["Nose"] = 17, ["Jaw"] = 18, ["Mouth"] = 19,
        ["LipColor"] = 20, ["MuscleMass"] = 21, ["TailShape"] = 22,
        ["BustSize"] = 23, ["FacePaint"] = 24, ["FacePaintColor"] = 25,
    };
    public static int Value(JObject design, string name) => design["Customize"]?[name]?.Value<int>("Value") ?? 0;
    public static int ToggleMask(string name) => name switch
    {
        "Highlights" or "SmallIris" or "Lipstick" or "FacePaintReversed" or "LegacyTattoo" => 128,
        "FacialFeature1" => 1, "FacialFeature2" => 2, "FacialFeature3" => 4,
        "FacialFeature4" => 8, "FacialFeature5" => 16, "FacialFeature6" => 32, "FacialFeature7" => 64,
        _ => 0,
    };
    public static bool IsPalette(string name, int race) => name is "SkinColor" or "HairColor" or "HighlightsColor"
        or "EyeColorLeft" or "EyeColorRight" or "TattooColor" or "FacePaintColor" || name == "LipColor" && race != 7;

    public IReadOnlyList<Choice> Choices(JObject design, string name)
    {
        var clan = Value(design, "Clan");
        var gender = Value(design, "Gender");
        var face = Value(design, "Face");
        var key = (name, clan, gender, face);
        if (cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var result = ReadChoices(design, name, clan, gender, face);
            cache[key] = result;
            return result;
        }
        catch (Exception ex)
        {
            Error = "Some game appearance options are unavailable: " + ex.Message;
            return [];
        }
    }

    private IReadOnlyList<Choice> ReadChoices(JObject design, string name, int clan, int gender, int face)
    {
        var data = DalamudServices.DataManager;
        var race = Value(design, "Race");
        if (name == "Race") return data.GetExcelSheet<Race>().Where(r => r.RowId is >= 1 and <= 8)
            .Select(r => new Choice((int)r.RowId, r.Masculine.ExtractText())).ToArray();
        if (name == "Clan") return data.GetExcelSheet<Tribe>().Where(r => r.RowId >= race * 2 - 1 && r.RowId <= race * 2)
            .Select(r => new Choice((int)r.RowId, (gender == 1 ? r.Feminine : r.Masculine).ExtractText())).ToArray();
        if (name == "Gender") return [new(0, "Masculine"), new(1, "Feminine")];
        if (clan is < 1 or > 16 || gender is < 0 or > 1) return [];
        var rowId = (uint)((clan - 1) * 2 + gender);
        var row = data.GetExcelSheet<CharaMakeType>().GetRow(rowId);
        if (IsPalette(name, race)) return Palette(name, clan, gender);
        if (ToggleMask(name) != 0)
        {
            uint icon = name == "LegacyTattoo" ? 137905u : 0u;
            var faceIndex = (race == 7 && face > 4 ? face - 4 : face) - 1;
            if (name.StartsWith("FacialFeature", StringComparison.Ordinal) && faceIndex >= 0 && faceIndex < row.FacialFeatureOption.Count)
            {
                var options = row.FacialFeatureOption[faceIndex];
                icon = name[^1] switch { '1' => (uint)options.Option1, '2' => (uint)options.Option2,
                    '3' => (uint)options.Option3, '4' => (uint)options.Option4, '5' => (uint)options.Option5,
                    '6' => (uint)options.Option6, '7' => (uint)options.Option7, _ => 0u };
            }
            return [new(0, "Off", icon), new(ToggleMask(name), "On", icon)];
        }
        var customize = data.GetExcelSheet<CharaMakeCustomize>();
        if (name is "Hairstyle" or "FacePaint")
        {
            var hair = data.GetExcelSheet<RawRow>(name: "HairMakeType").GetRow(rowId);
            var isHair = name == "Hairstyle";
            var count = hair.ReadUInt8Column(isHair ? 30 : 37);
            var result = new List<Choice>();
            for (var i = 0; i < count; i++)
            {
                var id = hair.ReadUInt32Column((isHair ? 66 : 73) + i * 9);
                if (id == uint.MaxValue) continue;
                if (customize.TryGetRow(id, out var entry))
                {
                    if (isHair && race == 7 && entry.Unknown0 != 0 && entry.Unknown0 != (face > 4 ? face : face + 4)) continue;
                    result.Add(new(entry.FeatureID, $"{(isHair ? "Hair" : "Paint")} {entry.FeatureID}", entry.Icon));
                }
                else result.Add(new(i, $"{(isHair ? "Hair" : "Paint")} {i}", id));
            }
            return result.DistinctBy(c => c.Value).OrderBy(c => c.Value).ToArray();
        }
        if (!Bytes.TryGetValue(name, out var byteIndex)) return [];
        foreach (var menu in row.CharaMakeStruct)
        {
            if (menu.Customize != byteIndex) continue;
            if (name is "Height" or "MuscleMass" or "BustSize")
                return Enumerable.Range(0, 101).Select(i => new Choice(i, $"{i}%")).ToArray();
            var result = new List<Choice>();
            for (var i = 0; i < menu.SubMenuNum; i++)
            {
                if (name is "Face" or "TailShape" or "LipColor")
                {
                    var id = menu.SubMenuParam[i];
                    var found = customize.TryGetRow(id, out var entry);
                    var value = found ? entry.FeatureID : i + 1;
                    // Hrothgar stores heads 1..4 as customize bytes 5..8.
                    var storedValue = name == "Face" && race == 7 ? value + 4 : value;
                    result.Add(new(storedValue, $"Option {value}", found ? entry.Icon : id));
                }
                else result.Add(new(i, $"Option {i + 1}"));
            }
            return result;
        }
        return [];
    }

    private IReadOnlyList<Choice> Palette(string name, int clan, int gender)
    {
        palette ??= DalamudServices.DataManager.GetFile("chara/xls/charamake/human.cmp")?.Data
            ?? throw new InvalidOperationException("human.cmp could not be loaded.");
        // CmpData: two 0x2400 global color tables, then 32 0x1400 clan/gender tables.
        var offset = name switch
        {
            "EyeColorLeft" or "EyeColorRight" => 0x2400,
            "HighlightsColor" => 0x2800,
            "TattooColor" => 0x3000,
            "SkinColor" => 0x4800 + ((clan - 1) * 2 + gender) * 0x1400 + 0xC00,
            "HairColor" => 0x4800 + ((clan - 1) * 2 + gender) * 0x1400 + 0x1000,
            "LipColor" => 0x800,
            "FacePaintColor" => 0xA00,
            _ => throw new InvalidOperationException("Unknown palette."),
        };
        var toned = name is "LipColor" or "FacePaintColor";
        var result = new List<Choice>(192);
        for (var i = 0; i < 192; i++)
        {
            var value = toned && i >= 96 ? i + 32 : i;
            var index = toned && i >= 96 ? offset + 0x800 + (i - 96) * 4 : offset + i * 4;
            if (index + 3 >= palette.Length) throw new InvalidOperationException("Unsupported human.cmp layout.");
            var color = new Vector4(palette[index] / 255f, palette[index + 1] / 255f, palette[index + 2] / 255f, 1f);
            result.Add(new(value, $"Color {value}", Color: color));
        }
        return result;
    }
}
