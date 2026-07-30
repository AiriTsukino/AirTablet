using System.Text.RegularExpressions;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using ShopHelper.Models;

namespace ShopHelper.Services;

internal sealed unsafe class ShopService : IDisposable
{
    // Standard shop addon names seen across gil, scrip, exchange, and special vendor windows.
    // The driver is intentionally data-driven so additional addon names can be added in one place.
    private static readonly string[] ShopAddonNames =
    [
        "Shop",
        "ShopExchangeItem",
        "ShopExchangeCurrency",
        "InclusionShop",
        "FreeShop",
        "GrandCompanyExchange",
    ];

    // These callback shapes are the only part that may need adjustment after a game/UI change.
    // They are kept isolated here so the UI/theme/settings remain unaffected.
    private const int ConfirmYesCallback = 0;
    private const int QuantityOkCallback = 0;
    private const int ShopSelectCallback = 0;
    private const int MaxSinglePurchase = 99;
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CallbackResponseTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan PostConfirmDelay = TimeSpan.FromMilliseconds(650);
    private static readonly Dictionary<string, int> PreferredCallbackAttempts = new(StringComparer.Ordinal);

    private readonly Configuration config;
    private DateTime nextActionUtc = DateTime.MinValue;
    private PendingPurchase? pending;
    private string status = "Open a shop to begin.";
    private IReadOnlyList<ShopItemEntry> cachedItems = Array.Empty<ShopItemEntry>();
    private string? cachedAddonName;
    private int? currentCurrencyAmount;
    private DateTime nextScanUtc = DateTime.MinValue;
    private static HashSet<string>? knownItemNames;
    private static Dictionary<string, int>? knownItemPrices;
    private static Dictionary<string, uint>? knownItemIds;
    private static Dictionary<string, bool>? knownItemStackable;

    public ShopService(Configuration config)
    {
        this.config = config;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    public string Status => pending is null ? status : $"Buying {pending.ItemName}: {pending.Completed:N0}/{pending.Total:N0} queued";
    public bool IsRunning => pending is not null;
    public bool IsShopOpen => TryGetOpenShopAddon(out _, out _);
    public string? CurrentShopAddonName { get; private set; }
    public int? CurrentCurrencyAmount => currentCurrencyAmount;

    public IReadOnlyList<ShopItemEntry> GetVisibleItems()
    {
        if (DateTime.UtcNow < nextScanUtc)
            return cachedItems;

        nextScanUtc = DateTime.UtcNow.AddMilliseconds(500);
        cachedItems = ScanVisibleItems();
        return cachedItems;
    }

    public void StartPurchase(ShopItemEntry item, int totalQuantity)
    {
        if (totalQuantity <= 0)
            return;

        var clampedTotal = Math.Clamp(totalQuantity, 1, 999999);
        if (item.Price.HasValue && currentCurrencyAmount.HasValue)
        {
            var totalCost = (long)item.Price.Value * clampedTotal;
            if (totalCost > currentCurrencyAmount.Value)
            {
                status = $"Not enough {item.UnitName}: {item.Name} costs {totalCost:N0}, but you only have {currentCurrencyAmount.Value:N0}.";
                DalamudServices.Log.Verbose("ShopHelper blocked unaffordable purchase of {ItemName}. Cost={Cost}, Available={Available}.", item.Name, totalCost, currentCurrencyAmount.Value);
                return;
            }
        }

        pending = new PendingPurchase(item.AddonName, item.RowIndex, item.Name, clampedTotal, item.CanSelectQuantity);
        nextActionUtc = DateTime.MinValue;
        status = $"Queued {pending.Total:N0} × {item.Name}.";
    }

    public void Cancel()
    {
        if (pending is not null)
            status = $"Cancelled {pending.ItemName} at {pending.Completed:N0}/{pending.Total:N0}.";
        pending = null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (config.AutoOpenWithShop && IsShopOpen)
                CurrentShopAddonName = cachedAddonName;

            if (pending is null || DateTime.UtcNow < nextActionUtc)
                return;

            nextActionUtc = DateTime.UtcNow.Add(ActionDelay);
            TickPurchase();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShopHelper purchase tick failed.");
            status = "Purchase tick failed. See /xllog for details.";
            pending = null;
        }
    }

    private void TickPurchase()
    {
        if (pending is null)
            return;

        if (!TryGetOpenShopAddon(out var shopAddon, out var shopAddonName) || !string.Equals(shopAddonName, pending.AddonName, StringComparison.Ordinal))
        {
            status = "Shop closed before the queued purchase finished.";
            pending = null;
            return;
        }

        if (config.AutoConfirmYesNo && TryClickYesNo())
        {
            var completedThisPurchase = Math.Max(1, pending.LastRequestedQuantity);
            pending.Completed += Math.Min(completedThisPurchase, pending.Remaining);
            pending.LastRequestedQuantity = 0;
            pending.WaitingForQuantityDialog = false;
            pending.WaitingForYesNo = false;
            pending.WaitStartedUtc = null;
            pending.CallbackAttempt = 0;

            if (pending.LastCallbackAttemptUsed.HasValue)
                PreferredCallbackAttempts[pending.AddonName] = pending.LastCallbackAttemptUsed.Value % 7;

            if (pending.Remaining <= 0)
            {
                status = $"Finished queue for {pending.ItemName}.";
                pending = null;
                return;
            }

            // Give the game/server a short moment to finish the accepted transaction before
            // firing the next row callback. Without this, long one-at-a-time queues can race
            // the shop UI and eventually time out or fail.
            nextActionUtc = DateTime.UtcNow.Add(PostConfirmDelay);
            return;
        }

        var now = DateTime.UtcNow;
        var chunk = pending.UsesQuantityDialog ? Math.Min(MaxSinglePurchase, pending.Remaining) : 1;

        if (pending.UsesQuantityDialog && TryConfirmQuantity(chunk))
        {
            // Do not count this as completed until the normal game confirmation is accepted.
            pending.LastRequestedQuantity = chunk;
            pending.WaitingForQuantityDialog = false;
            pending.WaitingForYesNo = true;
            pending.WaitStartedUtc = now;
            nextActionUtc = now.Add(ActionDelay);
            return;
        }

        if (pending.WaitingForQuantityDialog || pending.WaitingForYesNo)
        {
            var waitStarted = pending.WaitStartedUtc ?? now;
            if (now - waitStarted < CallbackResponseTimeout)
            {
                nextActionUtc = now.Add(ActionDelay);
                return;
            }

            // The previous callback shape did not produce the expected game window. Try the
            // next known callback shape instead of incrementing attempts every frame while
            // the UI is still responding.
            pending.WaitingForQuantityDialog = false;
            pending.WaitingForYesNo = false;
            pending.WaitStartedUtc = null;
        }

        pending.LastRequestedQuantity = chunk;
        var callbackAttempt = GetCallbackAttemptToTry(pending.AddonName, pending.CallbackAttempt++);
        pending.LastCallbackAttemptUsed = callbackAttempt;
        SelectShopRow(pending.AddonName, shopAddon, pending.RowIndex, chunk, callbackAttempt);
        pending.WaitStartedUtc = now;
        pending.WaitingForQuantityDialog = pending.UsesQuantityDialog;
        pending.WaitingForYesNo = !pending.UsesQuantityDialog;

        if (pending.CallbackAttempt > 24)
        {
            status = $"Could not trigger the shop purchase for {pending.ItemName}. See Debug Log and /xllog for callback details.";
            DalamudServices.Log.Warning("ShopHelper could not trigger purchase after {AttemptCount} callback attempts. Addon={AddonName}, Row={RowIndex}, Quantity={Quantity}, Item={ItemName}", pending.CallbackAttempt, pending.AddonName, pending.RowIndex, chunk, pending.ItemName);
            pending = null;
        }
    }

    private IReadOnlyList<ShopItemEntry> ScanVisibleItems()
    {
        if (!TryGetOpenShopAddon(out var addon, out var addonName))
        {
            cachedAddonName = null;
            CurrentShopAddonName = null;
            currentCurrencyAmount = null;
            status = pending is null ? "Open a shop to begin." : status;
            return Array.Empty<ShopItemEntry>();
        }

        cachedAddonName = addonName;
        CurrentShopAddonName = addonName;

        var textFragments = ExtractVisibleText(addon);
        currentCurrencyAmount = TryReadCurrentCurrencyAmount(textFragments);
        if (currentCurrencyAmount is null)
        {
            status = pending is null
                ? $"{addonName} is open, but no top-right shop currency amount was readable yet."
                : status;
            return Array.Empty<ShopItemEntry>();
        }

        if (string.Equals(addonName, "InclusionShop", StringComparison.Ordinal))
        {
            // InclusionShop keeps stale/hidden selection helper text around even when a
            // valid category/subcategory is selected, so do not decide from visible
            // "Select a subcategory" text alone. Try the active row table first; when
            // no active row table is present, treat the shop as not ready and hide the
            // available amount so the UI does not look purchasable.
            var inclusionRows = TryReadRowsFromAtkValues(addonName, addon, textFragments);
            if (inclusionRows.Count > 0)
            {
                status = pending is null ? $"Detected {inclusionRows.Count} shop item row(s) from {addonName} addon values." : status;
                return inclusionRows;
            }

            currentCurrencyAmount = null;
            status = pending is null
                ? $"{addonName} is open, but no active shop category/subcategory is selected."
                : status;
            return Array.Empty<ShopItemEntry>();
        }

        if (string.Equals(addonName, "GrandCompanyExchange", StringComparison.Ordinal))
        {
            // Grand Company seal exchange looks like an exchange shop, but the visible text
            // nodes only expose headers/rank messages while the actual rows live in AtkValues
            // as one item-name block followed by one same-length seal-cost block. Do not fall
            // through to the generic visible-text parser here, because it can turn rank/status
            // labels such as "You are not a member..." into fake purchasable rows.
            var grandCompanyRows = TryReadRowsFromAtkValues(addonName, addon, textFragments);
            if (grandCompanyRows.Count > 0)
            {
                status = pending is null ? $"Detected {grandCompanyRows.Count} shop item row(s) from {addonName} addon values." : status;
                return grandCompanyRows;
            }

            status = pending is null
                ? $"{addonName} is open, but no seal exchange item rows were readable yet."
                : status;
            return Array.Empty<ShopItemEntry>();
        }

        // For the normal gil Shop window, use AgentShop first. It has the actual item ids,
        // costs, and row order used by the shop callbacks. Reading AddonShop.BuyList first
        // produced raw SeString/link labels such as H=%I=&...IH on some clients.
        var agentRows = TryReadRowsFromAgentShop(addonName, textFragments);
        if (agentRows.Count > 0)
        {
            agentRows = EnrichShopRowsFromAtkValues(addon, agentRows);
            status = pending is null ? $"Detected {agentRows.Count} shop item row(s) from {addonName} agent data." : status;
            return agentRows;
        }

        // Avoid polling AtkComponentList.GetItemLabel for normal Shop windows. On some shop
        // list components that call can disturb the native list's visible row recycling while
        // the player scrolls. The addon backing AtkValues contain the same row names/prices
        // without touching the game's list widget, so use those first whenever AgentShop is
        // unavailable or incomplete.
        if (string.Equals(addonName, "Shop", StringComparison.Ordinal))
        {
            var valueRowsForShop = TryReadRowsFromAtkValues(addonName, addon, textFragments);
            if (valueRowsForShop.Count > 0)
            {
                status = pending is null ? $"Detected {valueRowsForShop.Count} shop item row(s) from {addonName} addon values." : status;
                return valueRowsForShop;
            }
        }

        var specificListRows = TryReadRowsFromSpecificShopList(addonName, addon, textFragments);
        if (specificListRows.Count > 0)
        {
            specificListRows = EnrichRowsWithAgentShopData(addonName, specificListRows);
            specificListRows = EnrichShopRowsFromAtkValues(addon, specificListRows);
            status = pending is null ? $"Detected {specificListRows.Count} shop item row(s) from {addonName} buy list." : status;
            return specificListRows;
        }

        // For exchange/category shops, the rendered list rows are safer than addon values.
        // AtkValues can contain filter/dropdown entries and metadata, so try row renderers/lists
        // before falling back to value arrays.
        var listRows = TryReadRowsFromAtkLists(addonName, addon, textFragments);
        if (listRows.Count > 0)
        {
            status = pending is null ? $"Detected {listRows.Count} shop item row(s) from {addonName} list data." : status;
            return listRows;
        }

        var rows = BuildShopRows(addonName, textFragments);
        if (rows.Count > 0)
        {
            status = pending is null ? $"Detected {rows.Count} visible item row(s) from {addonName}." : status;
            return rows;
        }

        var valueRows = TryReadRowsFromAtkValues(addonName, addon, textFragments);
        if (valueRows.Count > 0)
        {
            status = pending is null ? $"Detected {valueRows.Count} shop item row(s) from {addonName} addon values." : status;
            return valueRows;
        }

        rows = BuildShopRows(addonName, textFragments);
        if (rows.Count == 0)
        {
            status = textFragments.Count == 0
                ? $"{addonName} is open, but no shop text/list nodes were readable yet."
                : $"{addonName} is open, but no item rows were matched from {textFragments.Count} visible text/counter node(s).";
            return Array.Empty<ShopItemEntry>();
        }

        status = pending is null ? $"Detected {rows.Count} visible item row(s) from {addonName}." : status;
        return rows;
    }

