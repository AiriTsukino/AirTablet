namespace AutoGreet.Models;

[Serializable]
public readonly record struct VisitorKey(string Name, string World)
{
    public override string ToString() => $"{Name}@{World}";
    public string Display => $"{Name} ({World})";

    public static bool TryParse(string value, out VisitorKey key)
    {
        key = default;
        var idx = value.LastIndexOf('@');
        if (idx <= 0 || idx >= value.Length - 1) return false;
        key = new VisitorKey(value[..idx], value[(idx + 1)..]);
        return true;
    }
}
