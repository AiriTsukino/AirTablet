namespace ShopHelper.Models;

public sealed class ShopItemEntry
{
    public int RowIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public string UnitName { get; init; } = string.Empty;
    public int? Price { get; init; }
    public bool CanSelectQuantity { get; init; } = true;
    public string AddonName { get; init; } = string.Empty;
}
