using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ShopHelper.Models;
using ShopHelper.Services;
using ShopHelper.UI.Components;

namespace ShopHelper.UI;

internal sealed class MainWindow : Window
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly ShopService shopService;
    private readonly Action openSettings;
    private readonly Dictionary<string, int> quantities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> stackModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> automaticQuantities = new(StringComparer.OrdinalIgnoreCase);
    private PurchaseConfirmation? pendingConfirmation;
    private bool openConfirmationPopup;
    private string debugDump = "Click Refresh Debug Dump while the problematic shop tab is open, then copy or write it to /xllog.";

    public MainWindow(Configuration config, PersistenceService persistence, ShopService shopService, Action openSettings)
        : base("ShopHelper###ShopHelperMain")
    {
        Size = AirTablet.UI.TabletAppTheme.Px(new Vector2(820, 620));
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(720, 440)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.config = config;
        this.persistence = persistence;
        this.shopService = shopService;
        this.openSettings = openSettings;
    }

    public override void PreDraw() => ShopHelperTheme.Push();
    public override void PostDraw() => ShopHelperTheme.Pop();

    public override void Draw()
    {
        DrawHeader();

        if (ImGui.BeginTabBar("##ShopHelperTabs"))
        {
            if (ImGui.BeginTabItem("Shop"))
            {
                DrawStatus();
                ImGui.Spacing();
                DrawShopItems();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug Log"))
            {
                DrawDebugLogTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawPurchaseConfirmationPopup();
    }

    private void DrawHeader()
    {
        if (ImGui.BeginTable("##shop-helper-toolbar", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("context", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(
                "settings",
                ImGuiTableColumnFlags.WidthFixed,
                AirTablet.UI.TabletAppTheme.Px(104f));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            UiHelpers.TextMuted(
                shopService.CurrentShopAddonName is null
                    ? "Waiting for shop"
                    : shopService.CurrentShopAddonName);
            ImGui.TableNextColumn();
            if (ImGui.Button(
                    "Settings",
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                openSettings();
            }
            ImGui.EndTable();
        }
        ImGui.Separator();
    }

    private void DrawStatus()
    {
        if (UiHelpers.BeginCard(
                "##ShopHelperStatus",
                new Vector2(0, AirTablet.UI.TabletAppTheme.Px(108f)),
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(shopService.IsShopOpen ? ShopHelperTheme.Green : ShopHelperTheme.Muted, shopService.IsShopOpen ? "Shop detected" : "No shop detected");
            UiHelpers.TextWrappedMuted(shopService.Status);
            if (shopService.CurrentCurrencyAmount.HasValue)
                UiHelpers.TextMuted($"Available in shop: {shopService.CurrentCurrencyAmount.Value:N0}");
            if (shopService.IsRunning)
            {
                ImGui.SameLine();
                if (ImGui.Button("Cancel queue")) shopService.Cancel();
            }
        }
        UiHelpers.EndCard();
    }

    private void DrawShopItems()
    {
        var items = shopService.GetVisibleItems();
        if (items.Count == 0)
        {
            if (UiHelpers.BeginCard("##NoItems", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
            {
                UiHelpers.Header("Open a shop", "When a supported FFXIV shop window is open, visible item rows will appear here.");
                UiHelpers.TextWrappedMuted("Use the number box for an exact quantity, or tick Stacks to treat the number as stacks. The default stack size is adjustable in settings and defaults to 99.");
            }
            UiHelpers.EndCard();
            return;
        }

        var tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "##ShopItemsTable",
                6,
                tableFlags,
                AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 4f);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(80f));
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(86f));
        ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(106f));
        ImGui.TableSetupColumn("Purchase", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(128f));
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(82f));
        ImGui.TableHeadersRow();

        foreach (var item in items)
        {
            var key = ItemKey(item);
            if (!stackModes.ContainsKey(key)) stackModes[key] = false;

            if (!quantities.ContainsKey(key))
            {
                quantities[key] = GetSuggestedDefaultQuantity(item, item.CanSelectQuantity && stackModes[key]);
                automaticQuantities.Add(key);
            }

            if (!item.CanSelectQuantity)
            {
                stackModes[key] = false;
                quantities[key] = 1;
                automaticQuantities.Add(key);
            }

            var stackMode = item.CanSelectQuantity && stackModes[key];
            if (automaticQuantities.Contains(key))
                quantities[key] = GetSuggestedDefaultQuantity(item, stackMode);

            var quantity = quantities[key];
            var total = item.CanSelectQuantity ? (stackMode ? quantity * config.StackSize : quantity) : 1;
            var totalCost = item.Price.HasValue ? (long)item.Price.Value * total : (long?)null;
            var available = shopService.CurrentCurrencyAmount;
            var canAfford = !totalCost.HasValue || !available.HasValue || totalCost.Value <= available.Value;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(item.Name);
            UiHelpers.TextMuted(
                string.IsNullOrWhiteSpace(item.UnitName)
                    ? "Unit unavailable"
                    : item.UnitName);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(item.Price.HasValue ? item.Price.Value.ToString("N0") : "—");
            UiHelpers.TooltipOnHover("Price per item read from the shop data/UI. A dash means the price was not readable yet.");

            ImGui.TableSetColumnIndex(2);
            var modeLabel = $"Stacks##mode-{key}";
            ShopHelperTheme.PushHighContrastInput();
            using (new DisabledScope(!item.CanSelectQuantity))
            {
                if (ImGui.Checkbox(modeLabel, ref stackMode))
                {
                    stackModes[key] = stackMode;
                    quantities[key] = GetSuggestedDefaultQuantity(item, stackMode);
                    automaticQuantities.Add(key);
                }
            }
            ShopHelperTheme.PopHighContrastInput();
            UiHelpers.TooltipOnHover(item.CanSelectQuantity
                ? $"When enabled, Amount means stacks of {config.StackSize}. When disabled, Amount is a plain item quantity."
                : "This shop row has no FFXIV quantity selector. ShopHelper will buy it one item/click at a time.");

            ImGui.TableSetColumnIndex(3);
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1f));
            ShopHelperTheme.PushHighContrastInput();
            using (new DisabledScope(!item.CanSelectQuantity))
            {
                if (ImGui.InputInt($"##amount-{key}", ref quantity, 1, stackMode ? 10 : 99))
                {
                    quantity = item.CanSelectQuantity ? (stackMode ? Math.Clamp(quantity, 1, 100) : Math.Clamp(quantity, 1, 9999)) : 1;
                    quantities[key] = quantity;
                    automaticQuantities.Remove(key);
                }
            }
            ShopHelperTheme.PopHighContrastInput();

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted($"{total:N0} item{(total == 1 ? string.Empty : "s")}");
            UiHelpers.TextMuted(
                totalCost.HasValue
                    ? $"{totalCost.Value:N0} {item.UnitName}".Trim()
                    : "Cost unavailable");

            ImGui.TableSetColumnIndex(5);
            using var disabled = new DisabledScope(shopService.IsRunning || !canAfford);
            if (ImGui.Button($"Buy##buy-{key}", AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, 0))))
                RequestPurchase(item, total);
            if (!canAfford && totalCost.HasValue && available.HasValue)
                UiHelpers.TooltipOnHover($"Not enough {item.UnitName}. Cost: {totalCost.Value:N0}; available: {available.Value:N0}.");
        }

        ImGui.EndTable();
    }


    private void DrawDebugLogTab()
    {
        UiHelpers.TextWrappedMuted("Use this when a shop has wrong prices, wrong quantity detection, or missing rows. Open the exact shop/category first, click Refresh Debug Dump, then copy the text or write it to /xllog.");
        ImGui.Spacing();

        if (ImGui.Button("Refresh Debug Dump"))
            debugDump = shopService.BuildDebugDump();

        ImGui.SameLine();
        if (ImGui.Button("Copy Debug Dump"))
            ImGui.SetClipboardText(debugDump);

        ImGui.SameLine();
        if (ImGui.Button("Write to /xllog"))
            shopService.WriteDebugDumpToXlLog();

        ImGui.Spacing();
        if (ImGui.BeginChild("##ShopHelperDebugDump", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0)), true))
        {
            ImGui.TextWrapped(debugDump);
        }
        ImGui.EndChild();
    }

    private void RequestPurchase(ShopItemEntry item, int total)
    {
        if (!config.ConfirmBeforeBuying)
        {
            shopService.StartPurchase(item, total);
            return;
        }

        pendingConfirmation = new PurchaseConfirmation(item, total, item.Price.HasValue ? (long)item.Price.Value * total : null);
        openConfirmationPopup = true;
    }

    private void DrawPurchaseConfirmationPopup()
    {
        if (pendingConfirmation is null)
            return;

        if (openConfirmationPopup)
        {
            AirTablet.UI.TabletAppTheme.OpenCenteredModal("Confirm Purchase##ShopHelperConfirmPurchase");
            openConfirmationPopup = false;
        }

        ImGui.SetNextWindowSizeConstraints(
            AirTablet.UI.TabletAppTheme.Px(new Vector2(360, 0)),
            new Vector2(AirTablet.UI.TabletAppTheme.Px(520f), float.MaxValue));
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Confirm Purchase##ShopHelperConfirmPurchase",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
            return;

        var confirmation = pendingConfirmation;
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(460f));
        ImGui.TextWrapped($"You are about to buy {confirmation.Total:N0} × {confirmation.Item.Name}.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.TextUnformatted($"Total quantity: {confirmation.Total:N0}");
        ImGui.TextUnformatted(confirmation.Item.Price.HasValue ? $"Unit price: {confirmation.Item.Price.Value:N0}" : "Unit price: unavailable");
        ImGui.TextUnformatted(confirmation.TotalCost.HasValue ? $"Total cost: {confirmation.TotalCost.Value:N0}" : "Total cost: unavailable");

        ImGui.Spacing();
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(460f));
        UiHelpers.TextWrappedMuted("Make sure this quantity and cost look right before continuing.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        var buttonWidth = AirTablet.UI.TabletAppTheme.Px(120f);
        if (ImGui.Button("Buy", new Vector2(buttonWidth, 0)))
        {
            shopService.StartPurchase(confirmation.Item, confirmation.Total);
            pendingConfirmation = null;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
        {
            pendingConfirmation = null;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }

        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }


    private int GetSuggestedDefaultQuantity(ShopItemEntry item, bool stackMode)
    {
        if (!item.CanSelectQuantity)
            return 1;

        var configuredDefault = stackMode ? config.DefaultStacks : Math.Min(config.DefaultQuantity, 99);
        var maxAllowed = stackMode ? 100 : 9999;
        var suggested = Math.Clamp(configuredDefault, 1, maxAllowed);

        if (item.Price is > 0 && shopService.CurrentCurrencyAmount is int availableAmount)
        {
            var costPerEnteredAmount = (long)item.Price.Value * (stackMode ? config.StackSize : 1);
            if (costPerEnteredAmount > 0)
            {
                var affordable = (int)Math.Clamp(availableAmount / costPerEnteredAmount, 0, maxAllowed);
                suggested = Math.Min(suggested, Math.Max(1, affordable));
            }
        }

        return Math.Clamp(suggested, 1, maxAllowed);
    }

    private static string ItemKey(ShopItemEntry item) => $"{item.AddonName}:{item.RowIndex}:{item.Name}";

    private sealed record PurchaseConfirmation(ShopItemEntry Item, int Total, long? TotalCost);

    private readonly struct DisabledScope : IDisposable
    {
        private readonly bool disabled;
        public DisabledScope(bool disabled)
        {
            this.disabled = disabled;
            if (disabled) ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (disabled) ImGui.EndDisabled();
        }
    }
}
