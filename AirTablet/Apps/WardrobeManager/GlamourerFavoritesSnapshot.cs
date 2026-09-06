using Newtonsoft.Json.Linq;

namespace WardrobeManager;

// Read-only format adapter. No Glamourer objects or implementation are used.
internal sealed record GlamourerFavoritesSnapshot(HashSet<uint> Items, HashSet<uint> Facewear)
{
    internal static GlamourerFavoritesSnapshot Parse(string json)
    {
        var root = JToken.Parse(json);
        if (root is JArray legacy) return new(ReadIds(legacy, uint.MaxValue), []);
        if (root is not JObject document || document.Value<int?>("Version") != 1)
            return new([], []);
        return new(ReadIds(document["FavoriteItems"], uint.MaxValue),
            ReadIds(document["FavoriteBonusItems"], ushort.MaxValue));
    }

    private static HashSet<uint> ReadIds(JToken? token, uint maximum)
    {
        var result = new HashSet<uint>();
        if (token is not JArray array) return result;
        foreach (var value in array)
            if (value.Type == JTokenType.Integer && uint.TryParse(value.ToString(), out var id) && id > 0 && id <= maximum)
                result.Add(id);
        return result;
    }

    internal static GlamourerFavoritesSnapshot Read(string path)
    {
        try
        {
            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            if (stream.Length > 4 * 1024 * 1024) return new([], []);
            using var reader = new System.IO.StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (System.IO.IOException) { return new([], []); }
        catch (UnauthorizedAccessException) { return new([], []); }
        catch (Newtonsoft.Json.JsonException) { return new([], []); }
        catch (OverflowException) { return new([], []); }
        catch (FormatException) { return new([], []); }
    }
}
