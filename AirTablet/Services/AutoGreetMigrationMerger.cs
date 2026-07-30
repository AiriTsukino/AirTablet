using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AirTablet.Services;

internal static class AutoGreetMigrationMerger
{
    internal readonly record struct MergeResult(
        int VipAssignments,
        int BlacklistEntries,
        int VipTiers);

    public static MergeResult Merge(
        string preservedDirectory,
        string importedDirectory)
    {
        var preservedVenuesPath = Path.Combine(
            preservedDirectory,
            "VenueProfiles.json");
        var preservedVisitorsPath = Path.Combine(
            preservedDirectory,
            "VisitorHistory.json");
        var importedVenuesPath = Path.Combine(
            importedDirectory,
            "VenueProfiles.json");
        var importedVisitorsPath = Path.Combine(
            importedDirectory,
            "VisitorHistory.json");

        if (!File.Exists(preservedVenuesPath) ||
            !File.Exists(preservedVisitorsPath) ||
            !File.Exists(importedVenuesPath) ||
            !File.Exists(importedVisitorsPath))
        {
            return default;
        }

        var preservedVenuesRoot =
            JObject.Parse(File.ReadAllText(preservedVenuesPath));
        var preservedVisitorsRoot =
            JObject.Parse(File.ReadAllText(preservedVisitorsPath));
        var importedVenuesRoot =
            JObject.Parse(File.ReadAllText(importedVenuesPath));
        var importedVisitorsRoot =
            JObject.Parse(File.ReadAllText(importedVisitorsPath));

        var preservedVenues = GetArray(preservedVenuesRoot, "Venues");
        var importedVenues = GetArray(importedVenuesRoot, "Venues");
        var preservedVisitorsByVenue = GetObject(
            preservedVisitorsRoot,
            "LifetimeVisitorsByVenue");
        var importedVisitorsByVenue = GetObject(
            importedVisitorsRoot,
            "LifetimeVisitorsByVenue");
        if (preservedVenues is null ||
            importedVenues is null ||
            preservedVisitorsByVenue is null ||
            importedVisitorsByVenue is null)
        {
            return default;
        }

        var vipAssignments = 0;
        var blacklistEntries = 0;
        var vipTiers = 0;

        foreach (var preservedVenue in preservedVenues.Children<JObject>())
        {
            var preservedVenueId = GetString(preservedVenue, "Id");
            var preservedVenueName = GetString(preservedVenue, "Name");
            var importedVenue = FindVenue(
                importedVenues,
                preservedVenueId,
                preservedVenueName);
            if (importedVenue is null)
                continue;

            var importedVenueId = GetString(importedVenue, "Id");
            if (string.IsNullOrWhiteSpace(importedVenueId))
                continue;

            blacklistEntries += MergeBlacklist(
                preservedVenue,
                importedVenue);
            var (tierMap, addedTiers) = MergeVipTiers(
                preservedVenue,
                importedVenue);
            vipTiers += addedTiers;

            var preservedVisitors = GetObjectProperty(
                preservedVisitorsByVenue,
                preservedVenueId);
            if (preservedVisitors is null)
                continue;

            var importedVisitors = GetObjectProperty(
                importedVisitorsByVenue,
                importedVenueId);
            if (importedVisitors is null)
            {
                importedVisitors = new JObject();
                importedVisitorsByVenue[importedVenueId] =
                    importedVisitors;
            }

            var defaultTierId = GetString(
                importedVenue,
                "DefaultVipTierId");
            if (string.IsNullOrWhiteSpace(defaultTierId) ||
                defaultTierId == Guid.Empty.ToString())
            {
                defaultTierId = GetArray(importedVenue, "VipTiers")?
                    .Children<JObject>()
                    .Select(tier => GetString(tier, "Id"))
                    .FirstOrDefault(id =>
                        !string.IsNullOrWhiteSpace(id)) ??
                    Guid.Empty.ToString();
            }

            foreach (var preservedVisitorProperty in
                     preservedVisitors.Properties())
            {
                if (preservedVisitorProperty.Value is not JObject
                    preservedVisitor ||
                    !IsVip(preservedVisitor))
                {
                    continue;
                }

                var importedVisitorProperty = FindProperty(
                    importedVisitors,
                    preservedVisitorProperty.Name);
                JObject importedVisitor;
                if (importedVisitorProperty?.Value is JObject existing)
                {
                    importedVisitor = existing;
                }
                else
                {
                    importedVisitor =
                        (JObject)preservedVisitor.DeepClone();
                    importedVisitors[preservedVisitorProperty.Name] =
                        importedVisitor;
                }

                var preservedTierId = GetString(
                    preservedVisitor,
                    "VipTierId");
                var importedTierId =
                    !string.IsNullOrWhiteSpace(preservedTierId) &&
                    tierMap.TryGetValue(
                        preservedTierId,
                        out var mappedTierId)
                        ? mappedTierId
                        : defaultTierId;

                SetValue(importedVisitor, "Vip", true);
                SetValue(
                    importedVisitor,
                    "VipTierId",
                    string.IsNullOrWhiteSpace(importedTierId)
                        ? Guid.Empty.ToString()
                        : importedTierId);
                vipAssignments++;
            }
        }

        if (vipAssignments == 0 &&
            blacklistEntries == 0 &&
            vipTiers == 0)
        {
            return default;
        }

        SaveJson(importedVenuesPath, importedVenuesRoot);
        SaveJson(importedVisitorsPath, importedVisitorsRoot);
        return new MergeResult(
            vipAssignments,
            blacklistEntries,
            vipTiers);
    }

