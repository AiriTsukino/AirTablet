using System.Numerics;

namespace AutoGreet.Models;

[Serializable]
public enum CustomDetectionRegionShape
{
    Sphere = 0,
    Cube = 1,
}

[Serializable]
public sealed class CustomDetectionRegion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Outdoor region";
    public uint TerritoryType { get; set; }
    public Vector3 Center { get; set; }
    public CustomDetectionRegionShape Shape { get; set; } = CustomDetectionRegionShape.Sphere;
    public float Radius { get; set; } = 5f;
    public Vector3 HalfExtents { get; set; } = new(2.5f, 2.5f, 2.5f);
    public float YawDegrees { get; set; }
    public bool Enabled { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public string DisplayColorHex { get; set; } = "#FF0000";

    public bool Contains(Vector3 point)
    {
        if (!Enabled) return false;

        if (Shape != CustomDetectionRegionShape.Cube)
            return Vector3.DistanceSquared(point, Center) <= MathF.Pow(MathF.Max(0.5f, Radius), 2);

        var local = RotateAroundY(point - Center, -YawDegrees);
        return MathF.Abs(local.X) <= MathF.Max(0.5f, HalfExtents.X) &&
               MathF.Abs(local.Y) <= MathF.Max(0.5f, HalfExtents.Y) &&
               MathF.Abs(local.Z) <= MathF.Max(0.5f, HalfExtents.Z);
    }

    private static Vector3 RotateAroundY(Vector3 value, float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector3(
            value.X * cos - value.Z * sin,
            value.Y,
            value.X * sin + value.Z * cos);
    }
}