    private bool TryGetOpenShopAddon(out AtkUnitBase* addon, out string addonName)
    {
        foreach (var name in ShopAddonNames)
        {
            var ptr = DalamudServices.GameGui.GetAddonByName(name);
            if (ptr.IsNull)
                continue;

            var unit = (AtkUnitBase*)ptr.Address;
            if (unit is null || !unit->IsVisible)
                continue;

            addon = unit;
            addonName = name;
            return true;
        }

        addon = null;
        addonName = string.Empty;
        return false;
    }


    private static List<ShopItemEntry> EnrichRowsWithAgentShopData(string addonName, List<ShopItemEntry> rows)
    {
        if (!string.Equals(addonName, "Shop", StringComparison.Ordinal) || rows.Count == 0)
            return rows;

        var agentRows = TryReadRowsFromAgentShop(addonName, Array.Empty<TextFragment>());
        if (agentRows.Count == 0)
            return rows;

        var enriched = new List<ShopItemEntry>(rows.Count);
        foreach (var row in rows)
        {
            var agentRow = agentRows.FirstOrDefault(x => x.RowIndex == row.RowIndex);
            if (agentRow is null && row.RowIndex >= 0 && row.RowIndex < agentRows.Count)
                agentRow = agentRows[row.RowIndex];
            if (agentRow is null)
                agentRow = agentRows.FirstOrDefault(x => string.Equals(x.Name, row.Name, StringComparison.OrdinalIgnoreCase));

            enriched.Add(new ShopItemEntry
            {
                AddonName = row.AddonName,
                UnitName = !string.IsNullOrWhiteSpace(row.UnitName) ? row.UnitName : GetDefaultUnitName(row.AddonName),
                RowIndex = row.RowIndex,
                Name = agentRow is not null && !string.IsNullOrWhiteSpace(agentRow.Name) ? agentRow.Name : row.Name,
                Price = row.Price ?? agentRow?.Price,
                CanSelectQuantity = row.CanSelectQuantity,
            });
        }

        return enriched;
    }

    private static List<ShopItemEntry> EnrichShopRowsFromAtkValues(AtkUnitBase* addon, List<ShopItemEntry> rows)
    {
        if (rows.Count == 0)
            return rows;

        var normalShopHasQuantityColumn = rows.Any(x => string.Equals(x.AddonName, "Shop", StringComparison.Ordinal))
                                      && ShopHasQuantityColumn(addon);

        var values = ExtractAddonValueFragments(addon);
        if (values.Count == 0)
            return rows;

        var itemBlock = FindOrderedItemNameBlock(values, rows.Count);
        var priceBlock = BuildOrderedShopPriceNumberBlock(values, itemBlock.Count > 0 ? itemBlock : BuildFallbackItemCandidates(values, rows));
        if (priceBlock.Count == 0 && itemBlock.Count > 0)
            priceBlock = BuildOrderedExchangePriceTextBlock(values, itemBlock);

        var enriched = new List<ShopItemEntry>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var nameFromValues = i < itemBlock.Count ? itemBlock[i].Text : row.Name;
            var cleanName = PreferFullName(nameFromValues, row.Name);
            var price = i < priceBlock.Count && priceBlock[i] > 0 ? priceBlock[i] : row.Price;
            var canSelectQuantity = row.CanSelectQuantity || normalShopHasQuantityColumn || TryGetItemStackable(cleanName);

            enriched.Add(new ShopItemEntry
            {
                AddonName = row.AddonName,
                UnitName = row.UnitName,
                RowIndex = row.RowIndex,
                Name = cleanName,
                Price = price,
                CanSelectQuantity = canSelectQuantity,
            });
        }

