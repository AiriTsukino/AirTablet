using Dalamud.Configuration;

namespace ShopHelper;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool WindowVisible { get; set; }
    public bool SettingsWindowVisible { get; set; }
    public int DefaultQuantity { get; set; } = 99;
    public int DefaultStacks { get; set; } = 1;
    public int StackSize { get; set; } = 99;
    public bool AutoOpenWithShop { get; set; } = true;
    public bool AutoConfirmYesNo { get; set; } = true;
    public bool ConfirmBeforeBuying { get; set; } = true;

    public void Clamp()
    {
        DefaultQuantity = Math.Clamp(DefaultQuantity, 1, 9999);
        DefaultStacks = Math.Clamp(DefaultStacks, 1, 100);
        StackSize = Math.Clamp(StackSize, 1, 99);
    }
}
