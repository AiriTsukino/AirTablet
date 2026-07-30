namespace AutoGreet.Models;

[Serializable]
public sealed class VenuePlotLock
{
    public const string LocationKindAny = "";
    public const string LocationKindPlot = "Plot";
    public const string LocationKindApartment = "Apartment";

    public bool Enabled { get; set; }
    public string World { get; set; } = string.Empty;

    // Kept for backwards compatibility with old configs and as a fallback when the game does
    // not expose housing district data. New plot locks should use HousingDistrict instead.
    public uint TerritoryType { get; set; }
    public uint OriginalHouseTerritoryType { get; set; }

    public string HousingDistrict { get; set; } = string.Empty;
    public string LocationKind { get; set; } = LocationKindAny;
    public int Ward { get; set; } = -1;
    public int Plot { get; set; } = -1;
    public int Room { get; set; } = -1;
    public int Division { get; set; } = -1;

    public bool IsApartment => LocationKind.Equals(LocationKindApartment, StringComparison.OrdinalIgnoreCase);
    public bool IsPlot => LocationKind.Equals(LocationKindPlot, StringComparison.OrdinalIgnoreCase);

    public string DisplayText
    {
        get
        {
            var world = string.IsNullOrWhiteSpace(World) ? "Any world" : World.Trim();
            var district = string.IsNullOrWhiteSpace(HousingDistrict)
                ? GetLegacyTerritoryText()
                : HousingDistrict.Trim();
            var kind = string.IsNullOrWhiteSpace(LocationKind) ? "Any housing type" : LocationKind.Trim();
            var wardText = Ward < 0 ? "Any ward" : $"Ward {Ward + 1}";
            var divisionText = Division <= 0 ? "Any division" : Division == 2 ? "Subdivision" : "Main division";

            var unitText = LocationKind.Equals(LocationKindApartment, StringComparison.OrdinalIgnoreCase)
                ? Room < 0 ? "Any apartment room" : $"Room {Room}"
                : Plot < 0 ? "Any plot" : $"Plot {Plot + 1}";

            return $"{world} / {district} / {kind} / {wardText} / {divisionText} / {unitText}";
        }
    }

    public void CopyFrom(VenuePlotLock other)
    {
        World = other.World;
        TerritoryType = other.TerritoryType;
        OriginalHouseTerritoryType = other.OriginalHouseTerritoryType;
        HousingDistrict = other.HousingDistrict;
        LocationKind = other.LocationKind;
        Ward = other.Ward;
        Plot = other.Plot;
        Room = other.Room;
        Division = other.Division;
    }

    public bool Matches(VenuePlotLock current)
    {
        if (!Enabled) return true;

        if (!string.IsNullOrWhiteSpace(World) && !World.Equals(current.World, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!MatchesDistrict(current))
            return false;

        if (!string.IsNullOrWhiteSpace(LocationKind)
            && !LocationKind.Equals(current.LocationKind, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Ward >= 0 && current.Ward != Ward) return false;
        if (Division > 0 && current.Division != Division) return false;

        if (LocationKind.Equals(LocationKindApartment, StringComparison.OrdinalIgnoreCase))
        {
            if (Room >= 0 && current.Room != Room) return false;
        }
        else if (Plot >= 0 && current.Plot != Plot)
        {
            return false;
        }

        return true;
    }

    private bool MatchesDistrict(VenuePlotLock current)
    {
        if (!string.IsNullOrWhiteSpace(HousingDistrict))
            return HousingDistrict.Equals(current.HousingDistrict, StringComparison.OrdinalIgnoreCase);

        // Backwards compatible fallback for old saved plot locks that only had territory IDs.
        if (OriginalHouseTerritoryType != 0)
            return current.OriginalHouseTerritoryType == OriginalHouseTerritoryType || current.TerritoryType == OriginalHouseTerritoryType;

        return TerritoryType == 0 || current.TerritoryType == TerritoryType || current.OriginalHouseTerritoryType == TerritoryType;
    }

    private string GetLegacyTerritoryText()
    {
        var territory = OriginalHouseTerritoryType != 0 ? OriginalHouseTerritoryType : TerritoryType;
        return territory == 0 ? "Any housing district" : $"Legacy territory {territory}";
    }
}