        return enriched;
    }

    private static bool ShopHasQuantityColumn(AtkUnitBase* addon)
    {
        if (addon is null)
            return false;

        return ExtractAddonValueFragments(addon).Any(x =>
            x.Number is null &&
            string.Equals(CleanShopText(x.Text), "Quantity", StringComparison.OrdinalIgnoreCase));
    }

    private static List<AddonValueFragment> BuildFallbackItemCandidates(IReadOnlyList<AddonValueFragment> values, IReadOnlyList<ShopItemEntry> rows)
    {
        var result = new List<AddonValueFragment>();
        foreach (var row in rows)
        {
            var match = values.FirstOrDefault(x => x.Number is null && ItemNamesMatch(row.Name, x.Text));
            if (match is not null)
                result.Add(match);
        }

        return result.OrderBy(x => x.Index).ToList();
    }

    private static List<AddonValueFragment> FindOrderedItemNameBlock(IReadOnlyList<AddonValueFragment> values, int needed)
    {
        if (needed <= 0)
            return [];

        var runs = new List<List<AddonValueFragment>>();
        var current = new List<AddonValueFragment>();
        var previousIndex = -1000;

        foreach (var value in values.Where(x => x.Number is null).OrderBy(x => x.Index))
        {
            var isItem = IsKnownItemName(value.Text);
            if (!isItem)
            {
                if (current.Count > 0)
                    runs.Add(current);
                current = [];
                previousIndex = -1000;
                continue;
            }

            if (current.Count == 0 || value.Index == previousIndex + 1)
                current.Add(value with { Text = CleanShopText(value.Text) });
            else
            {
                runs.Add(current);
                current = [value with { Text = CleanShopText(value.Text) }];
            }

            previousIndex = value.Index;
        }

        if (current.Count > 0)
            runs.Add(current);

        return runs.FirstOrDefault(x => x.Count >= needed)?.Take(needed).ToList() ?? [];
    }

    private static List<int> BuildOrderedShopPriceNumberBlock(IReadOnlyList<AddonValueFragment> values, IReadOnlyList<AddonValueFragment> itemCandidates)
    {
        if (itemCandidates.Count == 0)
            return [];

        var needed = itemCandidates.Count;
        var lastItemIndex = itemCandidates.Max(x => x.Index);
        var runs = new List<List<int>>();
        var current = new List<int>();
        var previousIndex = -1000;

        foreach (var value in values.Where(x => x.Index > lastItemIndex).OrderBy(x => x.Index))
        {
            if (value.Number is not > 0)
            {
                if (current.Count > 0)
                    runs.Add(current);
                current = [];
                previousIndex = -1000;
                continue;
            }

            if (current.Count == 0 || value.Index == previousIndex + 1)
                current.Add(value.Number.Value);
            else
            {
                runs.Add(current);
                current = [value.Number.Value];
            }

            previousIndex = value.Index;
        }

        if (current.Count > 0)
            runs.Add(current);

        var exact = runs.FirstOrDefault(x => x.Count == needed);
        if (exact is not null)
            return exact;

        var largeEnough = runs.FirstOrDefault(x => x.Count > needed);
        return largeEnough?.Take(needed).ToList() ?? [];
    }

    private static string PreferFullName(string candidate, string fallback)
    {
        candidate = CleanShopText(candidate);
        fallback = CleanShopText(fallback);
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;
        if (string.IsNullOrWhiteSpace(fallback) || fallback.Contains("...", StringComparison.Ordinal))
            return candidate;
        return candidate.Length > fallback.Length && candidate.StartsWith(fallback.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            ? candidate
            : fallback;
    }

    private static bool ItemNamesMatch(string left, string right)
    {
        left = CleanShopText(left);
        right = CleanShopText(right);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        var leftPrefix = left.Replace("...", string.Empty, StringComparison.Ordinal).Trim();
        var rightPrefix = right.Replace("...", string.Empty, StringComparison.Ordinal).Trim();
        return leftPrefix.Length >= 6 && right.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase)
               || rightPrefix.Length >= 6 && left.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ShopItemEntry> TryReadRowsFromSpecificShopList(string addonName, AtkUnitBase* addon, IReadOnlyList<TextFragment> visibleText)
    {
        // Disabled for live scanning. The old path called AtkComponentList.GetItemLabel on
        // every row of AddonShop.BuyList; user testing showed that can disturb the native
        // shop list's visible renderer order while scrolling. AgentShop and AtkValues now
        // provide the same row data without touching the game's list widget.
        return [];
    }

    private static List<ShopItemEntry> ReadRowsFromList(string addonName, AtkComponentList* list, IReadOnlyList<TextFragment> visibleText, bool preferKnownItems)
    {
        if (list is null)
            return [];

        var rows = new List<ShopItemEntry>();
        var count = list->GetItemCount();
        if (count <= 0)
            count = list->ListLength;

        // Build the visible-row lookup once. BuyList labels sometimes contain raw link
        // payloads, while the visible row text usually contains the clean display text and
        // same-row numeric price nodes. Match by cleaned name first, then by visible row
        // order as a fallback. This is not capped to the number eight; it uses the actual
        // list count and only uses visible rows for price enrichment when available.
        var cleanedVisible = visibleText
            .Select(x => x with { Text = CleanShopText(x.Text) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
        var visibleItems = cleanedVisible
            .Where(x => !TryParsePositiveOrZeroInt(x.Text, out _) && IsLikelyItemName(x.Text))
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .ToList();
        var visibleNumbers = cleanedVisible
            .Where(x => TryParsePositiveOrZeroInt(x.Text, out _))
            .ToList();

        count = Math.Clamp(count, 0, 5000);
        for (var i = 0; i < count; i++)
        {
            var label = CleanShopText(list->GetItemLabel(i).ToString());
            if (string.IsNullOrWhiteSpace(label))
                continue;

            // The standard shop list can expose category/header labels through neighbouring
            // lists. For the concrete BuyList, prefer actual game Item sheet names, then fall
            // back to the normal item-name heuristic for unusual/custom rows.
            if (preferKnownItems && !IsKnownItemName(label) && !IsLikelyItemName(label))
                continue;

            if (!preferKnownItems && !IsLikelyItemName(label))
                continue;

            int? price = null;
            var visibleName = visibleItems.FirstOrDefault(x => string.Equals(x.Text, label, StringComparison.OrdinalIgnoreCase));
            if (visibleName is null && i >= 0 && i < visibleItems.Count)
                visibleName = visibleItems[i];
            if (visibleName is not null)
                price = TryFindPriceForRow(visibleName, visibleNumbers);

            // Last fallback for normal Shop rows: if visible row names and labels are in the
            // same order but exact Y matching failed, use the numeric fragments nearest the
            // row ordered by Y. This recovers prices from counter nodes that render a few
            // pixels away from the text baseline.
            if (price is null && string.Equals(addonName, "Shop", StringComparison.Ordinal) && i >= 0 && i < visibleItems.Count)
                price = TryFindPriceForRowRelaxed(visibleItems[i], visibleNumbers);

            // Normal gil shops often sell at the Item sheet vendor price; use it only as a
            // fallback after UI/agent reads fail so non-gil/exchange shops are still UI-driven.
            if (price is null && string.Equals(addonName, "Shop", StringComparison.Ordinal))
                price = TryGetItemSheetPrice(label);

            rows.Add(new ShopItemEntry
            {
                AddonName = addonName,
                UnitName = GetDefaultUnitName(addonName),
                RowIndex = i,
                Name = label,
                Price = price,
                CanSelectQuantity = HasVisibleQuantitySelector(label, visibleText) || TryGetItemStackable(label),
            });
        }

        return rows
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.RowIndex)
            .ToList();
    }

    private static List<AddonValueFragment> ExtractAddonValueFragments(AtkUnitBase* addon)
    {
        if (addon is null || addon->AtkValues is null || addon->AtkValuesCount == 0)
            return [];

        var values = new List<AddonValueFragment>();
        var count = Math.Clamp((int)addon->AtkValuesCount, 0, 5000);
        for (var i = 0; i < count; i++)
        {
            try
            {
                var value = addon->AtkValues[i];
                switch (value.Type)
                {
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.WideString:
                    {
                        var text = CleanShopText(value.GetValueAsString());
                        if (!string.IsNullOrWhiteSpace(text))
                            values.Add(new AddonValueFragment(i, text, null));
                        break;
                    }
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int:
                        values.Add(new AddonValueFragment(i, value.Int.ToString(), value.Int));
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt:
                        if (value.UInt <= int.MaxValue)
                            values.Add(new AddonValueFragment(i, value.UInt.ToString(), (int)value.UInt));
                        break;
                }
            }
            catch
            {
                // Bad or transient addon values should not break the whole reader.
            }
        }

        return values;
    }

    private static List<ShopItemEntry> TryReadRowsFromAtkValues(string addonName, AtkUnitBase* addon, IReadOnlyList<TextFragment> visibleText)
    {
        // Addon values can contain non-shop controls such as job/category dropdown
        // entries on exchange windows. They are still useful as a fallback, but exchange
        // addons are filtered to real Item-sheet names only below so dropdown entries like
        // Paladin / Gladiator are not exposed as purchasable rows.
        var values = ExtractAddonValueFragments(addon);
        if (values.Count == 0)
            return [];

        if (string.Equals(addonName, "InclusionShop", StringComparison.Ordinal))
        {
            // InclusionShop has lots of stale dropdown/category/item data in AtkValues after
            // changing category/subcategory selections. Never let it fall through to the
            // generic item-name parser, because that will repopulate stale rows after the
            // game UI is back on "-Select a subcategory-". The dedicated parser returns
            // either the active row table or an empty list.
            return TryReadRowsFromInclusionShopValues(addonName, values, visibleText);
        }

        if (string.Equals(addonName, "GrandCompanyExchange", StringComparison.Ordinal))
        {
            // GrandCompanyExchange also needs a dedicated parser. Its AtkValues contain
            // rank/status text and rank-tab labels in addition to purchasable rows, so the
            // generic exchange parser can pick those non-row strings.
            return TryReadRowsFromGrandCompanyExchangeValues(addonName, values);
        }

        var stringValues = values
            .Where(x => x.Number is null)
            .Select(x => x with { Text = CleanShopText(x.Text) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();

        // First pass: exact Item-sheet names. This avoids category/header strings like
        // "Materia", "Set", "Qty.", or job filter dropdown values being treated as rows.
        var candidates = stringValues.Where(x => IsKnownItemName(x.Text)).ToList();

        // Only normal/basic shop addons use the loose fallback. Exchange shops have too many
        // non-item strings in AtkValues, especially filter/dropdown labels.
        if (candidates.Count == 0 && !addonName.Contains("Exchange", StringComparison.OrdinalIgnoreCase))
            candidates = stringValues.Where(x => IsLikelyItemName(x.Text)).ToList();

        var unitName = GetDefaultUnitName(addonName);
        var isExchangeAddon = addonName.Contains("Exchange", StringComparison.OrdinalIgnoreCase);
        if (isExchangeAddon)
        {
            // Exchange windows store the payment currency as data, not as a column header.
            // In the dumps this pattern is:
            //   current amount text/number -> payment item id -> payment item/currency name -> purchasable item names.
            // Treat that detected payment name as the Unit column and remove only that value
            // from the candidate item list. This avoids keyword checks that miss currency-like
            // names such as "Auxesia Dronebit".
            var unitValue = TryFindExchangeUnitValue(values, visibleText);
            if (unitValue is not null)
            {
                unitName = unitValue.Text;
                candidates = candidates.Where(x => x.Index != unitValue.Index).ToList();
            }
            else if (candidates.Count > 1)
            {
                // Fallback for older/odd exchange addons where the currency name is only
                // recognizable by text and appears before the actual item rows.
                var possibleUnit = candidates[0].Text;
                if (IsLikelyCurrencyUnitName(possibleUnit))
                {
                    unitName = possibleUnit;
                    candidates = candidates.Skip(1).ToList();
                }
            }
        }

        var normalShopHasQuantityColumn = string.Equals(addonName, "Shop", StringComparison.Ordinal)
                                      && ShopHasQuantityColumn(addon);

        var rows = new List<ShopItemEntry>();
        var candidateIndexes = candidates.Select(x => x.Index).OrderBy(x => x).ToList();
        var numericValues = values.Where(x => x.Number is > 0).OrderBy(x => x.Index).ToList();
        var visibleNumbers = visibleText
            .Select(x => x with { Text = CleanShopText(x.Text) })
            .Where(x => TryParsePositiveOrZeroInt(x.Text, out _))
            .ToList();

        var orderedExchangePrices = isExchangeAddon
            ? BuildOrderedExchangePriceTextBlock(values, candidates)
            : new List<int>();
        if (isExchangeAddon && orderedExchangePrices.Count == 0)
            orderedExchangePrices = BuildOrderedExchangePriceList(numericValues, candidates.Select(x => x.Text).ToList(), unitName);

        var orderedShopPrices = !isExchangeAddon
            ? BuildOrderedShopPriceNumberBlock(values, candidates)
            : new List<int>();

        foreach (var candidate in candidates)
        {
            if (rows.Any(x => string.Equals(x.Name, candidate.Text, StringComparison.OrdinalIgnoreCase)))
                continue;

            var visibleName = visibleText
                .Select(x => x with { Text = CleanShopText(x.Text) })
                .FirstOrDefault(x => string.Equals(x.Text, candidate.Text, StringComparison.OrdinalIgnoreCase));

            var price = 0;

            if (isExchangeAddon)
            {
                // Exchange addons often expose rows as separated value blocks rather than as
                // usable rendered row nodes. The most reliable block from the debug dumps is:
                //   item name block -> item id/metadata blocks -> display price text block.
                // Prefer visible same-row price text only when it exists; otherwise use the
                // detected ordered price-text block before trying older per-candidate fallbacks.
                if (visibleName is not null)
                    price = TryFindPriceForRowRelaxed(visibleName, visibleNumbers) ?? 0;

                if (price <= 0 && rows.Count < orderedExchangePrices.Count)
                    price = orderedExchangePrices[rows.Count];

                if (price <= 0)
                    price = TryFindExchangePriceAfterCandidate(values, candidate, candidateIndexes, candidates.Select(x => x.Text), unitName);
            }
            else
            {
                if (rows.Count < orderedShopPrices.Count)
                    price = orderedShopPrices[rows.Count];

                if (price <= 0)
                {
                    var nextCandidateIndex = candidateIndexes.FirstOrDefault(x => x > candidate.Index);
                    price = values
                        .Where(x => x.Number is > 1
                                    && x.Index > candidate.Index
                                    && (nextCandidateIndex <= 0 || x.Index < nextCandidateIndex))
                        .Select(x => x.Number!.Value)
                        .FirstOrDefault();
                }

                if (price <= 0)
                {
                    var priceValuesByOrder = numericValues.Select(x => x.Number!.Value).ToList();
                    var leadingNumbersToSkip = Math.Max(0, priceValuesByOrder.Count - candidates.Count);
                    var orderedPriceIndex = leadingNumbersToSkip + rows.Count;
                    if (orderedPriceIndex >= 0 && orderedPriceIndex < priceValuesByOrder.Count)
                        price = priceValuesByOrder[orderedPriceIndex];
                }

                if (price <= 0 && visibleName is not null)
                    price = TryFindPriceForRowRelaxed(visibleName, visibleNumbers) ?? 0;
            }

            rows.Add(new ShopItemEntry
            {
                AddonName = addonName,
                UnitName = unitName,
                RowIndex = rows.Count,
                Name = candidate.Text,
                Price = price > 0 ? price : null,
                CanSelectQuantity = normalShopHasQuantityColumn || HasVisibleQuantitySelector(candidate.Text, visibleText) || TryGetItemStackable(candidate.Text),
            });
        }

        return rows;
    }



    private static List<ShopItemEntry> TryReadRowsFromGrandCompanyExchangeValues(string addonName, IReadOnlyList<AddonValueFragment> values)
    {
        // Grand Company Seal Exchange dump layout:
        //   item-name link block -> same-length seal price block -> other rank/stock/item-id blocks.
        // The visible text nodes only contain headers and current rank/status messages, so this
        // parser deliberately ignores visible text for row names.
        var itemRuns = new List<List<AddonValueFragment>>();
        var current = new List<AddonValueFragment>();
        var previousIndex = -1000;

        foreach (var value in values.Where(x => x.Number is null).OrderBy(x => x.Index))
        {
            var cleaned = CleanShopText(value.Text);
            var isItem = IsKnownItemName(cleaned);
            if (!isItem)
            {
                if (current.Count > 0)
                    itemRuns.Add(current);

                current = [];
                previousIndex = -1000;
                continue;
            }

            if (current.Count == 0 || value.Index == previousIndex + 1)
                current.Add(value with { Text = cleaned });
            else
            {
                itemRuns.Add(current);
                current = [value with { Text = cleaned }];
            }

            previousIndex = value.Index;
        }

        if (current.Count > 0)
            itemRuns.Add(current);

        // Use the longest real item-name run. Single strings elsewhere in the addon can be
        // rank names or helper text; the actual exchange rows are a block.
        var items = itemRuns
            .Where(x => x.Count >= 2)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x[0].Index)
            .FirstOrDefault();
        if (items is null || items.Count == 0)
            return [];

        var prices = BuildOrderedShopPriceNumberBlock(values, items);
        var rows = new List<ShopItemEntry>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var price = i < prices.Count ? prices[i] : 0;
            rows.Add(new ShopItemEntry
            {
                AddonName = addonName,
                UnitName = GetDefaultUnitName(addonName),
                RowIndex = i,
                Name = items[i].Text,
                Price = price > 0 ? price : null,
                CanSelectQuantity = TryGetItemStackable(items[i].Text),
            });
        }

        return rows;
    }

    private static List<ShopItemEntry> TryReadRowsFromInclusionShopValues(string addonName, IReadOnlyList<AddonValueFragment> values, IReadOnlyList<TextFragment> visibleText)
    {
        // InclusionShop (scrip exchange) does not expose the current rows as simple text/list
        // nodes. Its AtkValues include category/subcategory dropdown strings first, then a
        // compact current-row record table. From the debug dump the stable structure is:
        //   currency item id, current currency amount, current row count, flags,
        //   then row records of 18 UInt values each. In each row record:
        //   +0 = reward item id, +1 = required currency item id, +11 = required amount.
        // Reading only this current-row table prevents dropdown/category entries and other
        // subcategories from being shown as purchasable rows, and it updates when the user
        // changes either dropdown.
        var currentAmounts = visibleText
            .Select(x => CleanShopText(x.Text))
            .Select(x => TryParsePositiveOrZeroInt(x, out var parsed) ? parsed : (int?)null)
            .Where(x => x is > 0)
            .Select(x => x!.Value)
            .Distinct()
            .ToHashSet();

        if (currentAmounts.Count == 0)
            return [];

        // InclusionShop keeps the previously-selected row table in AtkValues after the
        // player changes a dropdown back to "-Select a subcategory-". The real UI shows
        // no purchasable rows in that state, so ignore the stale row table. The selected
        // subcategory index is stored immediately before the "-Select a subcategory-"
        // option string in the dropdown value block: 0 = nothing selected, >0 = active
        // subcategory selected.
        if (InclusionShopHasNoActiveSubcategory(values))
            return [];

        foreach (var amountValue in values.Where(x => x.Number is int n && currentAmounts.Contains(n)).OrderByDescending(x => x.Index))
        {
            var currencyId = values.FirstOrDefault(x => x.Index == amountValue.Index - 1 && x.Number is > 1000)?.Number;
            var rowCountValue = values.FirstOrDefault(x => x.Index == amountValue.Index + 1 && x.Number is > 0 and <= 500)?.Number;
            if (currencyId is null || rowCountValue is null)
                continue;

            var rowCount = Math.Clamp(rowCountValue.Value, 0, 500);
            if (rowCount <= 0)
                continue;

            var rowStart = amountValue.Index + 3;
            const int RowStride = 18;
            const int PriceOffset = 11;
            var unitName = GetItemNameFromSheet((uint)currencyId.Value);
            if (string.IsNullOrWhiteSpace(unitName))
                unitName = GetDefaultUnitName(addonName);

            var rows = new List<ShopItemEntry>();
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var start = rowStart + (rowIndex * RowStride);
                var itemId = values.FirstOrDefault(x => x.Index == start)?.Number ?? 0;
                var price = values.FirstOrDefault(x => x.Index == start + PriceOffset)?.Number ?? 0;
                if (itemId <= 0)
                    break;

                var name = GetItemNameFromSheet((uint)itemId);
                if (string.IsNullOrWhiteSpace(name) || !IsKnownItemName(name))
                    break;

                rows.Add(new ShopItemEntry
                {
                    AddonName = addonName,
                    UnitName = unitName,
                    RowIndex = rowIndex,
                    Name = name,
                    Price = price > 0 ? price : null,
                    // InclusionShop rows do not expose a simple per-row quantity-control flag
                    // in the passive row table. Use the Item sheet stackability so stackable
                    // scrip/exchange items can use quantity defaults while gear/books remain
                    // one-at-a-time.
                    CanSelectQuantity = TryGetItemStackable(name),
                });
            }

            if (rows.Count > 0)
                return rows;
        }

        return [];
    }

    private static bool InclusionShopHasNoActiveSubcategory(IReadOnlyList<AddonValueFragment> values)
    {
        // The subcategory dropdown stores its selected index immediately before, or very
        // close before, the "-Select a subcategory-" option string. Some layouts keep
        // stale row records after the player returns the dropdown to the placeholder, so
        // this must be checked before reading those row records.
        foreach (var option in values.Where(x => x.Number is null
                                             && CleanShopText(x.Text).Contains("Select a subcategory", StringComparison.OrdinalIgnoreCase)))
        {
            var selectedIndex = values
                .Where(x => x.Index < option.Index && x.Index >= option.Index - 4 && x.Number.HasValue)
                .OrderByDescending(x => x.Index)
                .Select(x => x.Number.GetValueOrDefault())
                .FirstOrDefault(-1);

            if (selectedIndex == 0)
                return true;

            if (selectedIndex > 0)
                return false;
        }

        return false;
    }

    private static AddonValueFragment? TryFindExchangeUnitValue(IReadOnlyList<AddonValueFragment> values, IReadOnlyList<TextFragment> visibleText)
    {
        if (values.Count == 0)
            return null;

        // The top-right amount is the current balance. Find that balance in AtkValues, then
        // look just after it for the payment/currency item id and display name. This is a
        // structural check, not a hardcoded currency-name list.
        var visibleAmounts = visibleText
            .Select(x => CleanShopText(x.Text))
            .Select(x => TryParsePositiveOrZeroInt(x, out var parsed) ? parsed : (int?)null)
            .Where(x => x is > 0)
            .Select(x => x!.Value)
            .Distinct()
            .ToHashSet();

        if (visibleAmounts.Count == 0)
            return null;

        foreach (var amountValue in values.Where(x => x.Number is not null && visibleAmounts.Contains(x.Number.Value)).OrderBy(x => x.Index))
        {
            var following = values
                .Where(x => x.Index > amountValue.Index && x.Index <= amountValue.Index + 8)
                .OrderBy(x => x.Index)
                .ToList();

            // Prefer: amount number -> currency item id -> currency name.
            foreach (var idValue in following.Where(x => x.Number is > 1000))
            {
                var nameValue = following.FirstOrDefault(x => x.Index > idValue.Index
                                                              && x.Number is null
                                                              && !string.IsNullOrWhiteSpace(x.Text)
                                                              && IsKnownItemName(x.Text));
                if (nameValue is not null)
                    return nameValue;
            }

            // Fallback: first known item-ish string immediately after the balance. This is
            // still constrained to the balance area and will not remove normal row names that
            // appear much later in the value list.
            var nearbyName = following.FirstOrDefault(x => x.Number is null
                                                           && !string.IsNullOrWhiteSpace(x.Text)
                                                           && IsKnownItemName(x.Text));
            if (nearbyName is not null)
                return nearbyName;
        }

        return null;
    }

    private static List<int> BuildOrderedExchangePriceTextBlock(IReadOnlyList<AddonValueFragment> values, IReadOnlyList<AddonValueFragment> itemCandidates)
    {
        if (itemCandidates.Count == 0)
            return [];

        var lastItemIndex = itemCandidates.Max(x => x.Index);
        var needed = itemCandidates.Count;
        var runs = new List<List<int>>();
        var current = new List<int>();
        var previousIndex = -1000;

        foreach (var value in values.Where(x => x.Index > lastItemIndex).OrderBy(x => x.Index))
        {
            // Prefer the strings/managed strings that back the actual visible Price column.
            // Integer blocks after the item names also contain item ids, selected filters,
            // owned counts, or quantity values, which caused values such as 2..35, 47,929,
            // or 999 to be treated as prices.
            if (value.Number is not null || !TryParsePositiveOrZeroInt(value.Text, out var parsed) || parsed <= 0)
            {
                if (current.Count > 0)
                    runs.Add(current);

                current = [];
                previousIndex = -1000;
                continue;
            }

            if (current.Count == 0 || value.Index == previousIndex + 1)
            {
                current.Add(parsed);
            }
            else
            {
                runs.Add(current);
                current = [parsed];
            }

            previousIndex = value.Index;
        }

        if (current.Count > 0)
            runs.Add(current);

        // The correct price block normally has one numeric display string for every item
        // row. Use the first matching block after the item-name block; later same-length
        // numeric blocks are usually Qty./owned counts or repeated numeric mirrors.
        var exact = runs.FirstOrDefault(x => x.Count == needed);
        if (exact is not null)
            return exact;

        var largeEnough = runs.FirstOrDefault(x => x.Count > needed);
        if (largeEnough is not null)
            return largeEnough.Take(needed).ToList();

        return [];
    }

    private static List<int> BuildOrderedExchangePriceList(IReadOnlyList<AddonValueFragment> numericValues, IReadOnlyList<string> itemNames, string unitName)
    {
        if (itemNames.Count <= 0)
            return [];

        var itemIds = itemNames
            .Select(TryGetItemIdByName)
            .Where(x => x.HasValue)
            .Select(x => (int)x!.Value)
            .ToHashSet();

        var unitId = TryGetItemIdByName(unitName);
        var unitIdInt = unitId.HasValue ? (int)unitId.Value : 0;

        // In special/currency shops, AtkValues commonly store row data like:
        //   received item id, payment/currency item id, payment count, ...
        // Previous versions treated the payment item id (for example Cosmocredit) or row
        // metadata as the price. Prefer numbers immediately following the payment item id.
        var values = numericValues
            .OrderBy(x => x.Index)
            .Select(x => x.Number ?? 0)
            .Where(x => x > 0)
            .ToList();

        var pricesAfterUnit = new List<int>();
        if (unitIdInt > 0)
        {
            for (var i = 0; i < values.Count - 1; i++)
            {
                if (values[i] != unitIdInt)
                    continue;

                var price = values[i + 1];
                if (price > 0 && !itemIds.Contains(price) && price != unitIdInt)
                    pricesAfterUnit.Add(price);
            }
        }

        if (pricesAfterUnit.Count >= Math.Min(itemNames.Count, 1))
            return pricesAfterUnit.Take(itemNames.Count).ToList();

        // Fallback: strip known item ids and the currency item id, but do not throw away
        // small values. Some real exchange prices are 30, 40, or 60.
        var filtered = values
            .Where(x => !itemIds.Contains(x) && x != unitIdInt)
            .ToList();

        if (filtered.Count > itemNames.Count)
            filtered = filtered.Skip(filtered.Count - itemNames.Count).ToList();

        return filtered.Take(itemNames.Count).ToList();
    }

    private static int TryFindExchangePriceAfterCandidate(
        IReadOnlyList<AddonValueFragment> values,
        AddonValueFragment candidate,
        IReadOnlyList<int> candidateIndexes,
        IEnumerable<string> itemNames, string unitName)
    {
        var itemIds = itemNames
            .Select(TryGetItemIdByName)
            .Where(x => x.HasValue)
            .Select(x => (int)x!.Value)
            .ToHashSet();

        var unitId = TryGetItemIdByName(unitName);
        var unitIdInt = unitId.HasValue ? (int)unitId.Value : 0;

        var nextCandidateIndex = candidateIndexes.FirstOrDefault(x => x > candidate.Index);
        var after = values
            .Where(x => x.Number is > 0
                        && x.Index > candidate.Index
                        && (nextCandidateIndex <= 0 || x.Index < nextCandidateIndex))
            .OrderBy(x => x.Index)
            .Select(x => x.Number!.Value)
            .ToList();

        if (unitIdInt > 0)
        {
            for (var i = 0; i < after.Count - 1; i++)
            {
                if (after[i] != unitIdInt)
                    continue;

                var price = after[i + 1];
                if (price > 0 && !itemIds.Contains(price) && price != unitIdInt)
                    return price;
            }
        }

        return after.FirstOrDefault(x => x > 0 && !itemIds.Contains(x) && x != unitIdInt);
    }

    private static bool LooksLikeUiOrdinal(int value)
    {
        // Row indexes, category ids, set ids, and selected-tab markers commonly show up in
        // exchange AtkValues. Actual exchange costs can be small in some shops, so this is kept
        // narrow and is used only after item ids have already been removed.
        return value is >= 1 and <= 99;
    }

    private static string GetItemNameFromSheet(uint itemId)
    {
        if (itemId == 0)
            return string.Empty;

        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();
            var row = sheet.GetRow(itemId);
            return CleanShopText(row.Name.ToString());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsKnownItemName(string text)
    {
        text = CleanShopText(text);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var cache = knownItemNames;
        if (cache is null)
        {
            cache = [];
            try
            {
                foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
                {
                    var name = CleanShopText(item.Name.ToString());
                    if (!string.IsNullOrWhiteSpace(name))
                        cache.Add(name);
                }
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Verbose(ex, "Failed to build item name cache.");
            }

            knownItemNames = cache;
        }

        return cache.Contains(text);
    }

    private static uint? TryGetItemIdByName(string text)
    {
        text = CleanShopText(text);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cache = knownItemIds;
        if (cache is null)
        {
            cache = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
                {
                    var name = CleanShopText(item.Name.ToString());
                    if (!string.IsNullOrWhiteSpace(name) && !cache.ContainsKey(name))
                        cache[name] = item.RowId;
                }
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Verbose(ex, "Failed to build item id cache.");
            }

            knownItemIds = cache;
        }

        return cache.TryGetValue(text, out var rowId) ? rowId : null;
    }

    private static bool TryGetItemStackable(string text)
    {
        text = CleanShopText(text);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var cache = knownItemStackable;
        if (cache is null)
        {
            cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
                {
                    var name = CleanShopText(item.Name.ToString());
                    if (!string.IsNullOrWhiteSpace(name) && !cache.ContainsKey(name))
                        cache[name] = item.StackSize > 1;
                }
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Verbose(ex, "Failed to build item stack cache.");
            }

            knownItemStackable = cache;
        }

        return cache.TryGetValue(text, out var stackable) && stackable;
    }

    private static int? TryGetItemSheetPrice(string text)
    {
        text = CleanShopText(text);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cache = knownItemPrices;
        if (cache is null)
        {
            cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
                {
                    var name = CleanShopText(item.Name.ToString());
                    if (string.IsNullOrWhiteSpace(name) || cache.ContainsKey(name))
                        continue;

                    var price = 0;
                    if (item.PriceMid > 0 && item.PriceMid <= 2147483647)
                        price = (int)item.PriceMid;
                    else if (item.PriceLow > 0 && item.PriceLow <= 2147483647)
                        price = (int)item.PriceLow;

                    if (price > 0)
                        cache[name] = price;
                }
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Verbose(ex, "Failed to build item price cache.");
            }

            knownItemPrices = cache;
        }

        return cache.TryGetValue(text, out var value) && value > 0 ? value : null;
    }


    private static int? TryReadCurrentCurrencyAmount(IReadOnlyList<TextFragment> fragments)
    {
        if (fragments.Count == 0)
            return null;

        var numeric = fragments
            .Select(x => x with { Text = CleanShopText(x.Text) })
            .Select(x => new
            {
                Fragment = x,
                Value = TryParseShopCurrencyAmount(x.Text),
            })
            .Where(x => x.Value is > 0)
            .Select(x => new
            {
                x.Fragment,
                Value = x.Value!.Value,
            })
            .ToList();

        if (numeric.Count == 0)
            return null;

        var minX = fragments.Min(x => x.X);
        var maxX = fragments.Max(x => x.X);
        var minY = fragments.Min(x => x.Y);
        var width = Math.Max(1f, maxX - minX);

        // The player's spendable amount is rendered in the title/top-right band of shop windows.
        // Some windows show it as "current/cap" (for example Grand Company seals), so parse the
        // first positive number from that top-right text instead of requiring the full text to be
        // only a plain number.
        var topRight = numeric
            .Where(x => x.Fragment.Y <= minY + 90f && x.Fragment.X >= minX + (width * 0.45f))
            .OrderBy(x => x.Fragment.Y)
            .ThenByDescending(x => x.Fragment.X)
            .FirstOrDefault();
        if (topRight is not null)
            return topRight.Value;

        return null;
    }

    private static int? TryParseShopCurrencyAmount(string text)
    {
        text = CleanShopText(text);
        if (TryParsePositiveOrZeroInt(text, out var direct) && direct > 0)
            return direct;

        // Handle balances shown as "10,987/50,000" or "10,987 / 50,000" by taking the
        // current amount before the slash. This is intentionally only for top-right currency
        // scanning; price parsing still uses stricter row/block-specific logic elsewhere.
        var slashIndex = text.IndexOf('/');
        if (slashIndex > 0)
        {
            var leading = text[..slashIndex].Trim();
            if (TryParsePositiveOrZeroInt(leading, out var fromSlash) && fromSlash > 0)
                return fromSlash;
        }

        return null;
    }

    private static bool IsWaitingForRequiredShopSelection(string addonName, IReadOnlyList<TextFragment> fragments)
    {
        if (!string.Equals(addonName, "InclusionShop", StringComparison.Ordinal))
            return false;

        return fragments
            .Select(x => CleanShopText(x.Text))
            .Any(x => x.Contains("Select a subcategory", StringComparison.OrdinalIgnoreCase)
                   || x.Contains("Select a category", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryDetectExchangeUnitName(string addonName, AtkUnitBase* addon)
    {
        if (!addonName.Contains("Exchange", StringComparison.OrdinalIgnoreCase) || addon is null || addon->AtkValues is null || addon->AtkValuesCount == 0)
            return null;

        var count = Math.Clamp((int)addon->AtkValuesCount, 0, 5000);
        for (var i = 0; i < count; i++)
        {
            try
            {
                var value = addon->AtkValues[i];
                switch (value.Type)
                {
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.WideString:
                    {
                        var text = CleanShopText(value.GetValueAsString());
                        if (IsLikelyCurrencyUnitName(text))
                            return text;
                        break;
                    }
                }
            }
            catch
            {
                // Keep scanning.
            }
        }

        return null;
    }

    private static string GetDefaultUnitName(string addonName)
    {
        if (string.Equals(addonName, "Shop", StringComparison.Ordinal))
            return "Gil";

        if (string.Equals(addonName, "GrandCompanyExchange", StringComparison.Ordinal))
            return "Seals";

        if (addonName.Contains("Exchange", StringComparison.OrdinalIgnoreCase))
            return "Currency";

        return string.Empty;
    }

    private static bool IsLikelyCurrencyUnitName(string text)
    {
        text = CleanShopText(text);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("credit")
               || lower.Contains("scrip")
               || lower.Contains("tome")
               || lower.Contains("token")
               || lower.Contains("seal")
               || lower.Contains("mark")
               || lower.Contains("cowrie")
               || lower.Contains("certificate")
               || lower.Contains("voucher")
               || lower.Contains("coin")
               || lower.Contains("nut")
               || lower.Contains("point");
    }


    private static List<ShopItemEntry> TryReadRowsFromAgentShop(string addonName, IReadOnlyList<TextFragment> visibleText)
    {
        var rows = new List<ShopItemEntry>();

        // Standard gil shop data is already stored on AgentShop. Reading this is more
        // reliable than guessing visible text-node positions, and it is not capped to the
        // number of rows currently visible on screen. The UI node walker below remains as a
        // fallback for non-standard shop addons.
        if (!string.Equals(addonName, "Shop", StringComparison.Ordinal))
            return rows;

        var agent = AgentShop.Instance();
        if (agent is null || agent->ItemReceiveCount <= 0 || agent->ItemReceive is null)
            return rows;

        var receiveCount = Math.Min(agent->ItemReceiveCount, 500);
        var costCount = Math.Max(0, Math.Min(agent->ItemCostCount, 500));

        var cleanedVisible = visibleText
            .Select(x => x with { Text = CleanShopText(x.Text) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
        var visibleItems = cleanedVisible
            .Where(x => !TryParsePositiveOrZeroInt(x.Text, out _) && IsLikelyItemName(x.Text))
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .ToList();
        var visibleNumbers = cleanedVisible
            .Where(x => TryParsePositiveOrZeroInt(x.Text, out _))
            .ToList();

        for (var i = 0; i < receiveCount; i++)
        {
            var receive = agent->ItemReceive[i];
            if (receive.ItemId == 0)
                continue;

            var name = GetItemNameFromSheet(receive.ItemId);
            if (string.IsNullOrWhiteSpace(name))
                name = CleanShopText(receive.ItemName.ToString());
            if (string.IsNullOrWhiteSpace(name))
                name = $"Item #{receive.ItemId}";

            var visibleRow = FindVisibleRowForItem(name, rows.Count, visibleItems);

            int? price = null;
            if (visibleRow is not null)
                price = TryFindPriceForRowRelaxed(visibleRow, visibleNumbers);

            if (price is null && i < costCount && agent->ItemCost is not null)
            {
                var cost = agent->ItemCost[i];
                if (cost.ItemCount > 0 && cost.ItemCount <= int.MaxValue)
                    price = (int)cost.ItemCount;
            }

            if (price is null && string.Equals(addonName, "Shop", StringComparison.Ordinal))
                price = TryGetItemSheetPrice(name);

            var canSelectQuantity = visibleRow is not null
                ? HasQuantitySelector(visibleRow, visibleNumbers)
                : TryGetItemStackable(name);

            rows.Add(new ShopItemEntry
            {
                AddonName = addonName,
                UnitName = GetDefaultUnitName(addonName),
                RowIndex = rows.Count,
                Name = name,
                Price = price,
                CanSelectQuantity = canSelectQuantity,
            });
        }

        return rows;
    }


    private static List<ShopItemEntry> TryReadRowsFromAtkLists(string addonName, AtkUnitBase* addon, IReadOnlyList<TextFragment> visibleText)
    {
        var unitName = TryDetectExchangeUnitName(addonName, addon) ?? GetDefaultUnitName(addonName);

        var rendererRows = ExtractRowsFromListRenderers(addonName, addon);
        if (rendererRows.Count > 0)
            return ApplyUnitName(rendererRows, unitName);

        // Do not call AtkComponentList.GetItemLabel as a live scan fallback. It looks like
        // a read-only accessor, but on several native shop lists it can disturb the visible
        // renderer cache/scroll recycling and make the in-game list appear to jump or wrap.
        // Renderer text and AtkValues are passive reads and cover the supported shop types.
        return [];
    }

    private static List<ShopItemEntry> ApplyUnitName(List<ShopItemEntry> rows, string unitName)
    {
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(unitName))
            return rows;

        return rows.Select(row => new ShopItemEntry
        {
            AddonName = row.AddonName,
            UnitName = unitName,
            RowIndex = row.RowIndex,
            Name = row.Name,
            Price = row.Price,
            CanSelectQuantity = row.CanSelectQuantity,
        }).ToList();
    }


    private static List<ShopItemEntry> ExtractRowsFromListRenderers(string addonName, AtkUnitBase* addon)
    {
        var lists = ExtractLists(addon);
        if (lists.Count == 0)
            return [];

        var bestRows = new List<ShopItemEntry>();
        foreach (var listPtr in lists)
        {
            var list = (AtkComponentList*)listPtr;
            var fragments = ExtractRendererFragments(list);
            if (fragments.Count == 0)
                continue;

            var grouped = fragments
                .GroupBy(x => x.RowIndex)
                .OrderBy(x => x.Key)
                .ToList();

            var rows = new List<ShopItemEntry>();
            foreach (var group in grouped)
            {
                var parts = group
                    .Select(x => x with { Text = CleanShopText(x.Text) })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                    .ToList();

                var item = parts
                    .Where(x => !TryParsePositiveOrZeroInt(x.Text, out _) && IsLikelyItemName(x.Text))
                    .OrderBy(x => x.X)
                    .ThenBy(x => x.Y)
                    .FirstOrDefault();

                if (item is null)
                    continue;

                var numeric = parts
                    .Where(x => TryParsePositiveOrZeroInt(x.Text, out _))
                    .Select(x => new TextFragment(x.Text, x.X, x.Y, x.IsNumeric))
                    .ToList();

                var itemFragment = new TextFragment(item.Text, item.X, item.Y, false);
                var price = TryFindPriceForRow(itemFragment, numeric);
                if (price is null && string.Equals(addonName, "Shop", StringComparison.Ordinal))
                    price = TryGetItemSheetPrice(item.Text);
                if (!string.Equals(addonName, "Shop", StringComparison.Ordinal) && price is null && !IsKnownItemName(item.Text))
                    continue;

                rows.Add(new ShopItemEntry
                {
                    AddonName = addonName,
                    UnitName = GetDefaultUnitName(addonName),
                    RowIndex = group.Key,
                    Name = item.Text,
                    Price = price,
                    CanSelectQuantity = HasQuantitySelector(itemFragment, numeric),
                });
            }

            rows = rows
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.RowIndex)
                .ToList();

            if (rows.Count > bestRows.Count)
                bestRows = rows;
        }

        return bestRows;
    }

    private static List<nint> ExtractLists(AtkUnitBase* addon)
    {
        var result = new List<nint>();
        var visited = new HashSet<nint>();
        var manager = addon->UldManager;
        if (manager.NodeListCount <= 0 || manager.NodeList is null)
            return result;

        for (var i = 0; i < manager.NodeListCount; i++)
            WalkLists(manager.NodeList[i], result, visited, 0);

        return result;
    }

    private static void WalkLists(AtkResNode* node, List<nint> result, HashSet<nint> visited, int depth)
    {
        if (node is null || depth > 48 || !visited.Add((nint)node))
            return;

        if (node->Type == NodeType.Component)
        {
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component is not null)
            {
                try
                {
                    if (componentNode->Component->GetComponentType() == ComponentType.List)
                    {
                        var list = (AtkComponentList*)componentNode->Component;
                        if (list->ListLength > 0 || list->GetItemCount() > 0 || list->AllocatedItemRendererListLength > 0)
                            result.Add((nint)list);
                    }
                }
                catch (Exception ex)
                {
                    DalamudServices.Log.Verbose(ex, "Failed to inspect component list.");
                }

                var manager = componentNode->Component->UldManager;
                if (manager.NodeListCount > 0 && manager.NodeList is not null)
                {
                    for (var i = 0; i < manager.NodeListCount; i++)
                        WalkLists(manager.NodeList[i], result, visited, depth + 1);
                }
            }
        }

        if (node->ChildNode != null)
            WalkLists(node->ChildNode, result, visited, depth + 1);

        if (node->NextSiblingNode != null)
            WalkLists(node->NextSiblingNode, result, visited, depth);
    }

    private static List<RendererFragment> ExtractRendererFragments(AtkComponentList* list)
    {
        var result = new List<RendererFragment>();
        if (list is null || list->ItemRendererList is null || list->AllocatedItemRendererListLength <= 0)
            return result;

        var rendererCount = Math.Clamp(list->AllocatedItemRendererListLength, 0, 256);
        for (var i = 0; i < rendererCount; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer is null)
                continue;

            var rowIndex = renderer->ListItemIndex;
            if (rowIndex < 0)
                rowIndex = list->FirstVisibleItemIndex + i;

            var nodeCount = (int)renderer->RowTemplateNodeCountByte;
            if (nodeCount <= 0)
                continue;

            var visited = new HashSet<nint>();
            if (nodeCount == 1 && renderer->RowTemplateNode is not null)
            {
                WalkRendererNode(renderer->RowTemplateNode, rowIndex, result, visited, 0);
            }
            else if (renderer->RowTemplateNodeList is not null)
            {
                nodeCount = Math.Clamp(nodeCount, 0, 128);
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
                    WalkRendererNode(renderer->RowTemplateNodeList[nodeIndex], rowIndex, result, visited, 0);
            }
        }

        return result;
    }

    private static void WalkRendererNode(AtkResNode* node, int rowIndex, List<RendererFragment> result, HashSet<nint> visited, int depth)
    {
        if (node is null || depth > 32 || !visited.Add((nint)node))
            return;

        if (node->Type == NodeType.Text)
        {
            var text = CleanShopText(((AtkTextNode*)node)->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new RendererFragment(text, rowIndex, node->ScreenX, node->ScreenY, false));
        }
        else if (node->Type == NodeType.Counter)
        {
            var text = CleanShopText(((AtkCounterNode*)node)->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new RendererFragment(text, rowIndex, node->ScreenX, node->ScreenY, true));
        }
        else if (node->Type == NodeType.Component)
        {
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component is not null)
            {
                var manager = componentNode->Component->UldManager;
                if (manager.NodeListCount > 0 && manager.NodeList is not null)
                {
                    for (var i = 0; i < manager.NodeListCount; i++)
                        WalkRendererNode(manager.NodeList[i], rowIndex, result, visited, depth + 1);
                }
            }
        }

        if (node->ChildNode != null)
            WalkRendererNode(node->ChildNode, rowIndex, result, visited, depth + 1);

        if (node->NextSiblingNode != null)
            WalkRendererNode(node->NextSiblingNode, rowIndex, result, visited, depth);
    }

    private static List<ListLabel> ExtractListLabels(AtkUnitBase* addon)
    {
        var result = new List<ListLabel>();
        var visited = new HashSet<nint>();
        var manager = addon->UldManager;
        var count = manager.NodeListCount;
        if (count <= 0 || manager.NodeList is null)
            return result;

        for (var i = 0; i < count; i++)
            WalkListLabels(manager.NodeList[i], result, visited, 0);

        return result;
    }

    private static void WalkListLabels(AtkResNode* node, List<ListLabel> result, HashSet<nint> visited, int depth)
    {
        if (node is null || depth > 48 || !visited.Add((nint)node))
            return;

        if (node->Type == NodeType.Component)
        {
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component is not null)
            {
                try
                {
                    if (componentNode->Component->GetComponentType() == ComponentType.List)
                    {
                        var list = (AtkComponentList*)componentNode->Component;
                        var count = list->GetItemCount();
                        if (count <= 0)
                            count = list->ListLength;

                        count = Math.Clamp(count, 0, 5000);
                        for (var i = 0; i < count; i++)
                        {
                            var label = CleanShopText(list->GetItemLabel(i).ToString());
                            if (!string.IsNullOrWhiteSpace(label))
                            {
                                result.Add(new ListLabel(
                                    label,
                                    i,
                                    (nint)list,
                                    list->ListLength,
                                    node->ScreenX,
                                    node->ScreenY + ((i - list->FirstVisibleItemIndex) * Math.Max(1, (int)list->ItemHeight))));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DalamudServices.Log.Verbose(ex, "Failed to read AtkComponentList labels.");
                }

                var manager = componentNode->Component->UldManager;
                if (manager.NodeListCount > 0 && manager.NodeList is not null)
                {
                    for (var i = 0; i < manager.NodeListCount; i++)
                        WalkListLabels(manager.NodeList[i], result, visited, depth + 1);
                }
            }
        }

        if (node->ChildNode != null)
            WalkListLabels(node->ChildNode, result, visited, depth + 1);

        if (node->NextSiblingNode != null)
            WalkListLabels(node->NextSiblingNode, result, visited, depth);
    }

    private static List<TextFragment> ExtractVisibleText(AtkUnitBase* addon)
    {
        var result = new List<TextFragment>();
        var visited = new HashSet<nint>();
        var manager = addon->UldManager;
        var count = manager.NodeListCount;
        if (count <= 0 || manager.NodeList is null)
            return result;

        for (var i = 0; i < count; i++)
            WalkNode(manager.NodeList[i], result, visited, 0);

        return result;
    }

    private static void WalkNode(AtkResNode* node, List<TextFragment> result, HashSet<nint> visited, int depth)
    {
        if (node is null || depth > 48 || !visited.Add((nint)node))
            return;

        // Do not prune the whole subtree on IsVisible(). Disabled/greyed shop rows and some
        // list renderer children can report invisible while still being drawn by their parent.
        // Filtering happens later by row-like text and addon/list context.
        if (node->Type == NodeType.Text)
        {
            var text = CleanShopText(((AtkTextNode*)node)->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new TextFragment(text, node->ScreenX, node->ScreenY, false));
        }
        else if (node->Type == NodeType.Counter)
        {
            // FFXIV uses counter nodes for a lot of numeric UI values, including many shop
            // quantity/price/bag cells. Earlier builds only read AtkTextNode, so they saw
            // item names but missed the price numbers.
            var text = CleanShopText(((AtkCounterNode*)node)->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new TextFragment(text, node->ScreenX, node->ScreenY, true));
        }
        else if (node->Type == NodeType.Component)
        {
            // The visible rows in standard FFXIV shop lists live inside component nodes.
            // Walking only AtkResNode.ChildNode/NextSiblingNode misses those inner UldManagers,
            // which is why earlier builds could see the Shop addon but not its item rows.
            WalkComponentNode((AtkComponentNode*)node, result, visited, depth + 1);
        }

        if (node->ChildNode != null)
            WalkNode(node->ChildNode, result, visited, depth + 1);

        if (node->NextSiblingNode != null)
            WalkNode(node->NextSiblingNode, result, visited, depth);
    }

    private static void WalkComponentNode(AtkComponentNode* componentNode, List<TextFragment> result, HashSet<nint> visited, int depth)
    {
        if (componentNode is null || componentNode->Component is null || depth > 48)
            return;

        var manager = componentNode->Component->UldManager;
        var count = manager.NodeListCount;
        if (count <= 0 || manager.NodeList is null)
            return;

        for (var i = 0; i < count; i++)
            WalkNode(manager.NodeList[i], result, visited, depth + 1);
    }

    private static List<ShopItemEntry> BuildShopRows(string addonName, IReadOnlyList<TextFragment> fragments)
    {
        var rows = new List<ShopItemEntry>();

        // Keep the UI traversal order for callback row indexes, but clean every string before matching.
        var candidates = fragments
            .Select(x => x with { Text = CleanShopText(x.Text) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();

        // Do not use the node type to decide whether a row is an item. Some standard
        // shop lists expose their visible strings through component/counter-flavoured nodes
        // depending on the current UI layout. The previous build discarded anything marked
        // as a counter node before checking the actual text, which is why it could count the
        // 8 visible shop rows but still reject all 8 as non-items.
        var numericFragments = candidates
            .Where(x => TryParsePositiveOrZeroInt(x.Text, out _))
            .ToList();

        var itemFragments = candidates
            .Where(x => !TryParsePositiveOrZeroInt(x.Text, out _) && IsLikelyItemName(x.Text))
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .ToList();

        for (var row = 0; row < itemFragments.Count; row++)
        {
            var item = itemFragments[row];
            var name = item.Text.Trim();

            if (rows.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var price = TryFindPriceForRow(item, numericFragments);
            if (price is null && string.Equals(addonName, "Shop", StringComparison.Ordinal))
                price = TryGetItemSheetPrice(name);

            // Exchange/shop-category addons can contain dropdown/filter entries such as
            // "Paladin / Gladiator". Those do not have a same-row shop price and are not
            // normal Item sheet names, so do not expose them as purchasable rows.
            if (!string.Equals(addonName, "Shop", StringComparison.Ordinal) && price is null && !IsKnownItemName(name))
                continue;

            rows.Add(new ShopItemEntry
            {
                AddonName = addonName,
                UnitName = GetDefaultUnitName(addonName),
                RowIndex = rows.Count,
                Name = name,
                Price = price,
                CanSelectQuantity = HasQuantitySelector(item, numericFragments),
            });
        }

        // Last-resort fallback: if the addon only exposed row names and no usable numeric
        // columns, still show those names rather than showing an empty plugin window. This
        // keeps the reader generic for any shop length; it does not assume 8 rows, Dark Matter,
        // or any specific vendor contents.
        if (rows.Count == 0 && string.Equals(addonName, "Shop", StringComparison.Ordinal))
        {
            var fallbackNames = candidates
                .Where(x => !TryParsePositiveOrZeroInt(x.Text, out _))
                .Select(x => x.Text.Trim())
                .Where(IsLikelyItemName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => candidates.First(c => string.Equals(c.Text.Trim(), x, StringComparison.OrdinalIgnoreCase)).Y)
                .ToList();

            foreach (var name in fallbackNames)
            {
                rows.Add(new ShopItemEntry
                {
                    AddonName = addonName,
                    UnitName = GetDefaultUnitName(addonName),
                    RowIndex = rows.Count,
                    Name = name,
                    Price = string.Equals(addonName, "Shop", StringComparison.Ordinal) ? TryGetItemSheetPrice(name) : null,
                    CanSelectQuantity = true,
                });
            }
        }

        return rows;
    }


    private static TextFragment? FindVisibleRowForItem(string itemName, int rowIndex, IReadOnlyList<TextFragment> visibleItems)
    {
        var cleanName = CleanShopText(itemName);
        if (string.IsNullOrWhiteSpace(cleanName) || visibleItems.Count == 0)
            return null;

        var exact = visibleItems.FirstOrDefault(x => string.Equals(x.Text, cleanName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        // The game may truncate long item names with an ellipsis in the visible shop row.
        // Use a conservative prefix match only after exact matching fails.
        var prefix = visibleItems.FirstOrDefault(x =>
            x.Text.EndsWith("...", StringComparison.Ordinal)
            && cleanName.StartsWith(x.Text.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
        if (prefix is not null)
            return prefix;

        return rowIndex >= 0 && rowIndex < visibleItems.Count ? visibleItems[rowIndex] : null;
    }

    private static bool HasVisibleQuantitySelector(string itemName, IReadOnlyList<TextFragment> visibleText)
    {
        var cleaned = visibleText
            .Select(x => x with { Text = CleanShopText(x.Text) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
        var item = cleaned.FirstOrDefault(x => string.Equals(x.Text, itemName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return false;

        var numbers = cleaned.Where(x => TryParsePositiveOrZeroInt(x.Text, out _)).ToList();
        return HasQuantitySelector(item, numbers);
    }

    private static bool HasQuantitySelector(TextFragment item, IReadOnlyList<TextFragment> numericFragments)
    {
        var sameRowNumbers = numericFragments
            .Where(x => Math.Abs(x.Y - item.Y) <= 10f && x.X > item.X + 20f)
            .OrderBy(x => x.X)
            .Select(x => TryParsePositiveOrZeroInt(x.Text, out var value) ? value : (int?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        // Rows with a quantity selector usually expose Quantity + Price + Owned/Bag.
        // Rows without a quantity selector generally expose only Price + Owned/Bag and must
        // be bought one click at a time.
        return sameRowNumbers.Count >= 3 && sameRowNumbers[0] <= 99;
    }

    private static int? TryFindPriceForRow(TextFragment item, IReadOnlyList<TextFragment> numericFragments)
    {
        var sameRowNumbers = numericFragments
            .Where(x => Math.Abs(x.Y - item.Y) <= 8f && x.X > item.X + 20f)
            .OrderBy(x => x.X)
            .Select(x => TryParsePositiveOrZeroInt(x.Text, out var value) ? value : (int?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        return PickLikelyPrice(sameRowNumbers);
    }

    private static int? TryFindPriceForRowRelaxed(TextFragment item, IReadOnlyList<TextFragment> numericFragments)
    {
        var sameRowNumbers = numericFragments
            .Where(x => Math.Abs(x.Y - item.Y) <= 14f && x.X > item.X + 20f)
            .OrderBy(x => x.X)
            .Select(x => TryParsePositiveOrZeroInt(x.Text, out var value) ? value : (int?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        return PickLikelyPrice(sameRowNumbers);
    }

    private static int? PickLikelyPrice(IReadOnlyList<int> sameRowNumbers)
    {
        if (sameRowNumbers.Count == 0)
            return null;

        // Typical shop row order is Quantity, Price, Bag. If there are three or more values,
        // the middle positive number is usually the price. Rows without a quantity selector
        // usually expose Price + Bag, so the first positive value is the price.
        if (sameRowNumbers.Count >= 3)
        {
            var middle = sameRowNumbers.Skip(1).FirstOrDefault(x => x > 0);
            if (middle > 0)
                return middle;
        }

        if (sameRowNumbers.Count >= 2 && sameRowNumbers[0] == 1)
            return sameRowNumbers.Skip(1).FirstOrDefault(x => x > 0);

        return sameRowNumbers.FirstOrDefault(x => x > 0);
    }

    private static bool TryParsePositiveOrZeroInt(string text, out int value)
    {
        return int.TryParse(CleanShopText(text).Replace(",", string.Empty), out value) && value >= 0;
    }

    private static string CleanShopText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        var lookedLikeItemLink = cleaned.Contains('&')
                                 || cleaned.StartsWith("H=", StringComparison.Ordinal)
                                 || cleaned.Contains("%I=", StringComparison.Ordinal)
                                 || (cleaned.StartsWith("H", StringComparison.Ordinal) && cleaned.Contains('('));

        // Raw SeString item links from AtkComponentList labels may arrive as strings like:
        //   H=%I=&Grade 1 Dark Matter|H
        //   H=%I=&Grade 1 Dark MatterIH
        // or with an unprintable byte between I and H. Take the visible text after the last
        // ampersand first, then strip link terminators after control/private glyph cleanup.
        if (cleaned.Contains('&'))
        {
            var ampIndex = cleaned.LastIndexOf('&');
            if (ampIndex >= 0 && ampIndex + 1 < cleaned.Length)
                cleaned = cleaned[(ampIndex + 1)..].Trim();
        }

        // Some Grand Company exchange labels arrive as H<control bytes>I<control bytes>(Item NameIH
        // rather than the more common H=%I=&Item Name|H form. Strip the link prefix by taking
        // the text after the last opening parenthesis before control/private glyph cleanup.
        if (lookedLikeItemLink && cleaned.StartsWith("H", StringComparison.Ordinal) && cleaned.Contains('('))
        {
            var parenIndex = cleaned.LastIndexOf('(');
            if (parenIndex >= 0 && parenIndex + 1 < cleaned.Length)
                cleaned = cleaned[(parenIndex + 1)..].Trim();
        }

        cleaned = Regex.Replace(cleaned, @"^H=[^&]*&", string.Empty).Trim();

        cleaned = new string(cleaned
            .Where(ch => !char.IsControl(ch)
                         && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.PrivateUse
                         && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Surrogate
                         && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.OtherNotAssigned)
            .ToArray()).Trim();

        if (lookedLikeItemLink)
        {
            // Remove link terminators after control stripping. The terminator has been seen as
            // |H, IH, a lone trailing H, and occasionally repeated after cleanup.
            cleaned = Regex.Replace(cleaned, @"(?:\|?I?H)+$", string.Empty).Trim();
            cleaned = Regex.Replace(cleaned, @"^H=[^&]*&", string.Empty).Trim();
        }

        cleaned = Regex.Replace(cleaned, @"^[^\p{L}\p{N}'""“”‘’\-]+", string.Empty).Trim();

        return cleaned;
    }

    private static bool IsLikelyItemName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        if (value.Length < 2 || value.Length > 80)
            return false;

        if (Regex.IsMatch(value, @"^[\d,]+$"))
            return false;

        // Exclude shop chrome, headers, tabs, and hidden buyback explanation text while
        // still allowing normal item names with numbers or punctuation.
        var blockedExact = new[]
        {
            "Buy", "Sell", "Shop", "Quantity", "Owned", "Required", "Cancel", "Close", "Total", "Gil",
            "In Inventory", "Possessed", "Currency", "Exchange", "Item", "Items", "Price", "Bag",
            "Current Stock", "Buyback", "Show only recently added items.", "Set", "Qty", "Qty.",
            "Show All", "Weapons", "Armor", "Accessories", "Others",
        };

        if (blockedExact.Any(x => string.Equals(value, x, StringComparison.OrdinalIgnoreCase)))
            return false;

        var blockedContains = new[]
        {
            "The list will be cleared",
            "Once an item is removed",
            "The Buyback list contains",
            "cannot be bought back",
            "changing areas",
        };

        if (blockedContains.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private static int GetCallbackAttemptToTry(string addonName, int attemptIndex)
    {
        if (!PreferredCallbackAttempts.TryGetValue(addonName, out var preferredAttempt))
            return attemptIndex;

        preferredAttempt = Math.Clamp(preferredAttempt, 0, 6);
        if (attemptIndex == 0)
            return preferredAttempt;

        var candidate = attemptIndex - 1;
        return candidate >= preferredAttempt ? candidate + 1 : candidate;
    }

    private bool TryClickYesNo()
    {
        var ptr = DalamudServices.GameGui.GetAddonByName("SelectYesno");
        if (ptr.IsNull)
            return false;

        var addon = (AtkUnitBase*)ptr.Address;
        if (addon is null || !addon->IsVisible)
            return false;

        FireAddonCallback(addon, ConfirmYesCallback);
        return true;
    }

    private bool TryConfirmQuantity(int quantity)
    {
        foreach (var addonName in new[] { "InputNumeric", "InputString" })
        {
            var ptr = DalamudServices.GameGui.GetAddonByName(addonName);
            if (ptr.IsNull)
                continue;

            var addon = (AtkUnitBase*)ptr.Address;
            if (addon is null || !addon->IsVisible)
                continue;

            FireAddonCallback(addon, QuantityOkCallback, quantity);
            return true;
        }

        return false;
    }

    private static void SelectShopRow(string addonName, AtkUnitBase* addon, int rowIndex, int quantity, int callbackAttempt)
    {
        var clampedQuantity = Math.Clamp(quantity, 1, MaxSinglePurchase);
        DalamudServices.Log.Verbose("ShopHelper selecting {AddonName} row {RowIndex} with quantity {Quantity}; callback attempt {Attempt}.", addonName, rowIndex, clampedQuantity, callbackAttempt);

        if (string.Equals(addonName, "ShopExchangeCurrency", StringComparison.Ordinal)
            || string.Equals(addonName, "ShopExchangeItem", StringComparison.Ordinal)
            || string.Equals(addonName, "GrandCompanyExchange", StringComparison.Ordinal))
        {
            // Different exchange/special-shop addons use different callback payloads across
            // categories and patches. Cycle conservative known shapes until the game opens
            // its normal yes/no confirmation; completion is only counted after that confirm.
            // This avoids a dead queue when a specific exchange vendor rejects one shape.
            switch (callbackAttempt % 7)
            {
                case 0:
                    FireAddonCallback(addon, 0, rowIndex, clampedQuantity);
                    return;
                case 1:
                    FireAddonCallback(addon, rowIndex, clampedQuantity);
                    return;
                case 2:
                    FireAddonCallback(addon, 1, rowIndex, clampedQuantity);
                    return;
                case 3:
                    FireAddonCallback(addon, 2, rowIndex, clampedQuantity);
                    return;
                case 4:
                    FireAddonCallback(addon, 3, rowIndex, clampedQuantity);
                    return;
                case 5:
                    FireAddonCallback(addon, 3, 0, 3, rowIndex, 3, clampedQuantity, 0, 0);
                    return;
                default:
                    FireAddonCallback(addon, 0, rowIndex);
                    return;
            }
        }

        // Normal gil shops use the simple row + quantity callback.
        FireAddonCallback(addon, ShopSelectCallback, rowIndex, clampedQuantity);
    }

    private static void FireAddonCallback(AtkUnitBase* addon, params int[] values)
    {
        if (addon is null || values.Length == 0)
            return;

        if (values.Length == 1)
        {
            addon->FireCallbackInt(values[0]);
            return;
        }

        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i] = new AtkValue();
            atkValues[i].SetInt(values[i]);
        }

        addon->FireCallback((uint)values.Length, atkValues);
    }


    private sealed record TextFragment(string Text, float X, float Y, bool IsNumeric);
    private sealed record RendererFragment(string Text, int RowIndex, float X, float Y, bool IsNumeric);
    private sealed record ListLabel(string Text, int Index, nint ListAddress, int ListLength, float X, float Y);
    private sealed record AddonValueFragment(int Index, string Text, int? Number);

    public string BuildDebugDump()
    {
        try
        {
            if (!TryGetOpenShopAddon(out var addon, out var addonName))
                return "No supported shop addon is currently open.";

            var text = ExtractVisibleText(addon);
            var lists = ExtractLists(addon);
            // Keep debug passive too: do not call AtkComponentList.GetItemLabel here.
            var labels = new List<ListLabel>();
            var rows = cachedItems.Count > 0 ? cachedItems : ScanVisibleItems();
            var values = ExtractAddonValuesForDebug(addon);
            var rendererLines = new List<string>();
            foreach (var listPtr in lists.Take(8))
            {
                var list = (AtkComponentList*)listPtr;
                var fragments = ExtractRendererFragments(list);
                var grouped = fragments
                    .GroupBy(x => x.RowIndex)
                    .OrderBy(x => x.Key)
                    .Take(80);
                foreach (var group in grouped)
                {
                    var parts = group
                        .OrderBy(x => x.X)
                        .Select(x => $"{x.Text}@{x.X:0},{x.Y:0}{(x.IsNumeric ? "#" : string.Empty)}");
                    rendererLines.Add($"list=0x{(nuint)listPtr:X} row={group.Key}: {string.Join(" | ", parts)}");
                }
            }

            var lines = new List<string>
            {
                $"ShopHelper debug dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Addon: {addonName}",
                $"Currency amount: {(currentCurrencyAmount.HasValue ? currentCurrencyAmount.Value.ToString("N0") : "<unread>")}",
                $"Parsed rows: {rows.Count}",
            };

            lines.Add("-- Parsed rows --");
            foreach (var row in rows.Take(300))
                lines.Add($"row={row.RowIndex} name=\"{row.Name}\" unit=\"{row.UnitName}\" price={(row.Price.HasValue ? row.Price.Value.ToString() : "<null>")} qty={row.CanSelectQuantity}");

            lines.Add($"-- Visible text/counter nodes ({text.Count}) --");
            foreach (var fragment in text.Take(500))
                lines.Add($"{(fragment.IsNumeric ? "NUM" : "TXT")} x={fragment.X:0} y={fragment.Y:0}: \"{fragment.Text}\"");

            lines.Add($"-- Component list labels ({labels.Count}) --");
            foreach (var label in labels.Take(500))
                lines.Add($"list=0x{(nuint)label.ListAddress:X} idx={label.Index}/{label.ListLength} x={label.X:0} y={label.Y:0}: \"{label.Text}\"");

            lines.Add($"-- Renderer rows ({rendererLines.Count}) --");
            lines.AddRange(rendererLines.Take(500));

            lines.Add($"-- AtkValues ({values.Count}) --");
            lines.AddRange(values.Take(800));

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Failed to build debug dump: {ex}";
        }
    }

    public void WriteDebugDumpToXlLog()
    {
        var dump = BuildDebugDump();
        const int chunkSize = 3500;
        for (var i = 0; i < dump.Length; i += chunkSize)
        {
            var chunk = dump.Substring(i, Math.Min(chunkSize, dump.Length - i));
            DalamudServices.Log.Information("ShopHelper debug dump chunk {Chunk}:\n{Dump}", (i / chunkSize) + 1, chunk);
        }
    }

    private static List<string> ExtractAddonValuesForDebug(AtkUnitBase* addon)
    {
        var result = new List<string>();
        if (addon is null || addon->AtkValues is null || addon->AtkValuesCount == 0)
            return result;

        var count = Math.Clamp((int)addon->AtkValuesCount, 0, 5000);
        for (var i = 0; i < count; i++)
        {
            try
            {
                var value = addon->AtkValues[i];
                var type = value.Type.ToString();
                string display;
                switch (value.Type)
                {
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString:
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.WideString:
                        display = CleanShopText(value.GetValueAsString());
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int:
                        display = value.Int.ToString();
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt:
                        display = value.UInt.ToString();
                        break;
                    default:
                        display = "<not decoded>";
                        break;
                }

                result.Add($"[{i}] {type}: \"{display}\"");
            }
            catch (Exception ex)
            {
                result.Add($"[{i}] <read failed>: {ex.Message}");
            }
        }

        return result;
    }

    public void Dispose()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
    }

    private sealed class PendingPurchase(string addonName, int rowIndex, string itemName, int total, bool usesQuantityDialog)
    {
        public string AddonName { get; } = addonName;
        public int RowIndex { get; } = rowIndex;
        public string ItemName { get; } = itemName;
        public int Total { get; } = total;
        public bool UsesQuantityDialog { get; } = usesQuantityDialog;
        public int Completed { get; set; }
        public int LastRequestedQuantity { get; set; }
        public int CallbackAttempt { get; set; }
        public int? LastCallbackAttemptUsed { get; set; }
        public bool WaitingForQuantityDialog { get; set; }
        public bool WaitingForYesNo { get; set; }
        public DateTime? WaitStartedUtc { get; set; }
        public int Remaining => Total - Completed;
    }
}
