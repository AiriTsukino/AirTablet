using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ShoutRunner;

internal sealed class PersistenceService
{
    private readonly Configuration config;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private readonly Dictionary<string, VenueProfile> profiles = new(StringComparer.OrdinalIgnoreCase);

    public PersistenceService(Configuration config)
    {
        this.config = config;
        Root = DalamudServices.PluginInterface.ConfigDirectory.FullName;
        ProfilesDirectory = Path.Combine(Root, "Profiles");
        RunStateFile = Path.Combine(Root, "run-state.json");
        LoadProfiles();
    }

    public string Root { get; }
    public string ProfilesDirectory { get; }
    public string RunStateFile { get; }
    public IReadOnlyDictionary<string, VenueProfile> Profiles => profiles;
    public VenueProfile ActiveProfile => GetOrCreate(config.ActiveVenueProfile);
    public string LastCharacterName => config.LastCharacterName;
    public string LastCharacterHomeWorld => config.LastCharacterHomeWorld;
    public string LastCharacterCurrentWorld => config.LastCharacterCurrentWorld;

    public void RememberCharacter(string name, string homeWorld, string currentWorld)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(homeWorld))
            return;
        if (config.LastCharacterName.Equals(name, StringComparison.Ordinal) &&
            config.LastCharacterHomeWorld.Equals(homeWorld, StringComparison.Ordinal) &&
            config.LastCharacterCurrentWorld.Equals(currentWorld, StringComparison.Ordinal))
            return;
        config.LastCharacterName = name;
        config.LastCharacterHomeWorld = homeWorld;
        config.LastCharacterCurrentWorld = currentWorld;
        SaveNow();
    }

    public VenueProfile GetOrCreate(string name)
    {
        name = Configuration.CleanProfileName(name);
        if (!profiles.TryGetValue(name, out var profile))
        {
            profile = new VenueProfile { Name = name };
            profile.Normalize();
            profiles[name] = profile;
            SaveProfile(profile);
        }
        return profile;
    }

    public bool Activate(string name)
    {
        name = Configuration.CleanProfileName(name);
        if (!profiles.ContainsKey(name))
            return false;
        config.ActiveVenueProfile = name;
        SaveConfig();
        return true;
    }

    public bool Create(string name, bool copyCurrent)
    {
        name = Configuration.CleanProfileName(name);
        if (profiles.ContainsKey(name))
            return false;
        var profile = copyCurrent
            ? JsonSerializer.Deserialize<VenueProfile>(JsonSerializer.Serialize(ActiveProfile, json), json) ?? new VenueProfile()
            : new VenueProfile();
        profile.Name = name;
        profile.Normalize();
        profiles[name] = profile;
        config.ActiveVenueProfile = name;
        SaveNow();
        return true;
    }

    public bool Delete(string name)
    {
        name = Configuration.CleanProfileName(name);
        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) || !profiles.Remove(name))
            return false;
        var path = ProfilePath(name);
        if (File.Exists(path))
            File.Delete(path);
        config.ActiveVenueProfile = "Default";
        GetOrCreate("Default");
        SaveConfig();
        return true;
    }

    public void SaveNow()
    {
        SaveConfig();
        foreach (var profile in profiles.Values)
            SaveProfile(profile);
    }

    public void SaveConfig()
    {
        config.ActiveVenueProfile = Configuration.CleanProfileName(config.ActiveVenueProfile);
        DalamudServices.PluginInterface.SavePluginConfig(config);
    }

    public void SaveProfile(VenueProfile profile)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDirectory);
            profile.Normalize();
            File.WriteAllText(ProfilePath(profile.Name), JsonSerializer.Serialize(profile, json));
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShoutRunner could not save venue profile {Profile}.", profile.Name);
        }
    }

    public PersistedRunState? LoadRunState()
    {
        try
        {
            return File.Exists(RunStateFile)
                ? JsonSerializer.Deserialize<PersistedRunState>(File.ReadAllText(RunStateFile), json)
                : null;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShoutRunner could not load its saved run state.");
            return null;
        }
    }

    public void SaveRunState(PersistedRunState? state)
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (state is null)
            {
                if (File.Exists(RunStateFile))
                    File.Delete(RunStateFile);
                return;
            }
            File.WriteAllText(RunStateFile, JsonSerializer.Serialize(state, json));
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShoutRunner could not save its run state.");
        }
    }

    private void LoadProfiles()
    {
        Directory.CreateDirectory(ProfilesDirectory);
        foreach (var path in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<VenueProfile>(File.ReadAllText(path), json);
                if (profile is null)
                    continue;
                profile.Normalize();
                profiles[profile.Name] = profile;
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(ex, "ShoutRunner skipped unreadable profile file {Path}.", path);
            }
        }
        if (profiles.Count == 0)
            profiles["Default"] = new VenueProfile();
        if (!profiles.ContainsKey(config.ActiveVenueProfile))
            config.ActiveVenueProfile = profiles.Keys.OrderBy(name => name).First();
        SaveNow();
    }

    private string ProfilePath(string name) =>
        Path.Combine(ProfilesDirectory, Configuration.CleanProfileName(name) + ".json");
}

internal sealed class ChatCommandService
{
    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> SendAsync(MessageBlock block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = (block.Text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            LastError = "The message block was empty.";
            return false;
        }
        if (text.Length > 400)
        {
            LastError = "The message block exceeded 400 characters.";
            return false;
        }
        var command = $"/{block.Channel.ToString().ToLowerInvariant()} {text}";
        return await DalamudServices.Framework.RunOnFrameworkThread(() => Send(command)).ConfigureAwait(false);
    }

    private unsafe bool Send(string command)
    {
        try
        {
            using var value = new Utf8String(command);
            if (value.Length > 500)
            {
                LastError = "The encoded command exceeded the game chat limit.";
                return false;
            }
            var shell = RaptureShellModule.Instance();
            var ui = UIModule.Instance();
            if (shell is null || ui is null)
            {
                LastError = "The game chat shell is not ready.";
                return false;
            }
            shell->ExecuteCommandInner(&value, ui);
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DalamudServices.Log.Warning(ex, "ShoutRunner could not send a message block.");
            return false;
        }
    }
}

internal sealed unsafe class TravelService
{
    private readonly Dictionary<CityTarget, uint> cityAetherytes = [];
    private readonly object debugLogLock = new();
    private readonly List<string> debugLog = [];
    private readonly Dictionary<string, DateTime> debugThrottle = new(StringComparer.Ordinal);
    private string requestedWorld = string.Empty;
    private string requestedDataCenter = string.Empty;
    private DateTime requestStartedUtc;
    private DateTime nextUiActionUtc;
    private bool logoutRequested;
    private bool characterTravelMenuOpened;
    private bool dataCenterDestinationChosen;
    private bool dataCenterProceedSubmitted;
    private bool dataCenterArrivalAcknowledged;
    private bool dataCenterSelectionChosen;
    private bool worldSelectionChosen;
    private bool aetheryteApproachActive;
    private bool cityZoneTeleportPending;
    private bool titleStartSelected;
    private bool characterLoginSelected;
    private bool worldVisitSubmitted;
    private bool returnHomeRequested;
    private bool returnHomeConfirmationSubmitted;
    private DateTime returnHomeConfirmationSubmittedUtc;
    private bool returnHomeProceedSubmitted;
    private bool returnHomeOnly;
    private bool returningHomeViaAetheryte;
    private string queuedDataCenterWorld = string.Empty;
    private string queuedDataCenterName = string.Empty;
    private string runCharacterName = string.Empty;
    private string runCharacterHomeWorld = string.Empty;
    private int generalReactionDelaySeconds;

    public TravelService()
    {
        cityAetherytes[CityTarget.Gridania] = 2;
        cityAetherytes[CityTarget.LimsaLominsa] = 8;
        cityAetherytes[CityTarget.Uldah] = 9;
        ResolveCityAetherytes();
        Trace("Travel diagnostics started.");
    }

    public string DebugLogText
    {
        get
        {
            lock (debugLogLock)
                return string.Join(Environment.NewLine, debugLog);
        }
    }

    public bool DestinationArrivalAcknowledged => dataCenterArrivalAcknowledged;
    public bool AutomaticDataCenterConnectionPending => dataCenterProceedSubmitted && !dataCenterArrivalAcknowledged;
    public bool IsCityZoneTeleportPending => cityZoneTeleportPending;

    public unsafe bool IsCityTeleportBusy
    {
        get
        {
            try
            {
                var telepo = Telepo.Instance();
                return (telepo is not null && telepo->ActiveTeleportRequest) ||
                       DalamudServices.Condition[ConditionFlag.BetweenAreas] ||
                       DalamudServices.Condition[ConditionFlag.BetweenAreas51];
            }
            catch { return false; }
        }
    }

    public void SetGeneralReactionDelaySeconds(int seconds) =>
        generalReactionDelaySeconds = Math.Clamp(seconds, 0, 10);

    public void ClearDebugLog()
    {
        lock (debugLogLock)
            debugLog.Clear();
        Trace("Travel diagnostics cleared.");
    }

    public void RecordRunDiagnostic(string message) => Trace($"Run state: {message}");

    public void PauseNavigation()
    {
        StopAetheryteApproach();
        Trace("Run paused; stopped active Aetheryte approach while preserving the current travel context.");
    }

    public void ResetInGameNavigationForResume()
    {
        ClearNavigation();
        Trace("Cleared stale in-game navigation state for route reconciliation after resume.");
    }

    public bool IsTravelBusy
    {
        get
        {
            try
            {
                if (!DalamudServices.ClientState.IsLoggedIn || DalamudServices.ObjectTable.LocalPlayer is null)
                    return !string.IsNullOrWhiteSpace(requestedWorld);
                if (DalamudServices.Condition[ConditionFlag.WaitingToVisitOtherWorld] ||
                    DalamudServices.Condition[ConditionFlag.ReadyingVisitOtherWorld])
                    return true;
                var agent = AgentWorldTravel.Instance();
                return agent is not null && agent->IsAgentActive();
            }
            catch { return !string.IsNullOrWhiteSpace(requestedWorld); }
        }
    }

    public string NavigationStatus => string.IsNullOrWhiteSpace(requestedWorld)
        ? "Ready"
        : returningHomeViaAetheryte
            ? $"Returning to home world {requestedWorld} before data-center travel"
        : aetheryteApproachActive
            ? $"Walking back to the city Aetheryte for travel to {requestedWorld}"
        : $"Navigating to {requestedWorld} on {requestedDataCenter}";

