using System.Numerics;

namespace AirTablet.UI;

internal sealed record ThemePalette(
    string Name,
    Vector4 Accent,
    Vector4 AccentHover,
    Vector4 Surface,
    Vector4 SurfaceRaised,
    Vector4 Background)
{
    public static readonly IReadOnlyList<ThemePalette> All =
    [
        new("Purple", new(0.56f, 0.35f, 0.96f, 1f), new(0.67f, 0.49f, 1f, 1f), new(0.10f, 0.08f, 0.16f, 0.96f), new(0.15f, 0.12f, 0.23f, 0.98f), new(0.035f, 0.025f, 0.065f, 0.98f)),
        new("Orange", new(0.98f, 0.48f, 0.16f, 1f), new(1.00f, 0.62f, 0.30f, 1f), new(0.16f, 0.10f, 0.07f, 0.96f), new(0.24f, 0.14f, 0.08f, 0.98f), new(0.065f, 0.035f, 0.020f, 0.98f)),
        new("Blue", new(0.20f, 0.55f, 0.98f, 1f), new(0.38f, 0.68f, 1.00f, 1f), new(0.06f, 0.11f, 0.18f, 0.96f), new(0.08f, 0.17f, 0.27f, 0.98f), new(0.020f, 0.040f, 0.075f, 0.98f)),
        new("Teal", new(0.12f, 0.72f, 0.67f, 1f), new(0.28f, 0.84f, 0.78f, 1f), new(0.05f, 0.14f, 0.15f, 0.96f), new(0.07f, 0.21f, 0.21f, 0.98f), new(0.018f, 0.060f, 0.060f, 0.98f)),
        new("Rose", new(0.94f, 0.32f, 0.53f, 1f), new(1.00f, 0.47f, 0.65f, 1f), new(0.16f, 0.07f, 0.11f, 0.96f), new(0.24f, 0.10f, 0.16f, 0.98f), new(0.065f, 0.020f, 0.040f, 0.98f)),
        new("Green", new(0.24f, 0.76f, 0.39f, 1f), new(0.40f, 0.86f, 0.52f, 1f), new(0.07f, 0.14f, 0.09f, 0.96f), new(0.09f, 0.21f, 0.12f, 0.98f), new(0.020f, 0.060f, 0.030f, 0.98f)),
        new("Gold", new(0.92f, 0.72f, 0.20f, 1f), new(1.00f, 0.84f, 0.38f, 1f), new(0.16f, 0.13f, 0.06f, 0.96f), new(0.23f, 0.18f, 0.08f, 0.98f), new(0.055f, 0.043f, 0.018f, 0.98f)),
        new("Slate", new(0.52f, 0.62f, 0.76f, 1f), new(0.66f, 0.74f, 0.86f, 1f), new(0.10f, 0.12f, 0.16f, 0.96f), new(0.15f, 0.18f, 0.23f, 0.98f), new(0.030f, 0.040f, 0.055f, 0.98f)),
    ];

    public static ThemePalette Resolve(string name) =>
        All.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
