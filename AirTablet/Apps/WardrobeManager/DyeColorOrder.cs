namespace WardrobeManager;

internal static class DyeColorOrder
{
    public static (int Family, float Darkness) Key(uint rgb)
    {
        var r = ((rgb >> 16) & 255) / 255f;
        var g = ((rgb >> 8) & 255) / 255f;
        var b = (rgb & 255) / 255f;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2;
        if (delta < .08f) return (0, -lightness);
        var hue = max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
        hue = (hue * 60 + 360) % 360;
        return (1 + (int)((hue + 15) % 360 / 30), -lightness);
    }
}