    public string CurrentWorld
    {
        get
        {
            try { return DalamudServices.ObjectTable.LocalPlayer?.CurrentWorld.Value.Name.ToString().Trim() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    public string HomeWorld
    {
        get
        {
            try { return DalamudServices.PlayerState.IsLoaded ? DalamudServices.PlayerState.HomeWorld.Value.Name.ToString().Trim() : string.Empty; }
            catch { return string.Empty; }
        }
    }

    public string CharacterName
    {
        get
        {
            try { return DalamudServices.PlayerState.IsLoaded ? DalamudServices.PlayerState.CharacterName.ToString().Trim() : string.Empty; }
            catch { return string.Empty; }
        }
    }

    public unsafe string GetLobbyCharacterCurrentWorld(string characterName, string characterHomeWorld)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return string.Empty;
        var lobby = AgentLobby.Instance();
        if (lobby is null)
            return string.Empty;
        var entries = lobby->LobbyData.CharaSelectEntries;
        for (var index = 0; index < entries.Count; index++)
        {
            CharaSelectCharacterEntry* candidate = entries[index];
            if (candidate is null ||
                !candidate->NameString.Equals(characterName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(characterHomeWorld) &&
                 !candidate->HomeWorldNameString.Equals(characterHomeWorld, StringComparison.OrdinalIgnoreCase)))
                continue;
            return candidate->CurrentWorldNameString;
        }
        return string.Empty;
    }

    public string CurrentTerritoryName
    {
        get
        {
            try
            {
                var row = DalamudServices.DataManager.GetExcelSheet<TerritoryType>().GetRow(DalamudServices.ClientState.TerritoryType);
                return row.PlaceName.Value.Name.ToString().Trim();
            }
            catch { return string.Empty; }
        }
    }

    public bool IsInCity(CityTarget city)
    {
        var current = CurrentTerritoryName;
        var mainTerritory = WorldCatalog.Cities.First(item => item.Id == city).TerritoryNames[0];
        return current.Equals(mainTerritory, StringComparison.OrdinalIgnoreCase);
    }

    public CityDefinition? GetCurrentCity()
    {
        var current = CurrentTerritoryName;
        return WorldCatalog.Cities.FirstOrDefault(city => city.TerritoryNames.Any(name =>
            current.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    public bool RequestWorld(string world, string characterName, string characterHomeWorld)
    {
        try
        {
            var target = WorldCatalog.FindWorld(world);
            var current = WorldCatalog.FindWorld(CurrentWorld);
            if (target is null || current is null)
                return false;

            if (!string.IsNullOrWhiteSpace(requestedWorld) &&
                requestedWorld.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (returningHomeViaAetheryte &&
                queuedDataCenterWorld.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            runCharacterName = characterName.Trim();
            runCharacterHomeWorld = characterHomeWorld.Trim();
            var home = WorldCatalog.FindWorld(runCharacterHomeWorld);
            returningHomeViaAetheryte = false;
            queuedDataCenterWorld = string.Empty;
            queuedDataCenterName = string.Empty;
            if (!current.DataCenter.Equals(target.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                home is not null &&
                current.DataCenter.Equals(home.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                !current.Name.Equals(home.Name, StringComparison.OrdinalIgnoreCase))
            {
                requestedWorld = home.Name;
                requestedDataCenter = home.DataCenter;
                queuedDataCenterWorld = target.Name;
                queuedDataCenterName = target.DataCenter;
                returningHomeViaAetheryte = true;
            }
            else
            {
                requestedWorld = target.Name;
                requestedDataCenter = target.DataCenter;
            }
            requestStartedUtc = DateTime.UtcNow;
            nextUiActionUtc = DateTime.UtcNow;
            worldVisitSubmitted = false;
            returnHomeRequested = false;
            returnHomeConfirmationSubmitted = false;
            returnHomeConfirmationSubmittedUtc = default;
            returnHomeProceedSubmitted = false;
            Trace(returningHomeViaAetheryte
                ? $"Data-center request queued: {current.Name} -> {target.Name} ({target.DataCenter}); returning through the Aetheryte to home world {home!.Name} first."
                : $"World request started: {CurrentWorld} -> {requestedWorld} ({requestedDataCenter}); character={runCharacterName}@{runCharacterHomeWorld}.");

            if (current.DataCenter.Equals(target.DataCenter, StringComparison.OrdinalIgnoreCase))
            {
                if (GetReadyAddon("WorldTravelSelect") is not null)
                    Trace($"Detected an already-open World Visit menu while resuming travel to {requestedWorld}; continuing from destination selection.");
                else
                    TryApproachCityAetheryte();
                return true;
            }

            logoutRequested = false;
            characterTravelMenuOpened = false;
            dataCenterDestinationChosen = false;
            dataCenterProceedSubmitted = false;
            dataCenterArrivalAcknowledged = false;
            dataCenterSelectionChosen = false;
            worldSelectionChosen = false;
            titleStartSelected = false;
            characterLoginSelected = false;
            return true;
        }
        catch (Exception ex)
        {
            Trace($"ERROR requesting travel to {world}: {ex.GetType().Name}: {ex.Message}");
            DalamudServices.Log.Warning(ex, "ShoutRunner could not request travel to {World}.", world);
            return false;
        }
    }

    public bool RequestReturnHomeWorld(string characterName, string characterHomeWorld)
    {
        var home = WorldCatalog.FindWorld(characterHomeWorld);
        if (home is null || string.IsNullOrWhiteSpace(characterName))
            return false;
        if (returnHomeOnly && requestedWorld.Equals(home.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        ClearNavigation();
        requestedWorld = home.Name;
        requestedDataCenter = home.DataCenter;
        runCharacterName = characterName.Trim();
        runCharacterHomeWorld = characterHomeWorld.Trim();
        requestStartedUtc = DateTime.UtcNow;
        nextUiActionUtc = DateTime.UtcNow;
        returnHomeOnly = true;
        Trace($"Return-home request started through character selection: {runCharacterName}@{runCharacterHomeWorld}.");
        return true;
    }

    public void TickNavigation()
    {
        if (string.IsNullOrWhiteSpace(requestedWorld))
            return;
        if (CurrentWorld.Equals(requestedWorld, StringComparison.OrdinalIgnoreCase))
        {
            if (returningHomeViaAetheryte && !string.IsNullOrWhiteSpace(queuedDataCenterWorld))
            {
                var completedHomeWorld = requestedWorld;
                requestedWorld = queuedDataCenterWorld;
                requestedDataCenter = queuedDataCenterName;
                queuedDataCenterWorld = string.Empty;
                queuedDataCenterName = string.Empty;
                returningHomeViaAetheryte = false;
                requestStartedUtc = DateTime.UtcNow;
                nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
                logoutRequested = false;
                characterTravelMenuOpened = false;
                dataCenterDestinationChosen = false;
                dataCenterProceedSubmitted = false;
                dataCenterArrivalAcknowledged = false;
                dataCenterSelectionChosen = false;
                worldSelectionChosen = false;
                titleStartSelected = false;
                characterLoginSelected = false;
                worldVisitSubmitted = false;
                returnHomeRequested = false;
                returnHomeConfirmationSubmitted = false;
                returnHomeConfirmationSubmittedUtc = default;
                returnHomeProceedSubmitted = false;
                Trace($"Arrived on home world {completedHomeWorld}; continuing queued data-center travel to {requestedWorld} on {requestedDataCenter}.");
                return;
            }
            ClearNavigation();
            return;
        }
        if (DateTime.UtcNow < nextUiActionUtc)
            return;

        var actionCycleUtc = DateTime.UtcNow;
        try
        {
            if (DalamudServices.ClientState.IsLoggedIn)
            {
                if (returnHomeOnly)
                {
                    if (!logoutRequested)
                    {
                        logoutRequested = SendShellCommand("/logout");
                        nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
                        return;
                    }
                    ConfirmVisibleDialog("log out");
                    return;
                }
                var current = WorldCatalog.FindWorld(CurrentWorld);
                if (current is not null && current.DataCenter.Equals(requestedDataCenter, StringComparison.OrdinalIgnoreCase))
                {
                    if (worldVisitSubmitted)
                    {
                        TraceThrottled(
                            "world-visit-underway",
                            $"World Visit to {requestedWorld} is underway; waiting for the destination world to load.",
                            TimeSpan.FromSeconds(5));
                        return;
                    }
                    if (ConfirmVisibleDialog(requestedWorld))
                    {
                        worldVisitSubmitted = true;
                        StopAetheryteApproach();
                        Trace($"Confirmed World Visit to {requestedWorld}; suppressing further Aetheryte interaction until travel completes.");
                        return;
                    }
                    if (TrySelectWorldVisitDestination())
                        return;
                    if (GetReadyAddon("WorldTravelSelect") is not null)
                        return;
                    if (TrySelectString("Visit Another World Server"))
                        return;
                    if (GetReadyAddon("SelectString") is not null)
                        return;
                    TryApproachCityAetheryte();
                    return;
                }

                if (!logoutRequested)
                {
                    logoutRequested = SendShellCommand("/logout");
                    nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
                    return;
                }
                ConfirmVisibleDialog("log out");
                return;
            }

            AdvanceDataCenterTravelMenus();
        }
        catch (Exception ex)
        {
            Trace($"ERROR advancing navigation: {ex.GetType().Name}: {ex.Message}");
            DalamudServices.Log.Warning(ex, "ShoutRunner's internal travel navigator could not advance its current step.");
            nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        }
        finally
        {
            ApplyReactionDelayFloor(actionCycleUtc);
        }
    }

    public void ContinueDataCenterNavigation(
        string world,
        string dataCenter,
        string characterName,
        string characterHomeWorld,
        bool awaitingAutomaticConnection = false)
    {
        if (string.IsNullOrWhiteSpace(requestedWorld))
        {
            requestedWorld = world.Trim();
            requestedDataCenter = dataCenter.Trim();
            runCharacterName = characterName.Trim();
            runCharacterHomeWorld = characterHomeWorld.Trim();
            requestStartedUtc = DateTime.UtcNow;
            nextUiActionUtc = DateTime.UtcNow;
            logoutRequested = true;
            dataCenterProceedSubmitted = awaitingAutomaticConnection;
            titleStartSelected = awaitingAutomaticConnection;
            Trace(awaitingAutomaticConnection
                ? $"Reconstructed submitted data-center travel after reload: {requestedWorld} on {requestedDataCenter}; waiting for the game's automatic destination connection without selecting Start."
                : $"Reconstructed data-center navigation after logout/reload: {requestedWorld} on {requestedDataCenter}; character={runCharacterName}@{runCharacterHomeWorld}.");
        }
        TickNavigation();
    }

    public void ContinueCharacterLogin(
        string characterName,
        string characterHomeWorld,
        string requiredCurrentWorld = "",
        bool allowTitleStart = true)
    {
        runCharacterName = characterName.Trim();
        runCharacterHomeWorld = characterHomeWorld.Trim();
        if (DateTime.UtcNow < nextUiActionUtc)
            return;
        var actionCycleUtc = DateTime.UtcNow;
        try
        {
            if (TryAcknowledgeOk()) return;
            if (TryConfirmCharacterLogin()) return;
            if (!string.IsNullOrWhiteSpace(requiredCurrentWorld))
            {
                if (!TryGetRunCharacterCurrentWorld(out var loginWorld) ||
                    !loginWorld.Equals(requiredCurrentWorld, StringComparison.OrdinalIgnoreCase))
                {
                    TraceThrottled(
                        "required-login-world-wait",
                        $"Destination login is waiting for {requiredCurrentWorld}; the saved character currently reports {loginWorld}. Title-screen Start is disabled during this automatic connection. Addons: {DescribeLobbyAddons()}.",
                        TimeSpan.FromSeconds(3));
                    return;
                }
            }
            if (TryLoginSelectedCharacter()) return;
            if (allowTitleStart && TryOpenTitleStart()) return;
            TraceThrottled(
                "character-login-wait",
                $"Waiting for the unlocked character list before logging in {runCharacterName}@{runCharacterHomeWorld}. Addons: {DescribeLobbyAddons()}. Characters: {DescribeLobbyCharacters()}.",
                TimeSpan.FromSeconds(3));
        }
        finally
        {
            ApplyReactionDelayFloor(actionCycleUtc);
        }
    }

    public unsafe bool RequestCity(CityTarget city)
    {
        if (!cityAetherytes.TryGetValue(city, out var id) || id == 0)
            return false;
        try
        {
            var telepo = Telepo.Instance();
            var accepted = telepo is not null && telepo->Teleport(id, 0);
            Trace(accepted
                ? $"City teleport request accepted for {city} using Aetheryte {id}."
                : $"City teleport request was not accepted for {city} using Aetheryte {id}.");
            return accepted;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShoutRunner could not teleport to {City}.", city);
            return false;
        }
    }

    public unsafe bool HandleAetheryteTicketPopup(AetheryteTicketAction action)
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon is null || !AddonContains(addon, "aetheryte ticket", "ticket"))
            return false;

        addon->FireCallbackInt(action == AetheryteTicketAction.UseTicket ? 0 : 1);
        Trace(action == AetheryteTicketAction.UseTicket
            ? "Accepted the Aetheryte ticket prompt."
            : "Declined the Aetheryte ticket prompt and continued with the gil teleport.");
        nextUiActionUtc = DateTime.UtcNow.AddMilliseconds(750);
        ApplyReactionDelayFloor(DateTime.UtcNow);
        return true;
    }

    public uint GetCityTeleportCost(CityTarget city)
    {
        if (!cityAetherytes.TryGetValue(city, out var id) || id == 0)
            return 0;

        var destination = DalamudServices.AetheryteList.FirstOrDefault(entry =>
            entry.AetheryteId == id && entry.SubIndex == 0);
        return destination?.GilCost ?? 0;
    }

    public void Abort()
    {
        ClearNavigation();
    }

    private bool TryApproachCityAetheryte()
    {
        var localPlayer = DalamudServices.ObjectTable.LocalPlayer;
        var currentTerritory = CurrentTerritoryName;
        var currentCity = GetCurrentCity();
        if (localPlayer is null || currentCity is null ||
            !cityAetherytes.TryGetValue(currentCity.Id, out var aetheryteId))
            return false;

        if (!IsInCity(currentCity.Id))
        {
            var teleportStarted = RequestCity(currentCity.Id);
            if (teleportStarted)
            {
                cityZoneTeleportPending = true;
                Trace($"Teleporting from {currentTerritory} to the main {currentCity.Name} Aetheryte zone before world travel.");
                nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
            }
            return teleportStarted;
        }
        cityZoneTeleportPending = false;

        var expectedNames = currentCity.AetheryteNames;
        var aetheryte = DalamudServices.ObjectTable
            .Where(gameObject => gameObject.ObjectKind == ObjectKind.Aetheryte)
            .Where(gameObject => gameObject.BaseId == aetheryteId ||
                                 expectedNames.Any(name => gameObject.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(gameObject => Vector3.DistanceSquared(localPlayer.Position, gameObject.Position))
            .FirstOrDefault();
        if (aetheryte is null)
            return false;

        DalamudServices.TargetManager.Target = aetheryte;
        var aetheryteObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)aetheryte.Address;
        var targetSystem = TargetSystem.Instance();
        if (aetheryteObject is null || targetSystem is null || !aetheryte.IsTargetable ||
            !TryFaceObject(localPlayer.Address, localPlayer.Position, aetheryte.Position))
            return false;
        targetSystem->SetHardTarget(aetheryteObject, true);
        var centerDistance = Vector3.Distance(localPlayer.Position, aetheryte.Position);
        var interactionDistance = MathF.Max(
            0f,
            centerDistance - MathF.Max(0f, aetheryte.HitboxRadius) - MathF.Max(0f, localPlayer.HitboxRadius));
        if (interactionDistance <= 4.75f)
        {
            StopAetheryteApproach();
            // The object identity, targetability, and interaction distance were
            // already validated above. The default native line-of-sight check can
            // reject an otherwise valid Aetheryte merely because it is outside the
            // camera view, producing "Cannot see target" despite a correct target.
            targetSystem->InteractWithObject(aetheryteObject, false);
            Trace($"Interacted directly with the validated object-table {currentCity.Name} Aetheryte at {interactionDistance:0.0} yalms from its hitbox ({centerDistance:0.0} center distance) without the camera line-of-sight gate; waiting for its menu.");
            nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
            return true;
        }

        if (aetheryteApproachActive)
        {
            nextUiActionUtc = DateTime.UtcNow.AddMilliseconds(250);
            return true;
        }

        // /lockon rejects targets outside the camera view even when Dalamud has
        // assigned the correct object-table target. Face the known world-space
        // position directly, then let automove advance while this method keeps
        // correcting the heading until the Aetheryte is in interaction range.
        if (!SendShellCommand("/automove"))
            return false;
        aetheryteApproachActive = true;
        Trace($"Hard-targeted and faced the object-table {currentCity.Name} Aetheryte; walking from {interactionDistance:0.0} yalms outside its hitbox ({centerDistance:0.0} center distance) without camera-dependent lock-on.");
        nextUiActionUtc = DateTime.UtcNow.AddMilliseconds(250);
        return true;
    }

    private static unsafe bool TryFaceObject(nint localPlayerAddress, Vector3 playerPosition, Vector3 targetPosition)
    {
        if (localPlayerAddress == 0)
            return false;
        var delta = targetPosition - playerPosition;
        if (delta.X * delta.X + delta.Z * delta.Z <= 0.0001f)
            return true;
        var playerObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)localPlayerAddress;
        playerObject->SetRotation(MathF.Atan2(delta.X, delta.Z));
        return true;
    }

    private void StopAetheryteApproach()
    {
        if (!aetheryteApproachActive)
            return;
        SendShellCommand("/automove");
        aetheryteApproachActive = false;
    }

    private void AdvanceDataCenterTravelMenus()
    {
        if (TryAcknowledgeOk()) return;
        if (returnHomeOnly &&
            TryGetRunCharacterCurrentWorld(out var homeLoginWorld) &&
            homeLoginWorld.Equals(runCharacterHomeWorld, StringComparison.OrdinalIgnoreCase) &&
            !returnHomeRequested)
        {
            if (TryConfirmCharacterLogin()) return;
            if (TryLoginSelectedCharacter()) return;
            return;
        }
        if (dataCenterArrivalAcknowledged)
        {
            if (TryConfirmCharacterLogin()) return;
            if (!TryGetRunCharacterCurrentWorld(out var destinationLoginWorld) ||
                string.IsNullOrWhiteSpace(destinationLoginWorld))
            {
                TraceThrottled(
                    "post-dc-lobby-world-loading",
                    $"The destination character screen has not published the saved character's current world yet. Waiting for {requestedWorld} before login. Addons: {DescribeLobbyAddons()}.",
                    TimeSpan.FromSeconds(3));
                return;
            }
            if (!destinationLoginWorld.Equals(requestedWorld, StringComparison.OrdinalIgnoreCase))
            {
                TraceThrottled(
                    "post-dc-wrong-lobby",
                    $"Refusing to log in because the visible character is on {destinationLoginWorld}, not the validated destination {requestedWorld}. Waiting for the game's automatic destination connection. Addons: {DescribeLobbyAddons()}.",
                    TimeSpan.FromSeconds(3));
                return;
            }
            if (TryLoginSelectedCharacter()) return;
            TraceThrottled(
                "post-dc-login-wait",
                $"Data-center travel completed. Waiting for the game to connect automatically to {requestedDataCenter} and display the {requestedWorld} character screen before logging in. Addons: {DescribeLobbyAddons()}.",
                TimeSpan.FromSeconds(3));
            return;
        }
        if (returnHomeRequested)
        {
            if (TryConfirmReturnHomeProceed()) return;
            if (TryConfirmReturnHomeTravel()) return;
            if (ConfirmVisibleDialog("return to home")) return;
            if (TryGetRunCharacterCurrentWorld(out var currentLoginWorld) &&
                currentLoginWorld.Equals(runCharacterHomeWorld, StringComparison.OrdinalIgnoreCase))
            {
                returnHomeRequested = false;
                returnHomeConfirmationSubmitted = false;
                returnHomeConfirmationSubmittedUtc = default;
                returnHomeProceedSubmitted = false;
                characterTravelMenuOpened = false;
                Trace($"Validated return to home world {runCharacterHomeWorld}; resuming travel to {requestedWorld} on {requestedDataCenter}.");
                if (returnHomeOnly)
                {
                    characterLoginSelected = false;
                    if (TryLoginSelectedCharacter()) return;
                    return;
                }
            }
            else
            {
                if (returnHomeConfirmationSubmitted &&
                    !returnHomeProceedSubmitted &&
                    returnHomeConfirmationSubmittedUtc != default &&
                    DateTime.UtcNow - returnHomeConfirmationSubmittedUtc >= TimeSpan.FromSeconds(30) &&
                    GetReadyAddon("_CharaSelectListMenu") is not null)
                {
                    Trace($"Return-home confirmation did not advance after 30 seconds while {currentLoginWorld} remained visible. Reopening the saved character's Return to Home World flow.");
                    returnHomeRequested = false;
                    returnHomeConfirmationSubmitted = false;
                    returnHomeConfirmationSubmittedUtc = default;
                    returnHomeProceedSubmitted = false;
                    characterTravelMenuOpened = false;
                    nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
                    return;
                }
                TraceThrottled(
                    "return-home-wait",
                    $"Waiting for {runCharacterName} to finish returning to home world {runCharacterHomeWorld}. Current login world: {currentLoginWorld}. Addons: {DescribeLobbyAddons()}.",
                    TimeSpan.FromSeconds(3));
                return;
            }
        }
        if (ConfirmVisibleDialog(requestedWorld)) return;
        if (TryConfirmDataCenterTravel()) return;
        if (TrySelectDataCenterDestination()) return;
        if (TryOpenDataCenterDestinationList()) return;
        if (TrySelectCharacterContextEntry()) return;
        if (TryOpenTitleStart()) return;
        if (!characterTravelMenuOpened && TryOpenCharacterSubcommand())
            return;
        TraceThrottled(
            "lobby-wait",
            $"Waiting for next lobby screen. Addons: {DescribeLobbyAddons()}. " +
            $"Flags: titleStart={titleStartSelected}, characterMenu={characterTravelMenuOpened}, " +
            $"dcSelected={dataCenterSelectionChosen}, worldSelected={worldSelectionChosen}, destinationConfirmed={dataCenterDestinationChosen}.",
            TimeSpan.FromSeconds(3));
    }

    private unsafe bool TryOpenTitleStart()
    {
        if (dataCenterProceedSubmitted || dataCenterArrivalAcknowledged)
        {
            TraceThrottled(
                "blocked-post-transfer-title-start",
                "Suppressed title-screen Start because data-center travel has already reached Proceed or arrival and the game must connect to the destination automatically.",
                TimeSpan.FromSeconds(3));
            return false;
        }
        if (titleStartSelected)
            return false;
        var addon = GetVisibleAddon("_TitleMenu");
        if (addon is null)
            addon = GetVisibleAddon("TitleMenu");
        if (addon is null)
            return false;

        // The title menu does not consistently publish its visible labels in
        // AtkValues, so do not gate this action on finding the word "Start".
        titleStartSelected = true;
        FireIntsAndClose(addon, 4);
        Trace("Selected Start on the title menu; waiting for character selection to become ready.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    private unsafe bool TryOpenCharacterSubcommand()
    {
        var addon = GetReadyAddon("_CharaSelectListMenu");
        if (addon is null)
            return false;
        if (!TryGetRunCharacterIndex(addon, out var characterIndex))
        {
            TraceThrottled(
                "character-match-context",
                $"Character-selection screen is ready, but the saved character could not be matched for its context menu. Requested: {runCharacterName}@{runCharacterHomeWorld}. Loaded: {DescribeLobbyCharacters()}.",
                TimeSpan.FromSeconds(3));
            return false;
        }
        // Keep the selected character bound to the run. The character that was
        // active when Start Run was pressed is selected before opening its menu.
        if (!RequestCharacterContextMenu(addon, characterIndex))
            return false;
        Trace($"Right-clicked the matched character at visible list position {characterIndex}: {runCharacterName}@{runCharacterHomeWorld}.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    private unsafe bool TryLoginSelectedCharacter()
    {
        if (characterLoginSelected)
            return false;
        var lobby = AgentLobby.Instance();
        if (lobby is null || lobby->TemporaryLocked)
            return false;
        var addon = GetReadyAddon("_CharaSelectListMenu");
        if (addon is null)
            return false;
        if (!TryGetRunCharacterIndex(addon, out var characterIndex))
        {
            TraceThrottled(
                "character-match-login",
                $"Character-selection screen is ready, but the saved character could not be matched. Requested: {runCharacterName}@{runCharacterHomeWorld}. Loaded: {DescribeLobbyCharacters()}.",
                TimeSpan.FromSeconds(3));
            return false;
        }
        characterLoginSelected = true;
        FireInts(addon, 29, 0, characterIndex);
        Trace($"Submitted login for the saved run character at visible list position {characterIndex}: {runCharacterName}@{runCharacterHomeWorld}.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(3);
        return true;
    }

    private unsafe bool TryConfirmCharacterLogin()
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon is null ||
            !AddonContainsTextStrict(addon, "log in with", "logging in with", "currently in a data center"))
            return false;
        addon->FireCallbackInt(0);
        Trace($"Accepted the character login confirmation for {runCharacterName}@{runCharacterHomeWorld}.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
        return true;
    }

    private unsafe bool TryAcknowledgeOk()
    {
        var addon = GetReadyAddon("SelectOk");
        if (addon is null)
            return false;
        if (AddonContains(addon, "players in queue", "currently congested", "server is congested"))
        {
            nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
            return true;
        }
        var isArrivalNotice = dataCenterProceedSubmitted &&
                              AddonContainsTextStrict(addon, "arrived safely", "has arrived");
        addon->FireCallbackInt(0);
        if (isArrivalNotice)
        {
            dataCenterArrivalAcknowledged = true;
            characterLoginSelected = false;
            titleStartSelected = false;
            Trace($"Acknowledged successful arrival on {requestedWorld} in {requestedDataCenter}; character login is now enabled.");
        }
        nextUiActionUtc = DateTime.UtcNow.AddMilliseconds(750);
        return true;
    }

    private unsafe bool ConfirmVisibleDialog(string expectedText)
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon is null || !AddonContains(addon, expectedText, "world", "travel", "log out", "proceed"))
            return false;
        addon->FireCallbackInt(0);
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
        return true;
    }

    private unsafe bool TrySelectCharacterContextEntry()
    {
        var addon = GetReadyAddon("ContextMenu");
        if (addon is null)
            return false;

        if (TryGetRunCharacterCurrentWorld(out var currentLoginWorld) &&
            !string.IsNullOrWhiteSpace(currentLoginWorld) &&
            !currentLoginWorld.Equals(runCharacterHomeWorld, StringComparison.OrdinalIgnoreCase))
        {
            const string returnLabel = "Return to Home World";
            var returnRowSource = "visible label";
            if (!TryFindEnabledExactListRow(addon, returnLabel, out _, out var returnContextRow))
            {
                if (TryFindContextMenuRowFromAtkValues(addon, returnLabel, out returnContextRow))
                {
                    returnRowSource = "normalized displayed option";
                }
                else
                {
                    TraceThrottled(
                        "return-home-context-row",
                        $"Character is visiting {currentLoginWorld}, but the visible character context menu has no exact enabled '{returnLabel}' row. Addon strings: {DescribeAddonStrings(addon)}.",
                        TimeSpan.FromSeconds(3));
                    return false;
                }
            }
            FireIntsAndClose(addon, 0, returnContextRow, 0);
            characterTravelMenuOpened = true;
            returnHomeRequested = true;
            returnHomeConfirmationSubmitted = false;
            returnHomeConfirmationSubmittedUtc = default;
            returnHomeProceedSubmitted = false;
            Trace($"Selected {returnRowSource} row {returnContextRow}: {returnLabel} for {runCharacterName}: {currentLoginWorld} -> {runCharacterHomeWorld}.");
            nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
            return true;
        }

        const string dataCenterLabel = "Visit Another Data Center";
        var contextRowSource = "visible label";
        if (!TryFindEnabledExactListRow(addon, dataCenterLabel, out _, out var contextRow))
        {
            if (TryFindContextMenuRowFromAtkValues(addon, dataCenterLabel, out contextRow))
            {
                contextRowSource = "normalized displayed option";
            }
            else
            {
                TraceThrottled(
                    "character-context-row",
                    $"Character context menu is visible, but it has no exact enabled '{dataCenterLabel}' row. Addon strings: {DescribeAddonStrings(addon)}.",
                    TimeSpan.FromSeconds(3));
                return false;
            }
        }
        FireIntsAndClose(addon, 0, contextRow, 0);
        Trace($"Selected exact {contextRowSource} character context row {contextRow}: {dataCenterLabel}.");
        characterTravelMenuOpened = true;
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    private unsafe bool TryOpenDataCenterDestinationList()
    {
        var addon = GetReadyAddon("LobbyDKTCheck");
        if (addon is null)
            return false;
        FireIntsAndClose(addon, 0);
        Trace("Advanced from the data-center travel introduction screen; waiting for destination lists.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
        return true;
    }

    private unsafe bool TrySelectDataCenterDestination()
    {
        if (dataCenterDestinationChosen)
            return false;
        var addon = GetReadyAddon("LobbyDKTWorldList");
        if (addon is null)
            return false;

        if (!dataCenterSelectionChosen)
        {
            var selectedDataCenterByRow = IsExactListRowSelected(addon, requestedDataCenter, out var selectedDataCenterRow);
            if (selectedDataCenterByRow || AddonStringValueMatches(addon, 152, requestedDataCenter))
            {
                dataCenterSelectionChosen = true;
                Trace(selectedDataCenterByRow
                    ? $"Validated selected data center from exact visible row {selectedDataCenterRow}: {requestedDataCenter}."
                    : $"Validated selected data center through the compatibility state value: {requestedDataCenter}.");
                nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
                return true;
            }
            if (!TryDispatchDestinationChoice(addon, true, requestedDataCenter, out var dataCenterItem))
            {
                TraceThrottled(
                    "dc-row",
                    $"Destination screen is visible and contains '{requestedDataCenter}', but its native selection event could not be dispatched. Addon strings: {DescribeAddonStrings(addon)}.",
                    TimeSpan.FromSeconds(3));
                return false;
            }
            Trace($"Selected exact enabled data-center tree row {dataCenterItem}: {requestedDataCenter}; waiting for the list selection to update.");
            nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
            return true;
        }
        if (!worldSelectionChosen)
        {
            var selectedWorldByRow = IsExactListRowSelected(addon, requestedWorld, out var selectedWorldRow);
            if (selectedWorldByRow || AddonStringValueMatches(addon, 10, requestedWorld))
            {
                worldSelectionChosen = true;
                Trace(selectedWorldByRow
                    ? $"Validated selected destination world from exact visible row {selectedWorldRow}: {requestedWorld}."
                    : $"Validated selected destination world through the compatibility state value: {requestedWorld}.");
                nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
                return true;
            }
            if (!TryDispatchDestinationChoice(addon, false, requestedWorld, out var worldItem))
            {
                TraceThrottled(
                    "dc-world-row",
                    $"Data center {requestedDataCenter} is selected and contains '{requestedWorld}', but its native selection event could not be dispatched. Addon strings: {DescribeAddonStrings(addon)}.",
                    TimeSpan.FromSeconds(3));
                return false;
            }
            Trace($"Selected exact enabled world row {worldItem}: {requestedWorld}; waiting for the list selection to update.");
            nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
            return true;
        }

        var dataCenterSelectionValidated = IsExactListRowSelected(addon, requestedDataCenter, out _) ||
                                           AddonStringValueMatches(addon, 152, requestedDataCenter);
        var worldSelectionValidated = IsExactListRowSelected(addon, requestedWorld, out _) ||
                                      AddonStringValueMatches(addon, 10, requestedWorld);
        if (!dataCenterSelectionValidated || !worldSelectionValidated)
        {
            dataCenterSelectionChosen = false;
            worldSelectionChosen = false;
            nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
            return true;
        }
        FireIntsAndClose(addon, 4);
        dataCenterDestinationChosen = true;
        Trace($"Confirmed validated destination: {requestedWorld} on {requestedDataCenter}.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(1);
        return true;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    private struct DestinationChoiceState
    {
        [FieldOffset(4)] public byte IsReady;
        [FieldOffset(8)] public int PackedSelection;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    private unsafe struct DestinationChoicePayload
    {
        [FieldOffset(8)] public nint* ChoiceStatePointer;
        [FieldOffset(16)] public int HighlightedItem;
    }

    private static unsafe bool TryDispatchDestinationChoice(
        AtkUnitBase* addon,
        bool dataCenterChoice,
        string expectedText,
        out int selectedItem)
    {
        selectedItem = -1;
        if (addon is null)
            return false;

        var category = 0;
        var itemWithinCategory = 0;
        var highlightedItem = -1;
        AtkComponentList* matchedList = null;
        if (TryFindEnabledExactListRow(addon, expectedText, out matchedList, out var matchedRow))
            highlightedItem = matchedRow;
        if (dataCenterChoice)
        {
            var matchingWorld = WorldCatalog.Worlds.FirstOrDefault(world =>
                world.DataCenter.Equals(expectedText, StringComparison.OrdinalIgnoreCase));
            if (matchingWorld is null)
                return false;
            category = (int)matchingWorld.Region;
            var regionDataCenters = WorldCatalog.Worlds
                .Where(world => world.Region == matchingWorld.Region)
                .Select(world => world.DataCenter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            itemWithinCategory = Array.FindIndex(regionDataCenters, dataCenter =>
                dataCenter.Equals(expectedText, StringComparison.OrdinalIgnoreCase)) + 1;
        }
        else
        {
            var matchingWorld = WorldCatalog.FindWorld(expectedText);
            if (matchingWorld is null)
                return false;
            var dataCenterWorlds = WorldCatalog.Worlds
                .Where(world => world.DataCenter.Equals(matchingWorld.DataCenter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            itemWithinCategory = Array.FindIndex(dataCenterWorlds, world =>
                world.Name.Equals(expectedText, StringComparison.OrdinalIgnoreCase)) + 1;
        }
        if (itemWithinCategory <= 0)
            return false;

        if (matchedList is null &&
            !TryResolveLegacyDestinationChoice(addon, dataCenterChoice, expectedText, out category, out itemWithinCategory, out highlightedItem))
            return false;

        AtkComponentBase* component;
        AtkResNode* targetNode;
        if (matchedList is not null)
        {
            component = (AtkComponentBase*)matchedList;
            targetNode = (AtkResNode*)component->OwnerNode;
        }
        else
        {
            var targetNodeIndex = dataCenterChoice ? 8 : 7;
            if (addon->UldManager.NodeList is null || targetNodeIndex >= addon->UldManager.NodeListCount)
                return false;
            targetNode = addon->UldManager.NodeList[targetNodeIndex];
            component = targetNode is null ? null : ((AtkComponentNode*)targetNode)->Component;
        }
        if (targetNode is null || component is null)
            return false;

        var choiceState = new DestinationChoiceState
        {
            IsReady = 1,
            PackedSelection = itemWithinCategory - 1 + (category << 8),
        };
        var choiceStateAddress = (nint)(&choiceState);
        var payload = new DestinationChoicePayload
        {
            ChoiceStatePointer = &choiceStateAddress,
            HighlightedItem = highlightedItem,
        };
        var selectionEvent = new AtkEvent
        {
            Target = (AtkEventTarget*)targetNode,
            Listener = &component->AtkEventListener,
            Param = 1,
            State = new AtkEventState { EventType = AtkEventType.ListItemClick },
        };
        addon->ReceiveEvent(
            AtkEventType.ListItemClick,
            dataCenterChoice ? 1 : 2,
            &selectionEvent,
            (AtkEventData*)&payload);
        ((AtkComponentList*)component)->SelectItem(highlightedItem, false);
        selectedItem = highlightedItem;
        return true;
    }

    private static unsafe bool TryResolveLegacyDestinationChoice(
        AtkUnitBase* addon,
        bool dataCenterChoice,
        string expectedText,
        out int category,
        out int itemWithinCategory,
        out int highlightedItem)
    {
        category = 0;
        itemWithinCategory = 0;
        highlightedItem = 0;
        if (addon->AtkValues is null)
            return false;
        if (dataCenterChoice)
        {
            var flattenedItem = 0;
            for (var region = 0; region < 4; region++)
            {
                flattenedItem++;
                for (var item = 0; item < 4; item++)
                {
                    var valueIndex = 17 + region * 34 + item * 8;
                    if (AddonStringValueMatches(addon, valueIndex, expectedText))
                    {
                        category = region;
                        itemWithinCategory = item + 1;
                        highlightedItem = flattenedItem;
                        return true;
                    }
                    flattenedItem++;
                }
            }
            return false;
        }

        for (var item = 0; item < 8; item++)
        {
            var valueIndex = 155 + item * 8;
            if (!AddonStringValueMatches(addon, valueIndex, expectedText))
                continue;
            itemWithinCategory = item + 1;
            highlightedItem = item + 1;
            return true;
        }
        return false;
    }

    private unsafe bool TryConfirmDataCenterTravel()
    {
        if (dataCenterProceedSubmitted)
            return false;
        var addon = GetReadyAddon("LobbyDKTCheckExec");
        if (addon is null)
            return false;
        FireIntsAndClose(addon, 0);
        dataCenterDestinationChosen = true;
        dataCenterProceedSubmitted = true;
        Trace("Selected Proceed on the final data-center travel confirmation.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    private unsafe bool TryConfirmReturnHomeTravel()
    {
        if (returnHomeConfirmationSubmitted)
            return false;
        var addon = GetReadyAddon("LobbyDKTWorldList");
        if (addon is null)
            return false;
        FireIntsAndClose(addon, 4);
        returnHomeConfirmationSubmitted = true;
        returnHomeConfirmationSubmittedUtc = DateTime.UtcNow;
        Trace($"Confirmed return to home data center and home world {runCharacterHomeWorld}.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    private unsafe bool TryConfirmReturnHomeProceed()
    {
        if (!returnHomeConfirmationSubmitted || returnHomeProceedSubmitted)
            return false;
        var addon = GetReadyAddon("LobbyDKTCheckExec");
        if (addon is null)
            return false;
        FireIntsAndClose(addon, 0);
        returnHomeProceedSubmitted = true;
        Trace($"Selected Proceed for the validated return to home world {runCharacterHomeWorld}.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    private unsafe bool TryGetRunCharacterIndex(AtkUnitBase* addon, out int characterIndex)
    {
        characterIndex = -1;
        if (string.IsNullOrWhiteSpace(runCharacterName))
            return false;

        var lobby = AgentLobby.Instance();
        if (lobby is not null)
        {
            var entries = lobby->LobbyData.CharaSelectEntries;
            for (var index = 0; index < entries.Count; index++)
            {
                CharaSelectCharacterEntry* candidate = entries[index];
                if (candidate is null ||
                    !candidate->NameString.Equals(runCharacterName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(runCharacterHomeWorld) &&
                     !candidate->HomeWorldNameString.Equals(runCharacterHomeWorld, StringComparison.OrdinalIgnoreCase)))
                    continue;
                characterIndex = index;
                Trace(
                    $"Matched saved character at visible list position {characterIndex} " +
                    $"(internal entry index {candidate->Index}, selected list position {lobby->SelectedCharacterIndex}): " +
                    $"{candidate->NameString}@{candidate->HomeWorldNameString}.");
                return true;
            }

            var selectedIndex = lobby->SelectedCharacterIndex;
            var entry = lobby->LobbyData.GetCharacterEntryFromServer(selectedIndex, lobby->SelectedCharacterContentId);
            if (entry is not null &&
                entry->NameString.Equals(runCharacterName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(runCharacterHomeWorld) ||
                 entry->HomeWorldNameString.Equals(runCharacterHomeWorld, StringComparison.OrdinalIgnoreCase)))
            {
                characterIndex = selectedIndex;
                Trace($"Matched saved character through selected lobby entry at index {characterIndex}.");
                return true;
            }
        }

        // Never continue on an identity mismatch. This fallback is only for the
        // brief frame where lobby character records are still populating.
        if (!AddonContains(addon, runCharacterName) ||
            (!string.IsNullOrWhiteSpace(runCharacterHomeWorld) && !AddonContains(addon, runCharacterHomeWorld)))
            return false;
        if (lobby is null)
            return false;
        characterIndex = lobby->SelectedCharacterIndex;
        return true;
    }

    private unsafe bool TryGetRunCharacterCurrentWorld(out string currentWorld)
    {
        currentWorld = string.Empty;
        var lobby = AgentLobby.Instance();
        if (lobby is null)
            return false;
        var entries = lobby->LobbyData.CharaSelectEntries;
        for (var index = 0; index < entries.Count; index++)
        {
            CharaSelectCharacterEntry* candidate = entries[index];
            if (candidate is null ||
                !candidate->NameString.Equals(runCharacterName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(runCharacterHomeWorld) &&
                 !candidate->HomeWorldNameString.Equals(runCharacterHomeWorld, StringComparison.OrdinalIgnoreCase)))
                continue;
            currentWorld = candidate->CurrentWorldNameString;
            return !string.IsNullOrWhiteSpace(currentWorld);
        }
        return false;
    }

    private unsafe bool TrySelectString(string label)
    {
        var addon = GetReadyAddon("SelectString");
        if (addon is null)
            return false;
        var rowSource = "visible label";
        if (!TryFindEnabledExactListRow(addon, label, out _, out var menuRow))
        {
            if (TryFindSelectStringRowFromAtkValues(addon, label, out menuRow))
            {
                rowSource = "normalized displayed option";
            }
            else
            {
                TraceThrottled(
                    $"select-string-{label}",
                    $"SelectString is open, but it has no exact enabled visible '{label}' row. Addon strings: {DescribeAddonStrings(addon)}.",
                    TimeSpan.FromSeconds(3));
                return false;
            }
        }
        FireIntsAndClose(addon, menuRow);
        Trace($"Selected exact {rowSource} SelectString row {menuRow}: {label}.");
        nextUiActionUtc = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    private static unsafe bool TryFindSelectStringRowFromAtkValues(
        AtkUnitBase* addon,
        string expectedLabel,
        out int menuRow)
    {
        menuRow = -1;
        if (addon is null || addon->AtkValues is null)
            return false;

        // SelectString publishes its prompt before index 7 and its displayed
        // options from index 7 onward. Count the actual non-empty option strings
        // so optional entries may be inserted or removed without changing which
        // callback row is selected.
        var displayedRow = 0;
        for (var valueIndex = 7; valueIndex < addon->AtkValuesCount; valueIndex++)
        {
            var value = addon->AtkValues[valueIndex];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
                continue;
            var text = value.GetValueAsString();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (MenuLabelMatches(text, expectedLabel))
            {
                menuRow = displayedRow;
                return true;
            }
            displayedRow++;
        }
        return false;
    }

    private static unsafe bool TryFindContextMenuRowFromAtkValues(
        AtkUnitBase* addon,
        string expectedLabel,
        out int menuRow)
    {
        menuRow = -1;
        if (addon is null || addon->AtkValues is null)
            return false;

        // ContextMenu publishes one option label per value from index 8 onward;
        // the callback row is its position relative to that first option. Resolve
        // the current label index instead of assuming a particular menu layout.
        const int firstOptionValueIndex = 8;
        for (var valueIndex = firstOptionValueIndex; valueIndex < addon->AtkValuesCount; valueIndex++)
        {
            var value = addon->AtkValues[valueIndex];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
                continue;
            if (!MenuLabelMatches(value.GetValueAsString(), expectedLabel))
                continue;
            menuRow = valueIndex - firstOptionValueIndex;
            return true;
        }
        return false;
    }

    private static unsafe bool AddonStringValueMatches(
        AtkUnitBase* addon,
        int valueIndex,
        string expectedLabel)
    {
        if (addon is null || addon->AtkValues is null ||
            valueIndex < 0 || valueIndex >= addon->AtkValuesCount)
            return false;
        var value = addon->AtkValues[valueIndex];
        if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
            return false;
        return MenuLabelMatches(value.GetValueAsString(), expectedLabel);
    }

    private static unsafe bool RequestCharacterContextMenu(AtkUnitBase* addon, int visiblePosition)
    {
        var stage = AtkStage.Instance();
        if (addon is null || stage is null || visiblePosition is < 0 or > 250)
            return false;

        var eventParameter = checked((byte)(5 + visiblePosition));
        var clickEvent = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &stage->AtkEventTarget,
            State = new AtkEventState
            {
                StateFlags = (AtkEventStateFlags)eventParameter,
            },
        };
        var clickData = new AtkEventData();
        *((byte*)&clickData + 6) = 1;
        addon->ReceiveEvent(AtkEventType.MouseClick, eventParameter, &clickEvent, &clickData);
        return true;
    }

    private static unsafe bool TryFindExactListRow(
        AtkUnitBase* addon,
        string expectedLabel,
        out AtkComponentList* matchedList,
        out int matchedRow)
    {
        matchedList = null;
        matchedRow = -1;
        if (addon is null || addon->UldManager.NodeList is null)
            return false;

        var visited = new HashSet<nint>();
        for (var index = 0; index < addon->UldManager.NodeListCount; index++)
        {
            if (TryFindExactListRow(
                    addon->UldManager.NodeList[index],
                    expectedLabel,
                    visited,
                    0,
                    out matchedList,
                    out matchedRow))
                return true;
        }
        return false;
    }

    private static unsafe bool IsExactListRowSelected(
        AtkUnitBase* addon,
        string expectedLabel,
        out int matchedRow)
    {
        if (!TryFindExactListRow(addon, expectedLabel, out var matchedList, out matchedRow))
            return false;
        return matchedList->SelectedItemIndex == matchedRow;
    }

    private static unsafe bool TryFindEnabledExactListRow(
        AtkUnitBase* addon,
        string expectedLabel,
        out AtkComponentList* matchedList,
        out int matchedRow)
    {
        if (!TryFindExactListRow(addon, expectedLabel, out matchedList, out matchedRow))
            return false;
        return matchedRow >= 0 && matchedRow < matchedList->ListLength &&
               !matchedList->GetItemDisabledState(matchedRow);
    }

    private static unsafe bool TryFindExactListRow(
        AtkResNode* node,
        string expectedLabel,
        HashSet<nint> visited,
        int depth,
        out AtkComponentList* matchedList,
        out int matchedRow)
    {
        matchedList = null;
        matchedRow = -1;
        if (node is null || depth > 40 || !visited.Add((nint)node))
            return false;

        if (node->Type == NodeType.Component)
        {
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component is not null)
            {
                if (componentNode->Component->GetComponentType() is ComponentType.List or ComponentType.TreeList)
                {
                    var list = (AtkComponentList*)componentNode->Component;
                    var listLength = Math.Clamp(list->ListLength, 0, 256);
                    for (var listRow = 0; listRow < listLength; listRow++)
                    {
                        if (!MenuLabelMatches(list->GetItemLabel(listRow).ToString(), expectedLabel))
                            continue;
                        matchedList = list;
                        matchedRow = listRow;
                        return true;
                    }

                    var rendererCount = Math.Clamp(list->AllocatedItemRendererListLength, 0, 128);
                    for (var rendererIndex = 0; rendererIndex < rendererCount; rendererIndex++)
                    {
                        var renderer = list->ItemRendererList[rendererIndex].AtkComponentListItemRenderer;
                        if (renderer is null)
                            continue;
                        if (!RendererContainsExactText(renderer, expectedLabel))
                            continue;
                        matchedList = list;
                        matchedRow = renderer->ListItemIndex >= 0
                            ? renderer->ListItemIndex
                            : list->FirstVisibleItemIndex + rendererIndex;
                        return true;
                    }
                }

                var manager = componentNode->Component->UldManager;
                if (manager.NodeList is not null)
                {
                    for (var index = 0; index < manager.NodeListCount; index++)
                    {
                        if (TryFindExactListRow(
                                manager.NodeList[index], expectedLabel, visited, depth + 1,
                                out matchedList, out matchedRow))
                            return true;
                    }
                }
            }
        }

        if (TryFindExactListRow(node->ChildNode, expectedLabel, visited, depth + 1, out matchedList, out matchedRow))
            return true;
        return TryFindExactListRow(node->NextSiblingNode, expectedLabel, visited, depth, out matchedList, out matchedRow);
    }

    private static unsafe bool RendererContainsExactText(
        AtkComponentListItemRenderer* renderer,
        string expectedLabel)
    {
        var visited = new HashSet<nint>();
        var nodeCount = Math.Clamp((int)renderer->RowTemplateNodeCountByte, 0, 64);
        if (nodeCount == 1 && renderer->RowTemplateNode is not null)
            return NodeContainsExactText(renderer->RowTemplateNode, expectedLabel, visited, 0);
        if (renderer->RowTemplateNodeList is null)
            return false;
        for (var index = 0; index < nodeCount; index++)
        {
            if (NodeContainsExactText(renderer->RowTemplateNodeList[index], expectedLabel, visited, 0))
                return true;
        }
        return false;
    }

    private static unsafe bool NodeContainsExactText(
        AtkResNode* node,
        string expectedLabel,
        HashSet<nint> visited,
        int depth)
    {
        if (node is null || depth > 24 || !visited.Add((nint)node))
            return false;
        if (node->Type == NodeType.Text &&
            MenuLabelMatches(((AtkTextNode*)node)->NodeText.ToString(), expectedLabel))
            return true;
        if (node->Type == NodeType.Component)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component is not null && component->UldManager.NodeList is not null)
            {
                for (var index = 0; index < component->UldManager.NodeListCount; index++)
                {
                    if (NodeContainsExactText(component->UldManager.NodeList[index], expectedLabel, visited, depth + 1))
                        return true;
                }
            }
        }
        if (NodeContainsExactText(node->ChildNode, expectedLabel, visited, depth + 1))
            return true;
        return NodeContainsExactText(node->NextSiblingNode, expectedLabel, visited, depth);
    }

    private static bool MenuLabelMatches(string actual, string expected)
    {
        actual = NormalizeMenuText(actual);
        expected = NormalizeMenuText(expected);
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return true;
        if (actual.Length == 0 || expected.Length == 0)
            return false;

        var matchIndex = actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase);
        while (matchIndex >= 0)
        {
            var beforeIsBoundary = matchIndex == 0 || !char.IsLetterOrDigit(actual[matchIndex - 1]);
            var afterIndex = matchIndex + expected.Length;
            var afterIsBoundary = afterIndex == actual.Length || !char.IsLetterOrDigit(actual[afterIndex]);
            if (beforeIsBoundary && afterIsBoundary)
                return true;
            matchIndex = actual.IndexOf(expected, matchIndex + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string NormalizeMenuText(string? value)
    {
        value ??= string.Empty;

        // FFXIV menu labels may begin with a SeString icon payload. When the
        // payload is exposed as plain text it includes control bytes and a small
        // marker such as F/E. Keep only the visible text after the final control
        // byte, then ignore the punctuation the game appends to menu labels.
        var finalControlIndex = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]))
                finalControlIndex = index;
        }
        if (finalControlIndex >= 0 && finalControlIndex + 1 < value.Length)
            value = value[(finalControlIndex + 1)..];

        value = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return string.Join(' ', value
            .Trim()
            .TrimEnd('.', '…')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private unsafe string DescribeLobbyAddons()
    {
        string[] names =
        [
            "_TitleMenu", "_CharaSelectWorldServer", "_CharaSelectListMenu", "ContextMenu",
            "LobbyDKTCheck", "LobbyDKTWorldList", "LobbyDKTCheckExec", "LobbyWKTCheckHome",
            "SelectString", "SelectOk", "SelectYesno",
        ];
        return string.Join(", ", names.Select(name => $"{name}={(GetVisibleAddon(name) is null ? "off" : "visible")}"));
    }

    private unsafe string DescribeLobbyCharacters()
    {
        var lobby = AgentLobby.Instance();
        if (lobby is null)
            return "AgentLobby unavailable";
        var entries = lobby->LobbyData.CharaSelectEntries;
        if (entries.Count == 0)
            return $"no lobby entries; selectedIndex={lobby->SelectedCharacterIndex}, selectedContentId={lobby->SelectedCharacterContentId}";
        var values = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            CharaSelectCharacterEntry* entry = entries[index];
            values.Add(entry is null
                ? $"[{index}]=null"
                : $"[{index}/entryIndex={entry->Index}]={entry->NameString}@{entry->HomeWorldNameString} current={entry->CurrentWorldNameString}");
        }
        return string.Join("; ", values);
    }

    private static unsafe string DescribeAddonStrings(AtkUnitBase* addon)
    {
        if (addon is null || addon->AtkValues is null)
            return "none";
        var values = new List<string>();
        for (var index = 0; index < addon->AtkValuesCount; index++)
        {
            var value = addon->AtkValues[index];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
                continue;
            var text = value.GetValueAsString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                values.Add($"[{index}]='{text.Replace("\r", " ").Replace("\n", " ")}'");
        }
        return values.Count == 0 ? "no string values" : string.Join(", ", values);
    }

    private void TraceThrottled(string key, string message, TimeSpan interval)
    {
        var now = DateTime.UtcNow;
        if (debugThrottle.TryGetValue(key, out var previous) && now - previous < interval)
            return;
        debugThrottle[key] = now;
        Trace(message);
    }

    private void Trace(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (debugLogLock)
        {
            debugLog.Add(line);
            if (debugLog.Count > 1000)
                debugLog.RemoveRange(0, debugLog.Count - 1000);
        }
        DalamudServices.Log.Debug("[ShoutRunner Travel] {Message}", message);
    }

    private unsafe bool TrySelectWorldVisitDestination()
    {
        var addon = GetReadyAddon("WorldTravelSelect");
        if (addon is null)
            return false;

        var rowSource = "visible label";
        if (!TryFindEnabledExactListRow(addon, requestedWorld, out _, out var destinationRow))
        {
            var destination = WorldCatalog.FindWorld(requestedWorld);
            var displayedWorlds = destination is null
                ? Array.Empty<WorldDefinition>()
                : WorldCatalog.Worlds
                    .Where(world => world.DataCenter.Equals(destination.DataCenter, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            var destinationIndex = Array.FindIndex(displayedWorlds, world =>
                world.Name.Equals(requestedWorld, StringComparison.OrdinalIgnoreCase));
            var expectedCurrentWorldSummary = $"{CurrentWorld} [{requestedDataCenter}]";
            if (destinationIndex < 0 ||
                !AddonContainsTextStrict(addon, expectedCurrentWorldSummary))
            {
                TraceThrottled(
                    "world-visit-destination-row",
                    $"World Visit destination list is visible, but neither an exact enabled '{requestedWorld}' row nor the expected current-world summary '{expectedCurrentWorldSummary}' could be validated. Addon strings: {DescribeAddonStrings(addon)}.",
                    TimeSpan.FromSeconds(3));
                return false;
            }
            destinationRow = destinationIndex + 2;
            rowSource = "validated compatibility mapping";
        }

        FireInts(addon, 0, destinationRow);
        Trace($"Selected exact {rowSource} World Visit row {destinationRow}: {requestedWorld}.");
        nextUiActionUtc = DateTime.UtcNow.AddMilliseconds(750);
        return true;
    }

    private unsafe AtkUnitBase* GetReadyAddon(string name)
    {
        var pointer = DalamudServices.GameGui.GetAddonByName(name);
        var addon = (AtkUnitBase*)pointer.Address;
        return addon is not null && addon->IsVisible && addon->IsReady ? addon : null;
    }

    private unsafe AtkUnitBase* GetVisibleAddon(string name)
    {
        var pointer = DalamudServices.GameGui.GetAddonByName(name);
        var addon = (AtkUnitBase*)pointer.Address;
        return addon is not null && addon->IsVisible ? addon : null;
    }

    private static unsafe int FindStringIndex(AtkUnitBase* addon, string label)
    {
        if (addon->AtkValues is null)
            return -1;
        var visibleIndex = 0;
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
                continue;
            var text = value.GetValueAsString();
            if (text.Equals(label, StringComparison.OrdinalIgnoreCase) || text.Contains(label, StringComparison.OrdinalIgnoreCase))
                return visibleIndex;
            if (!string.IsNullOrWhiteSpace(text))
                visibleIndex++;
        }
        return -1;
    }

    private static unsafe bool AddonContains(AtkUnitBase* addon, params string[] expected)
    {
        if (addon->AtkValues is null)
            return true;
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
                continue;
            var text = value.GetValueAsString();
            if (expected.Any(item => text.Contains(item, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static unsafe bool AddonContainsTextStrict(AtkUnitBase* addon, params string[] expected)
    {
        if (addon is null || addon->AtkValues is null)
            return false;
        for (var index = 0; index < addon->AtkValuesCount; index++)
        {
            var value = addon->AtkValues[index];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
                continue;
            var text = value.GetValueAsString();
            if (expected.Any(item => text.Contains(item, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static unsafe void FireInts(AtkUnitBase* addon, params int[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i] = new AtkValue();
            atkValues[i].SetInt(values[i]);
        }
        addon->FireCallback((uint)values.Length, atkValues);
    }

    private static unsafe void FireIntsAndClose(AtkUnitBase* addon, params int[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i] = new AtkValue();
            atkValues[i].SetInt(values[i]);
        }
        addon->FireCallback((uint)values.Length, atkValues, true);
    }

    private unsafe bool SendShellCommand(string command)
    {
        using var value = new Utf8String(command);
        var shell = RaptureShellModule.Instance();
        var ui = UIModule.Instance();
        if (shell is null || ui is null)
            return false;
        shell->ExecuteCommandInner(&value, ui);
        return true;
    }

    private void ClearNavigation()
    {
        StopAetheryteApproach();
        requestedWorld = string.Empty;
        requestedDataCenter = string.Empty;
        requestStartedUtc = default;
        nextUiActionUtc = default;
        logoutRequested = false;
        characterTravelMenuOpened = false;
        dataCenterDestinationChosen = false;
        dataCenterProceedSubmitted = false;
        dataCenterArrivalAcknowledged = false;
        dataCenterSelectionChosen = false;
        worldSelectionChosen = false;
        titleStartSelected = false;
        characterLoginSelected = false;
        worldVisitSubmitted = false;
        cityZoneTeleportPending = false;
        returnHomeRequested = false;
        returnHomeConfirmationSubmitted = false;
        returnHomeConfirmationSubmittedUtc = default;
        returnHomeProceedSubmitted = false;
        returnHomeOnly = false;
        returningHomeViaAetheryte = false;
        queuedDataCenterWorld = string.Empty;
        queuedDataCenterName = string.Empty;
    }

    private void ApplyReactionDelayFloor(DateTime actionCycleUtc)
    {
        if (generalReactionDelaySeconds <= 0)
            return;
        var reactionNotBefore = actionCycleUtc.AddSeconds(generalReactionDelaySeconds);
        if (nextUiActionUtc < reactionNotBefore)
            nextUiActionUtc = reactionNotBefore;
    }

    private void ResolveCityAetherytes()
    {
        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<Aetheryte>();
            foreach (var city in WorldCatalog.Cities)
            {
                var row = sheet.FirstOrDefault(candidate =>
                    city.AetheryteNames.Any(name =>
                        candidate.PlaceName.Value.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)));
                if (row.RowId != 0)
                    cityAetherytes[city.Id] = row.RowId;
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShoutRunner could not resolve city aetherytes.");
        }
    }
}

internal sealed class RunService : IDisposable
{
    private readonly PersistenceService persistence;
    private readonly TravelService travel;
    private readonly ChatCommandService chat;
    private readonly CancellationTokenSource disposeToken = new();
    private PersistedRunState? state;
    private Task<bool>? pendingMessage;
    private bool foregroundRequested;

    public RunService(PersistenceService persistence, TravelService travel, ChatCommandService chat)
    {
        this.persistence = persistence;
        this.travel = travel;
        this.chat = chat;
        state = persistence.LoadRunState();
        if (state is { Phase: RunPhase.Idle })
            state = null;
        if (state is not null)
        {
            state.SkippedStopIndexes ??= [];
            state.RunId = string.IsNullOrWhiteSpace(state.RunId) ? Guid.NewGuid().ToString("N") : state.RunId;
            state.StartedUtc = state.StartedUtc == default ? DateTime.UtcNow : state.StartedUtc;
            state.CharacterName = string.IsNullOrWhiteSpace(state.CharacterName) ? travel.CharacterName : state.CharacterName;
            state.CharacterHomeWorld = string.IsNullOrWhiteSpace(state.CharacterHomeWorld) ? travel.HomeWorld : state.CharacterHomeWorld;
            state.StartingWorld = string.IsNullOrWhiteSpace(state.StartingWorld)
                ? persistence.LastCharacterCurrentWorld
                : state.StartingWorld;
            if (state.ReturnHomeAfterRun)
            {
                state.PostRunDestination = PostRunDestination.HomeWorld;
                state.PostRunWorld = state.CharacterHomeWorld;
                state.ReturnHomeAfterRun = false;
            }
            if (state.Phase == RunPhase.Paused && state.PausedPhase == RunPhase.Idle)
                state.PausedPhase = RunPhase.Preparing;
            foregroundRequested = true;
        }
    }

    public RunPhase Phase => state?.Phase ?? RunPhase.Idle;
    public string Status => state?.Status ?? "Ready to start a new run.";
    public bool IsRunning => state is not null && state.Phase is not (RunPhase.Idle or RunPhase.Completed or RunPhase.Failed);
    public bool IsPaused => state?.Phase == RunPhase.Paused;
    public bool KeepTabletVisibleDuringTravel =>
        state is not null &&
        IsRunning &&
        (!DalamudServices.ClientState.IsLoggedIn ||
         DalamudServices.ObjectTable.LocalPlayer is null ||
         state.AwaitingInitialLogin ||
         state.Phase is RunPhase.TravelingDataCenter or RunPhase.TravelingWorld or RunPhase.TravelingCity or RunPhase.WaitingForArrival or RunPhase.ReturningHome);
    public int CompletedStops => state is null ? 0 : Math.Clamp(state.StopIndex, 0, state.Route.Count);
    public int SkippedStopCount => state?.SkippedStopIndexes.Count ?? 0;
    public int SuccessfulStopCount => Math.Max(0, CompletedStops - SkippedStopCount);
    public int TotalStops => state?.Route.Count ?? 0;
    public double ReceiptPausedSeconds => state?.TotalPausedSeconds ?? 0d;
    public IReadOnlyList<RouteStop> Route => state?.Route ?? [];
    public RouteStop? CurrentStop => state is { StopIndex: >= 0 } value && value.StopIndex < value.Route.Count ? value.Route[value.StopIndex] : null;
    public string ReceiptCharacter => state is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(state.CharacterHomeWorld)
            ? state.CharacterName
            : $"{state.CharacterName} @ {state.CharacterHomeWorld}";
    public DateTime ReceiptCompletedUtc => state?.CompletedUtc ?? default;
    public DateTime ReceiptStartedUtc => state?.StartedUtc ?? default;
    public ulong TeleportGilSpent => state?.TeleportGilSpent ?? 0;
    public string ReceiptCode => state?.ReceiptCode ?? string.Empty;
    public string ReceiptRunId => state?.RunId ?? string.Empty;
    public int ReceiptWorldCount => SuccessfulReceiptStops.Select(item => item.stop.World).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int ReceiptDataCenterCount => SuccessfulReceiptStops.Select(item => item.stop.DataCenter).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public bool IsStopSkipped(int index) => state?.SkippedStopIndexes.Contains(index) == true;

    public string CurrentTask => state?.Phase switch
    {
        RunPhase.Preparing => CurrentStop is { } stop ? $"Preparing {stop.World}" : "Preparing route",
        RunPhase.TravelingDataCenter => CurrentStop is { } stop ? $"Transferring to {stop.World}" : "Changing Data Centers",
        RunPhase.TravelingWorld when travel.IsCityZoneTeleportPending => "Loading",
        RunPhase.TravelingWorld => CurrentStop is { } stop ? $"Changing world to {stop.World}" : "Changing worlds",
        RunPhase.TravelingCity => CurrentStop is { } stop ? $"Teleporting to {stop.CityName}" : "Teleporting",
        RunPhase.WaitingForArrival => "Loading",
        RunPhase.SendingMessages => CurrentStop is { } stop ? $"Sending messages in {stop.CityName}" : "Sending messages",
        RunPhase.ReturningHome => state is null ? "Returning after run" : $"Returning to {state.PostRunWorld}",
        RunPhase.Paused => "Run paused",
        RunPhase.Completed => "Run complete",
        RunPhase.Failed => "Run stopped",
        _ => "Ready",
    };

    private IEnumerable<(RouteStop stop, int index)> SuccessfulReceiptStops => state?.Route
        .Select((stop, index) => (stop, index))
        .Where(item => !state.SkippedStopIndexes.Contains(item.index)) ?? [];

    public int GetConfiguredTotalStops(VenueProfile profile, string? homeWorld)
    {
        var allowedWorlds = WorldCatalog.VisibleWorlds(homeWorld, profile.DeveloperMode)
            .Select(world => world.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profile.Worlds.Count(allowedWorlds.Contains) * profile.Cities.Count;
    }

    public bool Start(VenueProfile profile, out string error)
    {
        EnsureDefaultWorldSelection(profile, string.IsNullOrWhiteSpace(travel.HomeWorld)
            ? persistence.LastCharacterHomeWorld
            : travel.HomeWorld);
        profile.Normalize();
        if (profile.Messages.Count == 0 || profile.Messages.All(block => string.IsNullOrWhiteSpace(block.Text)))
        {
            error = "Add at least one message block before starting.";
            return false;
        }
        if (profile.Messages.Any(block => block.Text.Length > 400))
        {
            error = "Every message block must be 400 characters or fewer.";
            return false;
        }
        if (profile.Cities.Count == 0)
        {
            error = "Select at least one city.";
            return false;
        }
        var characterName = string.IsNullOrWhiteSpace(travel.CharacterName)
            ? persistence.LastCharacterName
            : travel.CharacterName;
        var characterHomeWorld = string.IsNullOrWhiteSpace(travel.HomeWorld)
            ? persistence.LastCharacterHomeWorld
            : travel.HomeWorld;
        var currentWorldName = string.IsNullOrWhiteSpace(travel.CurrentWorld)
            ? travel.GetLobbyCharacterCurrentWorld(characterName, characterHomeWorld) is { Length: > 0 } lobbyWorld
                ? lobbyWorld
                : persistence.LastCharacterCurrentWorld
            : travel.CurrentWorld;
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(characterHomeWorld))
        {
            error = "Log into the character once before starting a run from the title or character-selection screen.";
            return false;
        }
        var visibleWorlds = WorldCatalog.VisibleWorlds(characterHomeWorld, profile.DeveloperMode)
            .Select(world => world.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = profile.Worlds.Where(visibleWorlds.Contains).ToArray();
        if (selected.Length == 0)
        {
            error = "Select at least one available world.";
            return false;
        }
        var current = WorldCatalog.FindWorld(currentWorldName);
        var routeWorlds = selected
            .Select(WorldCatalog.FindWorld)
            .Where(world => world is not null)
            .Cast<WorldDefinition>()
            .OrderBy(world => current is null || !world.DataCenter.Equals(current.DataCenter, StringComparison.OrdinalIgnoreCase))
            .ThenBy(world => !world.Name.Equals(currentWorldName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(world => world.DataCenter)
            .ThenBy(world => world.Name)
            .ToArray();
        var currentCity = travel.GetCurrentCity();
        var selectedCities = WorldCatalog.Cities
            .Where(city => profile.Cities.Contains(city.Id))
            .ToArray();
        var route = routeWorlds
            .SelectMany(world => selectedCities
                .OrderBy(city => world.Name.Equals(currentWorldName, StringComparison.OrdinalIgnoreCase) &&
                                 currentCity is not null && city.Id == currentCity.Id ? 0 : 1)
                .ThenBy(city => Array.IndexOf(selectedCities, city))
                .Select(city => new RouteStop(world.Name, world.DataCenter, city.Id)))
            .ToList();
        var firstRouteWorld = route.Count == 0 ? null : WorldCatalog.FindWorld(route[0].World);
        var allowedPostRunWorlds = WorldCatalog.VisibleWorlds(characterHomeWorld, profile.DeveloperMode)
            .Select(world => world.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var postRunWorld = profile.PostRunDestination switch
        {
            PostRunDestination.HomeWorld => characterHomeWorld,
            PostRunDestination.ChosenWorld when allowedPostRunWorlds.Contains(profile.ChosenPostRunWorld) => profile.ChosenPostRunWorld,
            _ => currentWorldName,
        };
        var requiresInitialLogin =
            !DalamudServices.ClientState.IsLoggedIn &&
            (current is null || firstRouteWorld is null ||
             current.DataCenter.Equals(firstRouteWorld.DataCenter, StringComparison.OrdinalIgnoreCase));
        state = new PersistedRunState
        {
            RunId = Guid.NewGuid().ToString("N"),
            StartedUtc = DateTime.UtcNow,
            CharacterName = characterName,
            CharacterHomeWorld = characterHomeWorld,
            StartingWorld = currentWorldName,
            PostRunDestination = profile.PostRunDestination,
            PostRunWorld = postRunWorld,
            ProfileName = profile.Name,
            Phase = DalamudServices.ClientState.IsLoggedIn
                ? RunPhase.Preparing
                : RunPhase.TravelingDataCenter,
            Route = route,
            Status = DalamudServices.ClientState.IsLoggedIn
                ? $"Preparing {route.Count:N0} city stop(s)."
                : requiresInitialLogin
                    ? $"Preparing to log into {characterName} for the first configured data center."
                    : $"Preparing data-center travel to {route[0].DataCenter} without logging into the current data center.",
            NextActionUtc = DateTime.UtcNow,
            AwaitingInitialLogin = requiresInitialLogin,
        };
        foregroundRequested = true;
        Save();
        error = string.Empty;
        return true;
    }

    public void Pause()
    {
        if (state is null || !IsRunning)
            return;
        state.PausedPhase = state.Phase;
        state.Phase = RunPhase.Paused;
        state.PauseStartedUtc = DateTime.UtcNow;
        state.Status = "Run paused. Resume when you are ready to continue from this stop.";
        travel.PauseNavigation();
        travel.RecordRunDiagnostic(
            $"Paused at route stop {state.StopIndex + 1}/{state.Route.Count} during {state.PausedPhase}; " +
            $"message block {state.MessageIndex + 1}, target={CurrentStop?.CityName}@{CurrentStop?.World}.");
        Save();
    }

    public void Resume()
    {
        if (state?.Phase != RunPhase.Paused)
            return;
        if (state.PauseStartedUtc != default)
        {
            state.TotalPausedSeconds += Math.Max(0d, (DateTime.UtcNow - state.PauseStartedUtc).TotalSeconds);
            state.PauseStartedUtc = default;
        }
        var stop = CurrentStop;
        if (stop is null)
        {
            state.PausedPhase = RunPhase.Idle;
            state.Phase = RunPhase.Preparing;
            state.NextActionUtc = DateTime.UtcNow;
            state.Status = "Resuming route completion.";
            travel.RecordRunDiagnostic("Resumed after the final route stop; reconciling completion.");
            foregroundRequested = true;
            Save();
            return;
        }

        var pausedPhase = state.PausedPhase;
        var localPlayerReady = DalamudServices.ClientState.IsLoggedIn &&
                               DalamudServices.ObjectTable.LocalPlayer is not null;
        if (!localPlayerReady)
        {
            if (pausedPhase is RunPhase.TravelingCity or RunPhase.WaitingForArrival &&
                (travel.IsCityTeleportBusy || DalamudServices.ClientState.IsLoggedIn))
            {
                state.Phase = RunPhase.WaitingForArrival;
                state.Status = $"Resumed while loading {stop.CityName} on {stop.World}.";
            }
            else if (pausedPhase == RunPhase.ReturningHome)
            {
                state.Phase = RunPhase.ReturningHome;
                state.Status = $"Resumed while returning to {state.PostRunWorld}.";
            }
            else
            {
                state.Phase = RunPhase.TravelingDataCenter;
                state.Status = $"Resumed outside the game world; reconciling login and data-center travel to {stop.World}.";
            }
        }
        else
        {
            travel.ResetInGameNavigationForResume();
            state.AwaitingInitialLogin = false;
            state.AwaitingDestinationLogin = false;
            state.AwaitingAutomaticDataCenterConnection = false;
            ResetTravelAttempt();

            var currentWorld = travel.CurrentWorld;
            if (!currentWorld.Equals(stop.World, StringComparison.OrdinalIgnoreCase))
            {
                var currentDefinition = WorldCatalog.FindWorld(currentWorld);
                var targetDefinition = WorldCatalog.FindWorld(stop.World);
                state.Phase = currentDefinition is not null && targetDefinition is not null &&
                              currentDefinition.DataCenter.Equals(targetDefinition.DataCenter, StringComparison.OrdinalIgnoreCase)
                    ? RunPhase.TravelingWorld
                    : RunPhase.TravelingDataCenter;
                state.Status = $"Resumed on {currentWorld}; continuing travel to {stop.World}.";
            }
            else if (!travel.IsInCity(stop.City))
            {
                state.Phase = RunPhase.TravelingCity;
                state.Status = $"Resumed on {stop.World}; continuing to {stop.CityName}.";
            }
            else
            {
                state.Phase = pausedPhase == RunPhase.SendingMessages
                    ? RunPhase.SendingMessages
                    : RunPhase.Preparing;
                state.Status = state.MessageIndex > 0
                    ? $"Resumed at {stop.CityName} on {stop.World}; continuing message block {state.MessageIndex + 1}."
                    : $"Resumed at the current route stop: {stop.CityName} on {stop.World}.";
            }
        }

        state.PausedPhase = RunPhase.Idle;
        state.NextActionUtc = DateTime.UtcNow;
        travel.RecordRunDiagnostic(
            $"Resumed route stop {state.StopIndex + 1}/{state.Route.Count}; reconciled {pausedPhase} to {state.Phase}. " +
            $"Current={travel.CurrentTerritoryName}@{travel.CurrentWorld}; target={stop.CityName}@{stop.World}; message block={state.MessageIndex + 1}.");
        foregroundRequested = true;
        Save();
    }

    public void Stop()
    {
        travel.Abort();
        pendingMessage = null;
        state = null;
        persistence.SaveRunState(null);
    }

    public void ResetCompletedRun()
    {
        if (state?.Phase != RunPhase.Completed)
            return;
        state = null;
        persistence.SaveRunState(null);
    }

    public bool ConsumeForegroundRequest()
    {
        if (!foregroundRequested)
            return false;
        foregroundRequested = false;
        return true;
    }

    public void Tick(VenueProfile profile)
    {
        if (state is null || state.Phase is RunPhase.Paused or RunPhase.Completed or RunPhase.Failed)
            return;
        if (!state.ProfileName.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            var savedProfile = persistence.Profiles.GetValueOrDefault(state.ProfileName);
            if (savedProfile is not null)
                profile = savedProfile;
        }
        travel.SetGeneralReactionDelaySeconds(profile.GeneralReactionDelaySeconds);
        if (travel.HandleAetheryteTicketPopup(profile.TicketAction))
            return;
        if (state.Phase == RunPhase.ReturningHome)
        {
            HandleReturnHome();
            return;
        }
        if (state.StopIndex >= state.Route.Count)
        {
            Complete();
            return;
        }

        var stop = state.Route[state.StopIndex];
        if (DalamudServices.ObjectTable.LocalPlayer is null &&
            state.Phase is RunPhase.TravelingCity or RunPhase.WaitingForArrival &&
            (DalamudServices.ClientState.IsLoggedIn || travel.IsCityTeleportBusy))
        {
            if (!state.TravelBusyObserved)
            {
                state.TravelBusyObserved = true;
                Save();
            }
            state.Status = $"Loading {stop.CityName} on {stop.World}.";
            return;
        }
        if (!DalamudServices.ClientState.IsLoggedIn || DalamudServices.ObjectTable.LocalPlayer is null)
        {
            var lobbyWorldName = travel.GetLobbyCharacterCurrentWorld(state.CharacterName, state.CharacterHomeWorld);
            var lobbyWorld = WorldCatalog.FindWorld(lobbyWorldName);
            var firstStopWorld = WorldCatalog.FindWorld(stop.World);
            if (!state.AwaitingInitialLogin &&
                !state.AwaitingDestinationLogin &&
                lobbyWorld is not null &&
                firstStopWorld is not null)
            {
                state.AwaitingInitialLogin = lobbyWorld.DataCenter.Equals(
                    firstStopWorld.DataCenter,
                    StringComparison.OrdinalIgnoreCase);
            }
            state.Status = state.AwaitingDestinationLogin
                ? $"Waiting for {stop.World} on {stop.DataCenter} to appear before logging in {state.CharacterName}."
                : state.AwaitingInitialLogin
                ? $"Logging into {state.CharacterName} on {lobbyWorldName} because that data center is first in the route."
                : $"Beginning data-center travel to {stop.World} on {stop.DataCenter} without logging into the current data center.";
            state.Phase = RunPhase.TravelingDataCenter;
            if (state.AwaitingDestinationLogin)
                travel.ContinueCharacterLogin(
                    state.CharacterName,
                    state.CharacterHomeWorld,
                    stop.World,
                    allowTitleStart: false);
            else if (state.AwaitingInitialLogin)
                travel.ContinueCharacterLogin(state.CharacterName, state.CharacterHomeWorld);
            else
            {
                travel.ContinueDataCenterNavigation(
                    stop.World,
                    stop.DataCenter,
                    state.CharacterName,
                    state.CharacterHomeWorld,
                    state.AwaitingAutomaticDataCenterConnection);
                if (travel.AutomaticDataCenterConnectionPending &&
                    !state.AwaitingAutomaticDataCenterConnection)
                {
                    state.AwaitingAutomaticDataCenterConnection = true;
                    Save();
                }
                if (travel.DestinationArrivalAcknowledged)
                {
                    state.AwaitingDestinationLogin = true;
                    state.AwaitingAutomaticDataCenterConnection = false;
                    state.Status = $"Data-center travel reached {stop.DataCenter}. Waiting for the destination character screen to log in {state.CharacterName}.";
                    Save();
                }
            }
            return;
        }
        if (state.AwaitingInitialLogin || state.AwaitingDestinationLogin)
        {
            state.AwaitingInitialLogin = false;
            state.AwaitingDestinationLogin = false;
            state.AwaitingAutomaticDataCenterConnection = false;
            ResetTravelAttempt();
            travel.Abort();
            state.Phase = RunPhase.Preparing;
            Save();
        }
        if (DalamudServices.ObjectTable.LocalPlayer is { IsTargetable: false })
        {
            state.Status = "Waiting for the character and destination to finish loading.";
            return;
        }

        var currentWorld = travel.CurrentWorld;
        var currentDefinition = WorldCatalog.FindWorld(currentWorld);
        if (state.Phase == RunPhase.TravelingDataCenter &&
            currentDefinition is not null &&
            currentDefinition.DataCenter.Equals(stop.DataCenter, StringComparison.OrdinalIgnoreCase))
        {
            ResetTravelAttempt();
            state.Phase = RunPhase.Preparing;
        }

        if (!currentWorld.Equals(stop.World, StringComparison.OrdinalIgnoreCase))
        {
            HandleWorldTravel(profile, stop);
            return;
        }

        if (PromoteCurrentCityStop(currentWorld))
        {
            stop = state.Route[state.StopIndex];
            Save();
        }

        if (state.Phase is RunPhase.TravelingWorld or RunPhase.TravelingDataCenter)
        {
            ResetTravelAttempt();
            state.Phase = RunPhase.Preparing;
        }

        if (!travel.IsInCity(stop.City))
        {
            HandleCityTravel(profile, stop);
            return;
        }

        if (state.Phase is RunPhase.TravelingCity or RunPhase.WaitingForArrival)
        {
            ResetTravelAttempt();
            state.Phase = RunPhase.SendingMessages;
        }

        SendMessages(profile, stop);
    }

    private bool PromoteCurrentCityStop(string currentWorld)
    {
        var currentCity = travel.GetCurrentCity();
        if (currentCity is null)
            return false;
        var matchingIndex = state!.Route.FindIndex(
            state.StopIndex,
            stop => stop.World.Equals(currentWorld, StringComparison.OrdinalIgnoreCase) && stop.City == currentCity.Id);
        if (matchingIndex <= state.StopIndex)
            return false;

        var currentCityStop = state.Route[matchingIndex];
        state.Route.RemoveAt(matchingIndex);
        state.Route.Insert(state.StopIndex, currentCityStop);
        state.Status = $"Arrived in {currentCity.Name}. Processing this city before the remaining stops on {currentWorld}.";
        return true;
    }

    private void HandleWorldTravel(VenueProfile profile, RouteStop stop)
    {
        travel.TickNavigation();
        var currentDefinition = WorldCatalog.FindWorld(travel.CurrentWorld);
        var targetDefinition = WorldCatalog.FindWorld(stop.World)!;
        var crossDc = currentDefinition is null || !currentDefinition.DataCenter.Equals(targetDefinition.DataCenter, StringComparison.OrdinalIgnoreCase);
        state!.Phase = crossDc ? RunPhase.TravelingDataCenter : RunPhase.TravelingWorld;

        if (travel.IsTravelBusy)
        {
            state.TravelBusyObserved = true;
            state.Status = crossDc
                ? $"Changing Data Centers to {targetDefinition.DataCenter}. The tablet will remain available during logout and login."
                : $"Visiting {stop.World}. Waiting for world travel to finish.";
            return;
        }
        if (state.TravelRequestUtc != default)
        {
            var elapsed = DateTime.UtcNow - state.TravelRequestUtc;
            var acceptedTravelTimedOut = state.TravelBusyObserved && elapsed >= TimeSpan.FromMinutes(10);
            var reactionAllowance = profile.GeneralReactionDelaySeconds * 8;
            var unacceptedTravelTimedOut = !state.TravelBusyObserved &&
                                          elapsed >= TimeSpan.FromSeconds(15 + reactionAllowance);
            if (acceptedTravelTimedOut || unacceptedTravelTimedOut)
            {
                travel.Abort();
                state.TravelRequestUtc = default;
                state.TravelBusyObserved = false;
                state.AwaitingAutomaticDataCenterConnection = false;
                ScheduleRetry(profile, $"Travel to {stop.World} ended before the destination was reached.");
                Save();
            }
            return;
        }
        if (DateTime.UtcNow < state.NextActionUtc)
            return;

        var alternatives = crossDc && profile.TryAlternateDataCenterWorlds
            ? WorldCatalog.Worlds
                .Where(world => world.DataCenter.Equals(stop.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                                !world.Name.Equals(stop.World, StringComparison.OrdinalIgnoreCase))
                .OrderBy(world => world.Name)
                .Select(world => world.Name)
                .ToArray()
            : [];
        var totalAllowedAttempts = profile.MaximumTravelAttempts + alternatives.Length;
        if (state.TravelAttempt >= totalAllowedAttempts)
        {
            SkipUnavailableRouteScope(
                stop,
                crossDc,
                crossDc
                    ? $"Could not enter {stop.DataCenter} through {stop.World} or any alternate gateway world."
                    : $"Could not reach {stop.World} after {profile.MaximumTravelAttempts} attempt(s).");
            return;
        }

        var destination = state.TravelAttempt < profile.MaximumTravelAttempts
            ? stop.World
            : alternatives[state.TravelAttempt - profile.MaximumTravelAttempts];
        state.TravelAttempt++;
        if (crossDc)
            state.AwaitingAutomaticDataCenterConnection = false;
        if (travel.RequestWorld(destination, state.CharacterName, state.CharacterHomeWorld))
        {
            state.TravelRequestUtc = DateTime.UtcNow;
            state.TravelBusyObserved = false;
            state.NextActionUtc = DateTime.UtcNow.AddSeconds(15);
            state.Status = destination.Equals(stop.World, StringComparison.OrdinalIgnoreCase)
                ? $"Travel request started for {destination} (attempt {state.TravelAttempt} of {profile.MaximumTravelAttempts})."
                : $"Primary attempts were exhausted. Trying {destination} once as alternate gateway {state.TravelAttempt - profile.MaximumTravelAttempts} of {alternatives.Length} into {stop.DataCenter}.";
        }
        else
        {
            travel.Abort();
            ScheduleRetry(profile, $"Travel could not start for {destination}.");
        }
        Save();
    }

    private void HandleCityTravel(VenueProfile profile, RouteStop stop)
    {
        if (state!.Phase == RunPhase.WaitingForArrival && state.TravelRequestUtc != default)
        {
            if (travel.IsCityTeleportBusy)
            {
                state.TravelBusyObserved = true;
                state.Status = $"Teleporting to {stop.CityName} on {stop.World}.";
                return;
            }

            var elapsed = DateTime.UtcNow - state.TravelRequestUtc;
            if (!state.TravelBusyObserved && elapsed < TimeSpan.FromSeconds(10))
            {
                state.Status = $"Waiting for the teleport to {stop.CityName} to begin.";
                return;
            }

            var teleportHadStarted = state.TravelBusyObserved;
            state.TravelRequestUtc = default;
            state.TravelBusyObserved = false;
            ScheduleRetry(
                profile,
                teleportHadStarted
                    ? $"Teleport loading ended without reaching {stop.CityName}."
                    : $"Teleport to {stop.CityName} was accepted but never began.");
            Save();
            return;
        }

        state.Phase = RunPhase.TravelingCity;
        if (DateTime.UtcNow < state.NextActionUtc)
        {
            state.Status = $"Teleporting to {stop.CityName} on {stop.World}.";
            return;
        }
        if (state.TravelAttempt >= profile.MaximumTravelAttempts)
        {
            SkipCurrentCityStop($"Could not teleport to {stop.CityName} after {profile.MaximumTravelAttempts} attempt(s).");
            return;
        }
        state.TravelAttempt++;
        var teleportCost = travel.GetCityTeleportCost(stop.City);
        if (travel.RequestCity(stop.City))
        {
            state.TeleportGilSpent += teleportCost;
            state.Phase = RunPhase.WaitingForArrival;
            state.TravelRequestUtc = DateTime.UtcNow;
            state.TravelBusyObserved = travel.IsCityTeleportBusy;
            state.NextActionUtc = DateTime.UtcNow;
            state.Status = $"Teleporting to {stop.CityName} on {stop.World}.";
        }
        else
        {
            ScheduleRetry(profile, $"The {stop.CityName} teleport could not start.");
        }
        Save();
    }

    private void SendMessages(VenueProfile profile, RouteStop stop)
    {
        state!.TravelAttempt = 0;
        state.Phase = RunPhase.SendingMessages;
        if (pendingMessage is not null)
        {
            if (!pendingMessage.IsCompleted)
                return;
            var sent = pendingMessage.Status == TaskStatus.RanToCompletion && pendingMessage.Result;
            pendingMessage = null;
            if (!sent)
            {
                Fail($"Message block {state.MessageIndex + 1} could not be sent: {chat.LastError}");
                return;
            }
            state.MessageIndex++;
            state.NextActionUtc = DateTime.UtcNow.AddSeconds(profile.MessageDelaySeconds);
            Save();
        }
        if (state.MessageIndex >= profile.Messages.Count)
        {
            state.StopIndex++;
            state.MessageIndex = 0;
            state.NextActionUtc = DateTime.UtcNow;
            state.Phase = RunPhase.Preparing;
            state.Status = state.StopIndex >= state.Route.Count
                ? "All selected cities and worlds have been completed."
                : $"Completed {stop.CityName} on {stop.World}. Preparing the next stop.";
            Save();
            if (state.StopIndex >= state.Route.Count)
                Complete();
            return;
        }
        if (DateTime.UtcNow < state.NextActionUtc)
            return;
        state.Status = $"Sending message {state.MessageIndex + 1} of {profile.Messages.Count} in {stop.CityName} on {stop.World}.";
        pendingMessage = chat.SendAsync(profile.Messages[state.MessageIndex], disposeToken.Token);
    }

    private void ScheduleRetry(VenueProfile profile, string reason)
    {
        var delay = profile.InitialRetryDelaySeconds +
                    Math.Max(0, state!.TravelAttempt - 1) * profile.RetryDelayIncreaseSeconds;
        state.NextActionUtc = DateTime.UtcNow.AddSeconds(delay);
        state.Status = state.TravelAttempt >= profile.MaximumTravelAttempts && profile.TryAlternateDataCenterWorlds
            ? $"{reason} Primary attempts are exhausted; trying the next alternate data-center gateway once in {delay} seconds."
            : $"{reason} Retrying in {delay} seconds (attempt {state.TravelAttempt + 1} of {profile.MaximumTravelAttempts}).";
    }

    private void SkipUnavailableRouteScope(RouteStop stop, bool entireDataCenter, string reason)
    {
        travel.Abort();
        var firstSkippedIndex = state!.StopIndex;
        while (state.StopIndex < state.Route.Count)
        {
            var candidate = state.Route[state.StopIndex];
            var sameScope = entireDataCenter
                ? candidate.DataCenter.Equals(stop.DataCenter, StringComparison.OrdinalIgnoreCase)
                : candidate.World.Equals(stop.World, StringComparison.OrdinalIgnoreCase);
            if (!sameScope)
                break;
            state.SkippedStopIndexes.Add(state.StopIndex);
            state.StopIndex++;
        }
        var skippedStops = state.StopIndex - firstSkippedIndex;
        state.MessageIndex = 0;
        state.TravelRequestUtc = default;
        state.TravelBusyObserved = false;
        state.TravelAttempt = 0;
        state.NextActionUtc = DateTime.UtcNow;
        state.Phase = RunPhase.Preparing;
        state.Status = entireDataCenter
            ? $"{reason} Skipped {stop.DataCenter} ({skippedStops} city stop(s)) and continuing the run."
            : $"{reason} Skipped {stop.World} ({skippedStops} city stop(s)) and continuing the run.";
        Save();
        if (state.StopIndex >= state.Route.Count)
            Complete();
    }

    private void SkipCurrentCityStop(string reason)
    {
        travel.Abort();
        state!.SkippedStopIndexes.Add(state.StopIndex);
        state.StopIndex++;
        state.MessageIndex = 0;
        state.TravelRequestUtc = default;
        state.TravelBusyObserved = false;
        state.TravelAttempt = 0;
        state.NextActionUtc = DateTime.UtcNow;
        state.Phase = RunPhase.Preparing;
        state.Status = $"{reason} Skipped this city stop and continuing the run.";
        Save();
        if (state.StopIndex >= state.Route.Count)
            Complete();
    }

    private void ResetTravelAttempt()
    {
        state!.TravelAttempt = 0;
        state.NextActionUtc = DateTime.UtcNow;
        state.TravelRequestUtc = default;
        state.TravelBusyObserved = false;
        state.AwaitingAutomaticDataCenterConnection = false;
    }

    private void Complete()
    {
        var destination = state!.PostRunWorld;
        if (!string.IsNullOrWhiteSpace(destination) &&
            !travel.CurrentWorld.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            state.Phase = RunPhase.ReturningHome;
            state.Status = $"Route complete. Returning to {destination}.";
            state.NextActionUtc = DateTime.UtcNow;
            Save();
            return;
        }
        FinalizeCompletion();
    }

    private void HandleReturnHome()
    {
        var destination = state!.PostRunWorld;
        if (travel.CurrentWorld.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            travel.Abort();
            FinalizeCompletion();
            return;
        }

        var requested = ContinuePostRunTravel(destination);
        travel.TickNavigation();
        if (requested)
        {
            state.Status = state.PostRunDestination == PostRunDestination.HomeWorld
                ? $"Returning to home world {destination}."
                : $"Returning to {destination}.";
            Save();
            return;
        }
        state.Status = $"Travel to {destination} could not start. Retrying shortly.";
        Save();
    }

    private bool ContinuePostRunTravel(string destination)
    {
        var activeState = state!;
        var destinationDefinition = WorldCatalog.FindWorld(destination);
        if (destinationDefinition is null)
            return false;

        // Let the normal world request choose the correct transport while the
        // character is logged in. It uses World Visit within the current data
        // center and only enters the logout flow for a real data-center change.
        if (DalamudServices.ClientState.IsLoggedIn && DalamudServices.ObjectTable.LocalPlayer is not null)
        {
            activeState.PostRunLoginPrepared = false;
            return travel.RequestWorld(destination, activeState.CharacterName, activeState.CharacterHomeWorld);
        }

        var lobbyWorldName = travel.GetLobbyCharacterCurrentWorld(activeState.CharacterName, activeState.CharacterHomeWorld);
        var lobbyWorld = WorldCatalog.FindWorld(lobbyWorldName);
        var homeWorld = WorldCatalog.FindWorld(activeState.CharacterHomeWorld);

        if (lobbyWorld is not null &&
            lobbyWorld.DataCenter.Equals(destinationDefinition.DataCenter, StringComparison.OrdinalIgnoreCase))
        {
            if (!activeState.PostRunLoginPrepared)
            {
                // Recover from a stale return-home request that may already have
                // logged out on the same data center. Log into the character's
                // current world first, then use World Visit after loading in.
                travel.Abort();
                activeState.PostRunLoginPrepared = true;
                Save();
            }
            travel.ContinueCharacterLogin(
                activeState.CharacterName,
                activeState.CharacterHomeWorld,
                lobbyWorld.Name,
                allowTitleStart: false);
            return true;
        }

        activeState.PostRunLoginPrepared = false;
        if (homeWorld is not null &&
            destinationDefinition.DataCenter.Equals(homeWorld.DataCenter, StringComparison.OrdinalIgnoreCase))
        {
            return travel.RequestReturnHomeWorld(activeState.CharacterName, activeState.CharacterHomeWorld);
        }

        return ContinuePostRunDataCenterNavigation(destinationDefinition);
    }

    private bool ContinuePostRunDataCenterNavigation(WorldDefinition destination)
    {
        travel.ContinueDataCenterNavigation(
            destination.Name,
            destination.DataCenter,
            state!.CharacterName,
            state.CharacterHomeWorld,
            state.AwaitingAutomaticDataCenterConnection);
        state.AwaitingAutomaticDataCenterConnection = travel.AutomaticDataCenterConnectionPending;
        return true;
    }

    private void FinalizeCompletion()
    {
        state!.Phase = RunPhase.Completed;
        state.CompletedUtc = DateTime.UtcNow;
        state.CharacterName = string.IsNullOrWhiteSpace(state.CharacterName) ? travel.CharacterName : state.CharacterName;
        state.CharacterHomeWorld = string.IsNullOrWhiteSpace(state.CharacterHomeWorld) ? travel.HomeWorld : state.CharacterHomeWorld;
        state.ReceiptCode = CreateReceiptCode(state);
        var skipped = state.SkippedStopIndexes.Count;
        state.Status = skipped == 0
            ? $"Run complete. Sent the configured message sequence at all {state.Route.Count:N0} route stops."
            : $"Run complete with {state.Route.Count - skipped:N0} successful stop(s) and {skipped:N0} skipped stop(s).";
        foregroundRequested = true;
        Save();
    }

    private void EnsureDefaultWorldSelection(VenueProfile profile, string? homeWorld)
    {
        if (profile.WorldDefaultsInitialized)
            return;

        if (profile.Worlds.Count > 0)
        {
            profile.WorldDefaultsInitialized = true;
            persistence.SaveProfile(profile);
            return;
        }

        var region = WorldCatalog.DetectHomeRegion(homeWorld);
        profile.Worlds = WorldCatalog.Worlds
            .Where(world => world.Region == region && world.Region != ShoutRunnerRegion.Oceania)
            .Select(world => world.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        profile.WorldDefaultsInitialized = true;
        persistence.SaveProfile(profile);
    }

    private static string CreateReceiptCode(PersistedRunState completed)
    {
        var route = string.Join("|", completed.Route.Select(stop => $"{stop.DataCenter}/{stop.World}/{stop.City}"));
        var skipped = string.Join(",", completed.SkippedStopIndexes.OrderBy(index => index));
        var source = $"{completed.RunId}|{completed.CharacterName}|{completed.CharacterHomeWorld}|{completed.StartedUtc:O}|{completed.CompletedUtc:O}|{route}|skipped:{skipped}|teleport-gil:{completed.TeleportGilSpent}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return $"{digest[..4]}-{digest[4..8]}-{digest[8..12]}-{digest[12..16]}";
    }

    private void Fail(string message)
    {
        state!.Phase = RunPhase.Failed;
        state.Status = message;
        foregroundRequested = true;
        Save();
    }

    private void Save() => persistence.SaveRunState(state);

    public void Dispose()
    {
        disposeToken.Cancel();
        disposeToken.Dispose();
        Save();
    }
}
