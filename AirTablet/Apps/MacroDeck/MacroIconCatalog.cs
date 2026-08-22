using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Excel.Sheets;
using System.Text.RegularExpressions;

namespace MacroDeck;

internal sealed record MacroIconEntry(int IconId, string Name);
internal sealed record MacroIconCategory(string Id, string Label, IReadOnlyList<MacroIconEntry> Icons);

internal sealed class MacroIconCatalog
{
    private static readonly Regex LeadingPerformanceNumber = new(@"^\s*\d+\s*[:.)-]?\s*", RegexOptions.Compiled);
    private readonly Dictionary<int, ISharedImmediateTexture> textures = [];
    private readonly List<MacroIconCategory> categories = [];

    public IReadOnlyList<MacroIconCategory> Categories => categories;
    public int DefaultIconId => categories.FirstOrDefault()?.Icons.FirstOrDefault()?.IconId ?? 0;

    public MacroIconCatalog()
    {
        AddCategory("macro", "Macro Icons", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<MacroIcon>().OrderBy(row => row.RowId).Select(row => new MacroIconEntry(row.Icon, string.Empty)));
        AddCategory("emotes", "Emotes", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<Emote>()
            .Where(row => row.Name.ToString().Length > 0 && !row.Name.ToString().Trim().Equals("Sleep", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Order)
            .ThenBy(row => row.RowId)
            .Select(row => new MacroIconEntry((int)row.Icon, GetEmoteName(row))));
        AddCategory("actions", "Actions", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<Lumina.Excel.Sheets.Action>().Where(row => row.Name.ToString().Length > 0).OrderBy(row => row.RowId).Select(row => new MacroIconEntry((int)row.Icon, row.Name.ToString())));
        AddCategory("general", "General Actions", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<GeneralAction>().Where(row => row.Name.ToString().Length > 0).OrderBy(row => row.UIPriority).ThenBy(row => row.RowId).Select(row => new MacroIconEntry(row.Icon, row.Name.ToString())));
        AddCategory("traits", "Traits", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<Trait>().Where(row => row.Name.ToString().Length > 0).OrderBy(row => row.RowId).Select(row => new MacroIconEntry(row.Icon, row.Name.ToString())));
        AddCategory("performance", "Performance", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<Perform>().Where(row => row.Name.ToString().Length > 0 || row.Instrument.ToString().Length > 0).OrderBy(row => row.RowId).Select(row => new MacroIconEntry(row.Icon, GetPerformanceName(row))));
        AddCategory("menu", "Menu Icons", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<MainCommand>().Where(row => row.Name.ToString().Length > 0).OrderBy(row => row.SortID).ThenBy(row => row.RowId).Select(row => new MacroIconEntry(row.Icon, row.Name.ToString())));
        AddCategory("crafting", "Crafting", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<CraftAction>().Where(row => row.Name.ToString().Length > 0).OrderBy(row => row.RowId).Select(row => new MacroIconEntry((int)row.Icon, row.Name.ToString())));
        AddCategory("companions", "Pet & Buddy", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<PetAction>().Where(row => row.Name.ToString().Length > 0).Select(row => new MacroIconEntry(row.Icon, row.Name.ToString()))
            .Concat(AirTablet.DalamudServices.DataManager.GetExcelSheet<BuddyAction>().Where(row => row.Name.ToString().Length > 0).Select(row => new MacroIconEntry(row.Icon, row.Name.ToString()))));
        AddCategory("mounts", "Mounts", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<Mount>().Where(row => row.Singular.ToString().Length > 0).OrderBy(row => row.Order).ThenBy(row => row.RowId).Select(row => new MacroIconEntry((int)row.Icon, row.Singular.ToString())));
        AddCategory("minions", "Minions", () => AirTablet.DalamudServices.DataManager
            .GetExcelSheet<Companion>().Where(row => row.Singular.ToString().Length > 0).OrderBy(row => row.Order).ThenBy(row => row.RowId).Select(row => new MacroIconEntry((int)row.Icon, row.Singular.ToString())));
    }

    private void AddCategory(string id, string label, Func<IEnumerable<MacroIconEntry>> load)
    {
        try
        {
            var icons = load()
                .Where(icon => icon.IconId > 0)
                .GroupBy(icon => icon.IconId)
                .Select(group => group.First())
                .ToList();
            if (icons.Count > 0)
                categories.Add(new MacroIconCategory(id, label, icons));
        }
        catch (Exception ex)
        {
            AirTablet.DalamudServices.Log.Warning(ex, "MacroDeck could not load the {Category} FFXIV icon catalog.", label);
        }
    }

    private static string GetPerformanceName(Perform row)
    {
        var name = row.Instrument.ToString().Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = row.Name.ToString().Trim();
        name = LeadingPerformanceNumber.Replace(name, string.Empty).Trim();
        return name.Equals("BD", StringComparison.OrdinalIgnoreCase)
            ? "Bass Drum"
            : name;
    }

    private static string GetEmoteName(Emote row)
    {
        var name = row.Name.ToString().Trim();
        return name.Equals("Snowball", StringComparison.OrdinalIgnoreCase)
            ? "Throw"
            : name;
    }

    public IDalamudTextureWrap? GetTexture(int iconId)
    {
        if (iconId <= 0)
            return null;

        try
        {
            if (!textures.TryGetValue(iconId, out var shared))
            {
                shared = AirTablet.DalamudServices.TextureProvider.GetFromGameIcon(
                    new GameIconLookup((uint)iconId));
                textures[iconId] = shared;
            }

            var texture = shared.GetWrapOrEmpty();
            return texture.Width > 1 && texture.Height > 1 ? texture : null;
        }
        catch (Exception ex)
        {
            AirTablet.DalamudServices.Log.Debug(ex, "MacroDeck could not load game icon {IconId}.", iconId);
            return null;
        }
    }
}
