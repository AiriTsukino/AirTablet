using Lumina.Excel.Sheets;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private readonly Dictionary<string, List<(uint Id, string Name, uint Icon)>> equipmentCatalog = [];
    private IEnumerator<Item>? catalogScan;
    private bool equipmentCatalogReady;
    private int equipmentCatalogRevision;
    private static readonly string[] EquipmentSlots = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger", "MainHand", "OffHand"];

    public void WarmEquipmentCatalog()
    {
        if (equipmentCatalogReady) return;
        catalogScan ??= DalamudServices.DataManager.GetExcelSheet<Item>().GetEnumerator();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 512; i++)
        {
            if (!catalogScan.MoveNext()) { catalogScan.Dispose(); catalogScan = null; equipmentCatalogReady = true; break; }
            var item = catalogScan.Current;
            if (item.EquipSlotCategory.RowId != 0)
            {
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    foreach (var slot in EquipmentSlots)
                        if (FitsSlot(item, slot))
                        {
                            if (!equipmentCatalog.TryGetValue(slot, out var list)) equipmentCatalog[slot] = list = [];
                            list.Add((item.RowId, name, item.Icon));
                        }
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 2) break;
        }
        equipmentCatalogRevision++;
    }
}
