using System.Globalization;
using System.Numerics;

namespace WardrobeManager;

internal static class AppearanceColorHex
{
    public static string Format(Vector3 color, float alpha = 1f, bool includeAlpha = false)
    {
        static int Byte(float value) => (int)MathF.Round(Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f) * 255f);
        var rgb = $"#{Byte(color.X):X2}{Byte(color.Y):X2}{Byte(color.Z):X2}";
        return includeAlpha ? rgb + $"{Byte(alpha):X2}" : rgb;
    }

    public static bool TryParse(string? text, bool allowAlpha, out Vector3 color, out float? alpha)
    {
        color = default;
        alpha = null;
        var hex = text?.Trim() ?? string.Empty;
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6 && !(allowAlpha && hex.Length == 8)) return false;
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
        if (hex.Length == 8) { alpha = (value & 255) / 255f; value >>= 8; }
        color = new Vector3((value >> 16) & 255, (value >> 8) & 255, value & 255) / 255f;
        return true;
    }
}
