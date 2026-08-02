using Dalamud.Configuration;

namespace ShoutRunner;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool WindowVisible { get; set; } = true;
    public bool SettingsWindowVisible { get; set; }
    public string ActiveVenueProfile { get; set; } = "Default";
    public string LastCharacterName { get; set; } = string.Empty;
    public string LastCharacterHomeWorld { get; set; } = string.Empty;
    public string LastCharacterCurrentWorld { get; set; } = string.Empty;

    internal static string CleanProfileName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return "Default";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');
        return name.Length > 64 ? name[..64] : name;
    }
}

internal enum MessageChannel
{
    Shout,
    Yell,
    Say,
    Echo,
}

internal enum CityTarget
{
    LimsaLominsa,
    Gridania,
    Uldah,
}

internal enum ShoutRunnerRegion
{
    NorthAmerica,
    Europe,
    Japan,
    Oceania,
}

internal enum RunPhase
{
    Idle,
    Preparing,
    TravelingDataCenter,
    TravelingWorld,
    TravelingCity,
    WaitingForArrival,
    SendingMessages,
    Paused,
    Completed,
    Failed,
}

internal sealed class MessageBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MessageChannel Channel { get; set; } = MessageChannel.Shout;
    public string Text { get; set; } = string.Empty;
}

internal sealed class VenueProfile
{
    public string Name { get; set; } = "Default";
    public List<MessageBlock> Messages { get; set; } =
    [
        new() { Channel = MessageChannel.Echo, Text = "ShoutRunner test message." },
    ];
    public HashSet<CityTarget> Cities { get; set; } =
    [
        CityTarget.LimsaLominsa,
        CityTarget.Gridania,
        CityTarget.Uldah,
    ];
    public HashSet<string> Worlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DeveloperMode { get; set; }
    public int MessageDelaySeconds { get; set; } = 3;
    public int InitialRetryDelaySeconds { get; set; } = 5;
    public int RetryDelayIncreaseSeconds { get; set; } = 5;
    public int MaximumTravelAttempts { get; set; } = 4;
    public bool TryAlternateDataCenterWorlds { get; set; } = true;

    public void Normalize()
    {
        Name = Configuration.CleanProfileName(Name);
        Messages ??= [];
        Cities ??= [];
        Worlds = (Worlds ?? [])
            .Where(world => !string.IsNullOrWhiteSpace(world))
            .Select(world => world.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var block in Messages)
        {
            block.Id = block.Id == Guid.Empty ? Guid.NewGuid() : block.Id;
            block.Text ??= string.Empty;
            if (block.Text.Length > 400)
                block.Text = block.Text[..400];
        }
        MessageDelaySeconds = Math.Clamp(MessageDelaySeconds, 1, 30);
        InitialRetryDelaySeconds = Math.Clamp(InitialRetryDelaySeconds, 5, 120);
        RetryDelayIncreaseSeconds = Math.Clamp(RetryDelayIncreaseSeconds, 0, 120);
        MaximumTravelAttempts = Math.Clamp(MaximumTravelAttempts, 1, 20);
    }
}

internal sealed record WorldDefinition(
    string Name,
    string DataCenter,
    ShoutRunnerRegion Region);

internal sealed record CityDefinition(
    CityTarget Id,
    string Name,
    string[] TerritoryNames,
    string[] AetheryteNames);

internal static class WorldCatalog
{
    public static readonly IReadOnlyList<CityDefinition> Cities =
    [
        new(CityTarget.LimsaLominsa, "Limsa Lominsa", ["Limsa Lominsa Lower Decks", "Limsa Lominsa Upper Decks"], ["Limsa Lominsa Lower Decks", "Limsa Lominsa"]),
        new(CityTarget.Gridania, "Gridania", ["New Gridania", "Old Gridania"], ["New Gridania", "Gridania"]),
        new(CityTarget.Uldah, "Ul'dah", ["Ul'dah - Steps of Nald", "Ul'dah - Steps of Thal"], ["Ul'dah - Steps of Nald", "Ul'dah"]),
    ];

    public static readonly IReadOnlyList<WorldDefinition> Worlds = BuildWorlds();

    public static ShoutRunnerRegion DetectHomeRegion(string? homeWorld) =>
        Worlds.FirstOrDefault(world => world.Name.Equals(homeWorld, StringComparison.OrdinalIgnoreCase))?.Region
        ?? ShoutRunnerRegion.NorthAmerica;

    public static IReadOnlyList<WorldDefinition> VisibleWorlds(string? homeWorld, bool developerMode)
    {
        if (developerMode)
            return Worlds;

        var homeRegion = DetectHomeRegion(homeWorld);
        return homeRegion == ShoutRunnerRegion.Oceania
            ? Worlds.Where(world => world.Region == ShoutRunnerRegion.Oceania).ToArray()
            : Worlds.Where(world => world.Region == homeRegion || world.Region == ShoutRunnerRegion.Oceania).ToArray();
    }

    public static WorldDefinition? FindWorld(string? name) =>
        Worlds.FirstOrDefault(world => world.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<WorldDefinition> BuildWorlds()
    {
        var worlds = new List<WorldDefinition>();
        Add(worlds, ShoutRunnerRegion.NorthAmerica, "Aether", "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren");
        Add(worlds, ShoutRunnerRegion.NorthAmerica, "Primal", "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros");
        Add(worlds, ShoutRunnerRegion.NorthAmerica, "Crystal", "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera");
        Add(worlds, ShoutRunnerRegion.NorthAmerica, "Dynamis", "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph");
        Add(worlds, ShoutRunnerRegion.Europe, "Chaos", "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan");
        Add(worlds, ShoutRunnerRegion.Europe, "Light", "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark");
        Add(worlds, ShoutRunnerRegion.Japan, "Elemental", "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon");
        Add(worlds, ShoutRunnerRegion.Japan, "Gaia", "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima");
        Add(worlds, ShoutRunnerRegion.Japan, "Mana", "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan");
        Add(worlds, ShoutRunnerRegion.Japan, "Meteor", "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus");
        Add(worlds, ShoutRunnerRegion.Oceania, "Materia", "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan");
        return worlds;
    }

    private static void Add(List<WorldDefinition> output, ShoutRunnerRegion region, string dc, params string[] names) =>
        output.AddRange(names.Select(name => new WorldDefinition(name, dc, region)));
}

internal sealed record RouteStop(string World, string DataCenter, CityTarget City)
{
    public string CityName => WorldCatalog.Cities.First(city => city.Id == City).Name;
}

internal sealed class PersistedRunState
{
    public string RunId { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string CharacterHomeWorld { get; set; } = string.Empty;
    public string ReceiptCode { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public RunPhase Phase { get; set; }
    public List<RouteStop> Route { get; set; } = [];
    public HashSet<int> SkippedStopIndexes { get; set; } = [];
    public int StopIndex { get; set; }
    public int MessageIndex { get; set; }
    public int TravelAttempt { get; set; }
    public DateTime NextActionUtc { get; set; }
    public DateTime TravelRequestUtc { get; set; }
    public bool TravelBusyObserved { get; set; }
    public bool AwaitingInitialLogin { get; set; }
    public bool AwaitingDestinationLogin { get; set; }
    public ulong TeleportGilSpent { get; set; }
    public string Status { get; set; } = string.Empty;
}