    private static int MergeBlacklist(
        JObject preservedVenue,
        JObject importedVenue)
    {
        var preserved = GetArray(preservedVenue, "Blacklist");
        if (preserved is null)
            return 0;

        var imported = GetArray(importedVenue, "Blacklist");
        if (imported is null)
        {
            imported = new JArray();
            SetValue(importedVenue, "Blacklist", imported);
        }

        var known = imported
            .Values<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var value in preserved.Values<string>()
                     .Where(value =>
                         !string.IsNullOrWhiteSpace(value)))
        {
            if (!known.Add(value!))
                continue;

            imported.Add(value);
            added++;
        }

        return added;
    }

    private static (
        Dictionary<string, string> TierMap,
        int AddedTiers) MergeVipTiers(
        JObject preservedVenue,
        JObject importedVenue)
    {
        var map = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var preserved = GetArray(preservedVenue, "VipTiers");
        if (preserved is null)
            return (map, 0);

        var imported = GetArray(importedVenue, "VipTiers");
        if (imported is null)
        {
            imported = new JArray();
            SetValue(importedVenue, "VipTiers", imported);
        }

        var added = 0;
        foreach (var preservedTier in preserved.Children<JObject>())
        {
            var preservedId = GetString(preservedTier, "Id");
            if (string.IsNullOrWhiteSpace(preservedId) ||
                preservedId == Guid.Empty.ToString())
            {
                continue;
            }

            var preservedName = GetString(preservedTier, "Name");
            var importedTier = imported.Children<JObject>()
                .FirstOrDefault(tier =>
                    GetString(tier, "Id").Equals(
                        preservedId,
                        StringComparison.OrdinalIgnoreCase))
                ?? imported.Children<JObject>()
                    .FirstOrDefault(tier =>
                        !string.IsNullOrWhiteSpace(preservedName) &&
                        GetString(tier, "Name").Equals(
                            preservedName,
                            StringComparison.OrdinalIgnoreCase));

            if (importedTier is null)
            {
                importedTier = (JObject)preservedTier.DeepClone();
                imported.Add(importedTier);
                added++;
            }

            var importedId = GetString(importedTier, "Id");
            if (!string.IsNullOrWhiteSpace(importedId))
                map[preservedId] = importedId;
        }

        return (map, added);
    }

    private static JObject? FindVenue(
        JArray venues,
        string id,
        string name)
    {
        var byId = venues.Children<JObject>()
            .FirstOrDefault(venue =>
                !string.IsNullOrWhiteSpace(id) &&
                GetString(venue, "Id").Equals(
                    id,
                    StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId;

        return venues.Children<JObject>()
            .FirstOrDefault(venue =>
                !string.IsNullOrWhiteSpace(name) &&
                GetString(venue, "Name").Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVip(JObject visitor)
    {
        var vipToken = GetValue(visitor, "Vip");
        if (vipToken?.Type == JTokenType.Boolean &&
            vipToken.Value<bool>())
        {
            return true;
        }

        return Guid.TryParse(
                   GetString(visitor, "VipTierId"),
                   out var tierId) &&
               tierId != Guid.Empty;
    }

    private static JArray? GetArray(JObject value, string name) =>
        GetValue(value, name) as JArray;

    private static JObject? GetObject(JObject value, string name) =>
        GetValue(value, name) as JObject;

    private static JObject? GetObjectProperty(
        JObject value,
        string propertyName) =>
        FindProperty(value, propertyName)?.Value as JObject;

    private static JToken? GetValue(
        JObject value,
        string propertyName) =>
        FindProperty(value, propertyName)?.Value;

    private static string GetString(
        JObject value,
        string propertyName) =>
        GetValue(value, propertyName)?.Value<string>()?.Trim() ??
        string.Empty;

    private static JProperty? FindProperty(
        JObject value,
        string propertyName) =>
        value.Properties().FirstOrDefault(property =>
            property.Name.Equals(
                propertyName,
                StringComparison.OrdinalIgnoreCase));

    private static void SetValue(
        JObject target,
        string propertyName,
        object value)
    {
        var token = value as JToken ?? JToken.FromObject(value);
        var property = FindProperty(target, propertyName);
        if (property is null)
            target[propertyName] = token;
        else
            property.Value = token;
    }

    private static void SaveJson(string path, JObject value)
    {
        var temporaryPath = path + ".merge.tmp";
        File.WriteAllText(
            temporaryPath,
            value.ToString(Formatting.Indented));
        File.Move(temporaryPath, path, true);
    }
}
