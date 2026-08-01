using System.Globalization;
using System.Reflection;
using AutoGreet.Models;

namespace AutoGreet.Services;

internal static class HousingLocationFormatter
{
    private static readonly Dictionary<uint, string> TerritoryNameCache = new();

    private static readonly Dictionary<uint, string> KnownHousingTerritories = new()
    {
        // Residential district maps. The Lumina lookup below is preferred, but these fallback
        // names keep the UI readable even if the sheet lookup fails during early login/loading.
        [339] = "Mist",
        [340] = "The Lavender Beds",
        [341] = "The Goblet",
        [641] = "Shirogane",
        [979] = "Empyreum",
    };

    public static string GetRegionDisplayName(CustomDetectionRegion region)
        => $"{region.Name}  ({GetTerritoryDisplayName(region.TerritoryType)})";

    public static string GetTerritoryDisplayName(uint territoryType)
    {
        if (territoryType == 0)
            return "Unknown location";

        if (TerritoryNameCache.TryGetValue(territoryType, out var cached))
            return cached;

        var knownHousingName = GetKnownHousingDistrictFromTerritory(territoryType);
        if (!string.IsNullOrWhiteSpace(knownHousingName))
            return TerritoryNameCache[territoryType] = knownHousingName;

        var sheetName = TryGetTerritoryPlaceName(territoryType);
        return TerritoryNameCache[territoryType] = string.IsNullOrWhiteSpace(sheetName)
            ? $"Territory {territoryType}"
            : $"{sheetName} ({territoryType})";
    }

    public static string NormalizeHousingDistrictName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var value = raw.Trim();
        var comparable = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (string.IsNullOrWhiteSpace(comparable)
            || comparable is "none" or "unknown" or "indoor" or "outdoor" or "workshop")
            return string.Empty;

        if (comparable.Contains("lavender")) return "The Lavender Beds";
        if (comparable.Contains("goblet")) return "The Goblet";
        if (comparable.Contains("mist")) return "Mist";
        if (comparable.Contains("shirogane")) return "Shirogane";
        if (comparable.Contains("empyreum")) return "Empyreum";

        return value;
    }

    public static string GetKnownHousingDistrictFromTerritory(uint territoryType)
    {
        if (territoryType == 0)
            return string.Empty;

        if (KnownHousingTerritories.TryGetValue(territoryType, out var name))
            return name;

        var territoryName = TryGetTerritoryPlaceName(territoryType);
        var normalized = NormalizeHousingDistrictName(territoryName);
        return normalized is "Mist" or "The Lavender Beds" or "The Goblet" or "Shirogane" or "Empyreum"
            ? normalized
            : string.Empty;
    }

    private static string? TryGetTerritoryPlaceName(uint territoryType)
    {
        if (territoryType == 0)
            return null;

        try
        {
            var territoryTypeRow = FindLoadedType("Lumina.Excel.Sheets.TerritoryType");
            if (territoryTypeRow is null) return null;

            var sheet = GetExcelSheet(territoryTypeRow);
            if (sheet is null) return null;

            var row = GetExcelRow(sheet, territoryType);
            if (row is null) return null;

            var placeNameRef = GetMemberValue(row, "PlaceName");
            var placeName = ResolveRowRef(placeNameRef);
            var name = GetMemberValue(placeName, "Name")?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name;

            return GetMemberValue(row, "PlaceNameRegion")?.ToString();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "AutoGreet could not resolve territory display name for {TerritoryType}.", territoryType);
            return null;
        }
    }

    private static object? GetExcelSheet(Type rowType)
    {
        var methods = DalamudServices.DataManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethodDefinition)
            .OrderBy(m => m.GetParameters().Length);

        foreach (var method in methods)
        {
            try
            {
                var generic = method.MakeGenericMethod(rowType);
                var parameters = method.GetParameters();
                object?[] args = parameters.Length == 0 ? Array.Empty<object?>() : parameters.Select(_ => (object?)null).ToArray();
                return generic.Invoke(DalamudServices.DataManager, args);
            }
            catch
            {
                // Try the next overload.
            }
        }

        return null;
    }

    private static object? GetExcelRow(object sheet, uint rowId)
    {
        if (rowId == 0)
            return null;

        var sheetType = sheet.GetType();
        var getRow = sheetType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "GetRow" && m.GetParameters().Length >= 1);
        if (getRow is null) return null;

        var first = getRow.GetParameters()[0];
        object idArg = first.ParameterType == typeof(uint) ? rowId : Convert.ChangeType(rowId, first.ParameterType, CultureInfo.InvariantCulture);
        object?[] args = getRow.GetParameters().Length == 1
            ? new object?[] { idArg }
            : getRow.GetParameters().Select((_, i) => i == 0 ? idArg : null).ToArray();

        return getRow.Invoke(sheet, args);
    }

    private static object? ResolveRowRef(object? rowRef)
    {
        if (rowRef is null) return null;
        foreach (var name in new[] { "Value", "ValueNullable" })
        {
            var value = GetMemberValue(rowRef, name);
            if (value is not null) return value;
        }
        return rowRef;
    }

    private static object? GetMemberValue(object? value, string memberName)
    {
        if (value is null) return null;

        var type = value.GetType();
        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) return field.GetValue(value);

        var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return prop?.GetValue(value);
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false, false);
                if (type is not null) return type;
            }
            catch
            {
                // Ignore dynamic/reflection-only assembly issues.
            }
        }

        return Type.GetType(fullName, false, false);
    }
}
