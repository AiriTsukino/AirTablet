using System.Globalization;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal static class FacewearId
{
    // Glamourer's public CustomItemId representation tags standard bonus items
    // with bit 49. The actual Glasses sheet ID is an unsigned 16-bit value.
    private const ulong BonusTag = 1UL << 49;

    public static bool TryRead(JObject entry, out uint rowId)
    {
        rowId = 0;
        if (!ulong.TryParse(entry["BonusId"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var saved)) return false;
        if (saved <= ushort.MaxValue) { rowId = (uint)saved; return true; } // Legacy export.
        if ((saved & ~((ulong)ushort.MaxValue)) != BonusTag) return false; // Custom/unknown model: preserve untouched.
        rowId = (uint)(saved & ushort.MaxValue);
        return true;
    }

    public static ulong Encode(uint rowId)
    {
        if (rowId > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(rowId));
        return BonusTag | rowId;
    }
}
