using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using Lumina.Excel.Sheets;

namespace WardrobeManager;

internal sealed class IntegrationService : IDisposable
{
    private const string Source = "AirTablet WardrobeManager";
    private const string FolderSetupDesignName = ".WardrobeManager Folder Setup";
    private const int TemporarySettingsKey = -1094335841;
    private readonly GetModList getModList;
    private readonly GetChangedItems getChangedItems;
    private readonly GetCollection getCollection;
    private readonly GetCollectionForObject getCollectionForObject;
    private readonly GetCollections getCollections;
    private readonly GetModDirectory getModDirectory;
    private readonly GetCurrentModSettings getCurrentSettings;
    private readonly GetCurrentModSettingsWithTemp getCurrentSettingsWithTemp;
    private readonly GetAvailableModSettings getAvailableSettings;
    private readonly TrySetMod setMod;
    private readonly TrySetModPriority setPriority;
    private readonly TrySetModSettings setOptions;
    private readonly SetCollectionForObject setCollectionForObject;
    private readonly RedrawObject redraw;
    private readonly GetPlayerResourcePaths getPlayerResourcePaths;
    private readonly SetTemporaryModSettings setTemporarySettings;
    private readonly RemoveAllTemporaryModSettings removeTemporarySettings;
    private readonly GetStateBase64 getState;
    private readonly ICallGateSubscriber<int, uint, object?> getStateObject;
    private readonly ApplyState applyState;
    private readonly ApplyDesign applyDesign;
    private readonly GetDesignList getDesignList;
    private readonly GetDesignListExtended getDesignListExtended;
    private readonly GetDesignBase64 getDesignBase64;
    private readonly ICallGateSubscriber<Guid, object?> getDesignJObject;
    private readonly AddDesign addDesign;
    private readonly DeleteDesign deleteDesign;
    private readonly OpenDesign openDesign;
    private readonly OpenQuickDesignBar openQuickDesignBar;
    private readonly ICallGateSubscriber<IList<(Guid UniqueId, string Name, string VirtualPath,
        List<(string Name, ushort WorldId, byte CharacterType, ushort CharacterSubType)> Characters,
        int Priority, bool IsEnabled)>> getCustomizeProfiles;
    private readonly ICallGateSubscriber<Guid, int> enableCustomizeProfile;
    private readonly ICallGateSubscriber<Guid, int> disableCustomizeProfile;
    private readonly ICallGateSubscriber<int, object?> clearHonorificTitle;
    private readonly Dictionary<string, IReadOnlyList<ChangedItem>> changedItemCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ModOptionGroup>> optionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> effectiveConflictCache = new(StringComparer.OrdinalIgnoreCase);
    private Guid activeTemporaryCollectionId;

    public IntegrationService()
    {
        var pi = DalamudServices.PluginInterface;
        getModList = new GetModList(pi);
        getChangedItems = new GetChangedItems(pi);
        getCollection = new GetCollection(pi);
        getCollectionForObject = new GetCollectionForObject(pi);
        getCollections = new GetCollections(pi);
        getModDirectory = new GetModDirectory(pi);
        getCurrentSettings = new GetCurrentModSettings(pi);
        getCurrentSettingsWithTemp = new GetCurrentModSettingsWithTemp(pi);
        getAvailableSettings = new GetAvailableModSettings(pi);
        setMod = new TrySetMod(pi);
        setPriority = new TrySetModPriority(pi);
        setOptions = new TrySetModSettings(pi);
        setCollectionForObject = new SetCollectionForObject(pi);
        redraw = new RedrawObject(pi);
        getPlayerResourcePaths = new GetPlayerResourcePaths(pi);
        setTemporarySettings = new SetTemporaryModSettings(pi);
        removeTemporarySettings = new RemoveAllTemporaryModSettings(pi);
        getState = new GetStateBase64(pi);
        getStateObject = pi.GetIpcSubscriber<int, uint, object?>("Glamourer.GetState");
        applyState = new ApplyState(pi);
        applyDesign = new ApplyDesign(pi);
        getDesignList = new GetDesignList(pi);
        getDesignListExtended = new GetDesignListExtended(pi);
        getDesignBase64 = new GetDesignBase64(pi);
        // Request object at the IPC boundary so Glamourer's JObject does not need to be
        // converted into the copy of Newtonsoft.Json loaded by AirTablet.
        getDesignJObject = pi.GetIpcSubscriber<Guid, object?>("Glamourer.GetDesignJObject");
        addDesign = new AddDesign(pi);
        deleteDesign = new DeleteDesign(pi);
        openDesign = new OpenDesign(pi);
        openQuickDesignBar = new OpenQuickDesignBar(pi);
        getCustomizeProfiles = pi.GetIpcSubscriber<IList<(Guid, string, string,
            List<(string, ushort, byte, ushort)>, int, bool)>>("CustomizePlus.Profile.GetList");
        enableCustomizeProfile = pi.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.EnableByUniqueId");
        disableCustomizeProfile = pi.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DisableByUniqueId");
        clearHonorificTitle = pi.GetIpcSubscriber<int, object?>("Honorific.ClearCharacterTitle");
    }

    public IntegrationRequirementState RequirementState
    {
        get
        {
            var penumbra = false;
            var glamourer = false;
            try { _ = new Penumbra.Api.IpcSubscribers.ApiVersion(DalamudServices.PluginInterface).Invoke(); penumbra = true; } catch { }
            try { _ = new Glamourer.Api.IpcSubscribers.ApiVersion(DalamudServices.PluginInterface).Invoke(); glamourer = true; } catch { }
            return new IntegrationRequirementState(penumbra, glamourer);
        }
    }

    public string RequirementStatus => RequirementState.Message;

    public WardrobeModRule CreateRule(AvailableMod mod, int fallbackPriority)
    {
        var rule = new WardrobeModRule { Directory = mod.Directory, Name = mod.Name, Priority = fallbackPriority };
        try
        {
            var collection = getCollection.Invoke(ApiCollectionType.Yourself);
            if (collection is null) return rule;
            var settings = getCurrentSettings.Invoke(collection.Value.Id, mod.Directory, mod.Name, false);
            if (!IsSuccess(settings.Item1) || settings.Item2 is not { } current) return rule;
            rule.Enabled = current.Item1;
            rule.AssociationState = current.Item1
                ? GlamourerModAssociationState.Enabled : GlamourerModAssociationState.Disabled;
            rule.Priority = current.Item2;
            rule.Options = current.Item3.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return rule;
    }

    public IReadOnlyList<PenumbraCollection> GetCollections()
    {
        try { return getCollections.Invoke().Select(pair => new PenumbraCollection(pair.Key, pair.Value)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not read Penumbra collections."); return []; }
    }

    public PenumbraCollection? GetYourselfCollection()
    {
        try
        {
            var value = getCollection.Invoke(ApiCollectionType.Yourself);
            return value is null ? null : new PenumbraCollection(value.Value.Id, value.Value.Name);
        }
        catch { return null; }
    }

    public IReadOnlyList<CustomizePlusProfile> GetCustomizePlusProfiles()
    {
        try
        {
            return getCustomizeProfiles.InvokeFunc()
                .Select(profile => new CustomizePlusProfile(profile.UniqueId, profile.Name,
                    profile.VirtualPath, profile.Characters, profile.IsEnabled))
                .OrderBy(profile => profile.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not read Customize+ profiles.");
            return [];
        }
    }

    public IReadOnlyList<HonorificTitle> GetHonorificTitles()
    {
        try
        {
            if (!DalamudServices.PlayerState.IsLoaded) return [];
            var character = ReadHonorificCharacterConfig();
            var configured = new List<JObject>();
            if (character?["DefaultTitle"] is JObject defaultTitle) configured.Add(defaultTitle);
            configured.AddRange(character?["CustomTitles"]?.OfType<JObject>() ?? []);
            var entries = TryReadLiveHonorificTitles(out var live) && live.Count > 0 ? live : configured;
            var result = new List<HonorificTitle>();
            foreach (var data in entries)
            {
                var title = data.Value<string>("Title")?.Trim() ?? string.Empty;
                if (title.Length == 0) continue;
                var prefix = data.Value<bool?>("IsPrefix") ?? false;
                var configuredMatch = configured.FirstOrDefault(candidate =>
                    string.Equals(candidate.Value<string>("Title")?.Trim(), title, StringComparison.OrdinalIgnoreCase)
                    && (candidate.Value<bool?>("IsPrefix") ?? false) == prefix);
                result.Add(new HonorificTitle(title, data.Value<bool?>("IsPrefix") ?? false,
                    configuredMatch?.Value<string>("UniqueId") ?? data.Value<string>("UniqueId") ?? string.Empty,
                    data.ToString(Newtonsoft.Json.Formatting.None)));
            }
            return result.GroupBy(title => (title.Id, title.Name, title.IsPrefix))
                .Select(group => group.First()).OrderBy(title => title.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not read Honorific titles.");
            return [];
        }
    }

    private static bool TryReadLiveHonorificTitles(out List<JObject> titles)
    {
        titles = [];
        try
        {
            var honorificAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                assembly.GetName().Name?.Equals("Honorific", StringComparison.OrdinalIgnoreCase) == true);
            var titleData = honorificAssembly?.GetType("Honorific.TitleData");
            if (titleData is null) return false;

            var subscriberMethod = typeof(Dalamud.Plugin.IDalamudPluginInterface).GetMethods()
                .FirstOrDefault(method => method.Name == "GetIpcSubscriber"
                    && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 3
                    && method.GetParameters() is [{ ParameterType: var parameterType }]
                    && parameterType == typeof(string));
            if (subscriberMethod is null) return false;
            var arrayType = titleData.MakeArrayType();
            var subscriber = subscriberMethod.MakeGenericMethod(typeof(string), typeof(uint), arrayType)
                .Invoke(DalamudServices.PluginInterface, ["Honorific.GetCharacterTitleList"]);
            if (subscriber is null) return false;
            var subscriberInterface = typeof(ICallGateSubscriber<,,>)
                .MakeGenericType(typeof(string), typeof(uint), arrayType);
            var invoke = subscriberInterface.GetMethod("InvokeFunc");
            if (invoke is null) return false;

            var path = HonorificConfigPath();
            var root = path is not null && File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            var identity = ResolveHonorificIdentity(root);
            if (invoke.Invoke(subscriber, [identity.Name, identity.World]) is not System.Collections.IEnumerable values)
                return false;
            foreach (var value in values)
            {
                if (value is null) continue;
                titles.Add(JObject.FromObject(value));
            }
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not read Honorific's live title list; using its saved configuration instead.");
            titles = [];
            return false;
        }
    }

    public IReadOnlyList<AvailableMod> GetMods()
    {
        try { return getModList.Invoke().Select(pair => new AvailableMod(pair.Key, pair.Value)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not read the Penumbra mod list."); return []; }
    }

    public IReadOnlyList<ModOptionGroup> GetAvailableOptions(WardrobeModRule rule)
    {
        if (optionCache.TryGetValue(rule.Directory, out var cached)) return cached;
        try
        {
            cached = (getAvailableSettings.Invoke(rule.Directory, rule.Name) ?? new Dictionary<string, (string[], GroupType)>())
                .Select(pair => new ModOptionGroup(
                    pair.Key,
                    pair.Value.Item1,
                    pair.Value.Item2 is GroupType.Multi or GroupType.Combining or GroupType.Complex))
                .ToList();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not read options for {Mod}.", rule.Name);
            cached = [];
        }

        optionCache[rule.Directory] = cached;
        return cached;
    }

    public bool TryCaptureAppearance(out string state, out Dictionary<string, uint> equipment, out string error)
    {
        state = string.Empty;
        equipment = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        try
        {
            var (result, value) = getState.Invoke(0, 0);
            if (result != GlamourerApiEc.Success || string.IsNullOrWhiteSpace(value))
            {
                error = $"Glamourer did not return the local appearance ({result}).";
                return false;
            }
            state = value;
            try
            {
                var rawState = getStateObject.InvokeFunc(0, 0);
                equipment = ParseEquipmentItems(ParseStateObject(rawState));
                if (equipment.Count == 0)
                    DalamudServices.Log.Warning(
                        "WardrobeManager captured Glamourer state but no equipment entries; raw IPC type={Type}.",
                        rawState?.GetType().FullName ?? "null");
            }
            catch (Exception ex) { DalamudServices.Log.Warning(ex, "WardrobeManager could not read equipment from the current Glamourer state."); }
            return true;
        }
        catch (Exception ex) { error = "Glamourer is unavailable: " + ex.Message; return false; }
    }

    public bool TryCaptureOutfitAppearance(out string outfitJson,
        out Dictionary<string, uint> equipment, out string error)
    {
        outfitJson = string.Empty;
        equipment = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        try
        {
            var state = ParseStateObject(getStateObject.InvokeFunc(0, 0));
            if (state is null)
            {
                error = "Glamourer did not return the local outfit appearance.";
                return false;
            }
            equipment = ParseEquipmentItems(state);
            RestrictToOutfitAppearance(state);
            outfitJson = state.ToString(Newtonsoft.Json.Formatting.None);
            return true;
        }
        catch (Exception ex)
        {
            error = "Glamourer could not capture the current outfit: " + ex.Message;
            return false;
        }
    }

    public bool TryCaptureCharacterAppearance(out string characterJson, out string error)
    {
        characterJson = string.Empty;
        error = string.Empty;
        try
        {
            var state = ParseStateObject(getStateObject.InvokeFunc(0, 0));
            if (state is null)
            {
                error = "Glamourer did not return the local character appearance.";
                return false;
            }

            RestrictToCharacterAppearance(state, initializeApply: true);
            characterJson = state.ToString(Newtonsoft.Json.Formatting.None);
            return true;
        }
        catch (Exception ex)
        {
            error = "Glamourer could not capture the physical character appearance: " + ex.Message;
            return false;
        }
    }

    public LayerScanResult CaptureActiveOutfitMods(WardrobePreset preset)
    {
        try
        {
            effectiveConflictCache.Clear();
            var collection = ResolveCurrentCollection();
            if (collection is null)
                return LayerScanResult.Fail("Penumbra has no active collection for the current character.");

            var modDirectory = getModDirectory.Invoke();
            if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
                return LayerScanResult.Fail("Penumbra's mod directory is unavailable.");

            var installed = new Dictionary<string, string>(getModList.Invoke(), StringComparer.OrdinalIgnoreCase);
            var roots = new List<(string Directory, string Name, string Root)>();
            var normalizedModDirectory = Path.GetFullPath(modDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var mod in installed)
            {
                try
                {
                    var root = Path.GetFullPath(Path.Combine(modDirectory, mod.Key))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (root.StartsWith(normalizedModDirectory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                        roots.Add((mod.Key, mod.Value, root));
                }
                catch { }
            }
            roots.Sort((left, right) => right.Root.Length.CompareTo(left.Root.Length));

            var resourcePaths = getPlayerResourcePaths.Invoke();
            if (!resourcePaths.TryGetValue(0, out var playerResources))
                return LayerScanResult.Fail("Penumbra could not read the current player's loaded resources. Try again after the character is fully visible.");

            var targetModels = ResolveEquipmentModels(preset.EquipmentItemIds);
            if (targetModels.Count == 0)
                return LayerScanResult.Fail("The captured Glamourer appearance has no readable equipped-item models. Capture the completed outfit again before scanning its mods.");

            var used = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in playerResources)
            {
                string actualPath;
                try { actualPath = Path.GetFullPath(resource.Key); }
                catch { continue; }
                var owner = roots.FirstOrDefault(candidate => actualPath.StartsWith(candidate.Root, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(owner.Directory)) continue;
                foreach (var gamePath in resource.Value)
                {
                    if (string.IsNullOrWhiteSpace(gamePath)) continue;
                    var normalized = gamePath.Replace('\\', '/');
                    if (MatchEquipmentSlots(normalized, targetModels).Count == 0) continue;
                    if (!used.TryGetValue(owner.Directory, out var paths))
                        used[owner.Directory] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    paths.Add(normalized);
                }
            }

            var previous = preset.Mods.GroupBy(rule => rule.Directory, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var candidates = new List<(WardrobeModRule Rule, HashSet<string> ConflictKeys)>();
            foreach (var entry in used)
            {
                if (!installed.TryGetValue(entry.Key, out var name)
                    || !TryGetEffectiveEnabledSettings(collection.Value.Id, entry.Key, name, out var currentPriority, out var options)) continue;
                var conflictKeys = GetConflictKeys(entry.Key, name, options, false);
                previous.TryGetValue(entry.Key, out var old);
                candidates.Add((new WardrobeModRule
                {
                    Directory = entry.Key,
                    Name = name,
                    Enabled = old?.Enabled ?? true,
                    Role = old?.Role is WardrobeLayerRole.Base or WardrobeLayerRole.Highest ? old.Role : WardrobeLayerRole.Highest,
                    CapturedFromResources = true,
                    CapturedPriority = currentPriority,
                    Priority = currentPriority,
                    Options = options,
                    Slots = entry.Value.SelectMany(path => MatchEquipmentSlots(path, targetModels))
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(SlotSortOrder).ToList(),
                    AffectedItems = entry.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                }, conflictKeys));
            }

            preset.RegisteredOutfitMods = candidates.Select(candidate => candidate.Rule)
                .OrderBy(rule => rule.Slots.Count == 0 ? int.MaxValue : rule.Slots.Min(SlotSortOrder))
                .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // Resource attribution tells us which mods are genuinely part of the worn
            // outfit. Compare declarations only inside that captured set: unrelated
            // enabled body, nail, UI, or other character mods are never candidates.
            var sharedByDirectory = candidates.ToDictionary(candidate => candidate.Rule.Directory,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            for (var left = 0; left < candidates.Count; left++)
            {
                for (var right = left + 1; right < candidates.Count; right++)
                {
                    var shared = candidates[left].ConflictKeys.Intersect(candidates[right].ConflictKeys,
                            StringComparer.OrdinalIgnoreCase)
                        .Where(key => IsEquipmentConflictKey(key, targetModels)).ToList();
                    if (shared.Count == 0) continue;
                    sharedByDirectory[candidates[left].Rule.Directory].UnionWith(shared);
                    sharedByDirectory[candidates[right].Rule.Directory].UnionWith(shared);
                }
            }

            var captured = new List<WardrobeModRule>();
            foreach (var candidate in candidates)
            {
                var shared = sharedByDirectory[candidate.Rule.Directory];
                if (shared.Count == 0) continue;
                var displayRule = new WardrobeModRule
                {
                    Directory = candidate.Rule.Directory,
                    Name = candidate.Rule.Name,
                    Enabled = candidate.Rule.Enabled,
                    Role = candidate.Rule.Role,
                    CapturedFromResources = true,
                    CapturedPriority = candidate.Rule.CapturedPriority,
                    Priority = candidate.Rule.Priority,
                    Options = candidate.Rule.Options.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(),
                        StringComparer.OrdinalIgnoreCase),
                    AffectedItems = shared.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                };
                displayRule.Slots = shared.SelectMany(key => MatchEquipmentConflictSlots(key, targetModels))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(SlotSortOrder).ThenBy(slot => slot, StringComparer.OrdinalIgnoreCase).ToList();
                captured.Add(displayRule);
            }

            if (captured.Count > 0)
            {
                var highest = captured.Where(rule => rule.Role == WardrobeLayerRole.Highest)
                    .OrderByDescending(rule => rule.CapturedPriority).FirstOrDefault()
                    ?? captured.OrderByDescending(rule => rule.CapturedPriority).First();
                foreach (var rule in captured) rule.Role = ReferenceEquals(rule, highest)
                    ? WardrobeLayerRole.Highest : WardrobeLayerRole.Base;
            }
            var manual = preset.Mods.Where(rule => !rule.CapturedFromResources
                && captured.All(found => !found.Directory.Equals(rule.Directory, StringComparison.OrdinalIgnoreCase))).ToList();
            preset.Mods = captured.Concat(manual)
                .OrderBy(rule => rule.Slots.Count == 0 ? int.MaxValue : rule.Slots.Min(SlotSortOrder))
                .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase).ToList();
            preset.AutomaticLayersScanned = true;
            DalamudServices.Log.Information(
                "WardrobeManager captured {Registered} equipped-item mod(s) for {Preset}; {Conflicting} are in pairwise equipment conflicts.",
                preset.RegisteredOutfitMods.Count, preset.Name, captured.Count);
            return captured.Count == 0
                ? LayerScanResult.Ok($"Registered {preset.RegisteredOutfitMods.Count} clothing/equipment mod{(preset.RegisteredOutfitMods.Count == 1 ? string.Empty : "s")} for priority application. No conflicts between those mods need to be shown.")
                : LayerScanResult.Ok($"Registered {preset.RegisteredOutfitMods.Count} clothing/equipment mod{(preset.RegisteredOutfitMods.Count == 1 ? string.Empty : "s")} for priority application; {captured.Count} appear in the conflict editor. Skin, body, and other non-equipment resources were ignored.");
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "WardrobeManager could not capture active outfit mods.");
            return LayerScanResult.Fail("Penumbra could not capture active outfit mods: " + ex.Message);
        }
    }

    public bool TryCreateGlamourerDesign(WardrobePreset preset, out string error)
    {
        error = string.Empty;
        if (preset.GlamourerDesignId != Guid.Empty || string.IsNullOrWhiteSpace(preset.GlamourerState)) return true;
        try
        {
            var result = addDesign.Invoke(preset.GlamourerState, preset.Name, out var designId);
            if (result != GlamourerApiEc.Success || designId == Guid.Empty)
            {
                error = $"Glamourer could not create the linked design ({result}).";
                return false;
            }
            preset.GlamourerDesignId = designId;
            return true;
        }
        catch (Exception ex)
        {
            error = "Glamourer could not create the linked design: " + ex.Message;
            return false;
        }
    }

    public bool RefreshOutfitFromGlamourer(WardrobePreset preset, out string error)
    {
        error = string.Empty;
        if (preset.GlamourerDesignId == Guid.Empty)
        {
            error = "This outfit is not linked to a Glamourer design yet.";
            return false;
        }

        try
        {
            var designs = getDesignList.Invoke();
            if (!designs.TryGetValue(preset.GlamourerDesignId, out var name))
            {
                error = "The linked Glamourer design no longer exists.";
                return false;
            }

            var state = getDesignBase64.Invoke(preset.GlamourerDesignId);
            var data = ParseDesignObject(getDesignJObject.InvokeFunc(preset.GlamourerDesignId));
            if (string.IsNullOrWhiteSpace(state) || data is null)
            {
                error = "Glamourer could not read the linked design.";
                return false;
            }

            preset.Name = string.IsNullOrWhiteSpace(name) ? preset.Name : name.Trim();
            preset.GlamourerState = state;
            var outfit = (JObject)data.DeepClone();
            preset.OutfitAppearanceJson = outfit.ToString(Newtonsoft.Json.Formatting.None);
            preset.EquipmentItemIds = ParseEquipmentItems(data);
            preset.Mods = ParseAssociatedMods(data).Select(ToWardrobeRule).ToList();
            preset.RegisteredOutfitMods = [];
            preset.AutomaticLayersScanned = false;
            return true;
        }
        catch (Exception ex)
        {
            error = "Glamourer could not refresh the linked design: " + ex.Message;
            return false;
        }
    }

    public bool TryGetEditableAppearance(WardrobePreset preset, out JObject? design, out string error)
    {
        design = null;
        error = string.Empty;
        try
        {
            var json = preset.Type == WardrobePresetType.Character ? preset.CharacterAppearanceJson : preset.OutfitAppearanceJson;
            design = preset.GlamourerDesignId == Guid.Empty
                ? (string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json))
                : ParseDesignObject(getDesignJObject.InvokeFunc(preset.GlamourerDesignId));
            if (design is null) { error = "Capture an appearance or reconnect the linked Glamourer design first."; return false; }
            OutfitAppearancePolicy.PreserveAndApply(design, null, preset.OutfitAppearanceOverrides, preset.AppearanceValueOverrides);
            return true;
        }
        catch (Exception ex) { error = "The appearance could not be read: " + ex.Message; return false; }
    }

    public bool SyncOutfitToGlamourer(WardrobePreset preset, string folderPath, out string message)
    {
        message = string.Empty;
        if (preset.Type != WardrobePresetType.Outfit)
        {
            message = "Only outfit presets can be synchronized as Glamourer outfit designs.";
            return false;
        }
        if (!OutfitAppearancePolicy.HasCapture(preset))
        {
            message = "Capture the current appearance before saving this outfit to Glamourer.";
            return false;
        }

        Guid seedId = Guid.Empty;
        try
        {
            var oldId = preset.GlamourerDesignId;
            JObject? oldDesign = null;
            if (oldId != Guid.Empty)
            {
                oldDesign = ParseDesignObject(getDesignJObject.InvokeFunc(oldId));
                if (oldDesign is null)
                {
                    message = "The linked Glamourer design could not be read. It was not replaced, so its appearance settings remain untouched.";
                    return false;
                }
            }

            // New outfits can be captured as JSON without a legacy Base64 state.
            var capture = string.IsNullOrWhiteSpace(preset.OutfitAppearanceJson)
                ? preset.GlamourerState : preset.OutfitAppearanceJson;
            var seeded = addDesign.Invoke(capture, preset.Name, out seedId);
            if (seeded != GlamourerApiEc.Success || seedId == Guid.Empty)
            {
                message = $"Glamourer could not create the outfit design ({seeded}).";
                return false;
            }

            var design = ParseDesignObject(getDesignJObject.InvokeFunc(seedId));
            if (design is null)
            {
                try { deleteDesign.Invoke(seedId); } catch { }
                seedId = Guid.Empty;
                message = "Glamourer could not export the outfit design for editing.";
                return false;
            }

            // Preserve Glamourer-owned presentation and behavior metadata when an
            // existing linked design is replaced. The captured state supplies the
            // new appearance; WardrobeManager supplies the manual mod associations.
            if (oldDesign is not null)
            {
                foreach (var property in new[]
                {
                    "Description", "ForcedRedraw", "ResetTemporarySettings",
                    "Color", "QuickDesign", "Tags", "Links", "ResetAdvancedDyes", "RevertAdvancedDyes",
                    "FileSystemFolder", "SortOrderName"
                })
                    if (oldDesign[property] is { } value) design[property] = value.DeepClone();
            }

            design["Name"] = preset.Name.Trim();
            // The preset editor is authoritative for folder placement. Imported
            // folders retain Glamourer's exact path, newly created folders use their
            // assigned path, and Unfiled deliberately moves the design to the root.
            design["FileSystemFolder"] = folderPath.Trim();
            design["Mods"] = new JArray(preset.Mods.Select(SerializeModAssociation));
            OutfitAppearancePolicy.PreserveAndApply(design, oldDesign, preset.OutfitAppearanceOverrides, preset.AppearanceValueOverrides);

            // Glamourer's AddDesign IPC determines filesystem placement from the
            // name argument (everything before its final slash), not from the
            // FileSystemFolder property in the imported JSON.
            var importName = DesignImportName(folderPath, preset.Name);
            var replaced = addDesign.Invoke(design.ToString(Newtonsoft.Json.Formatting.None), importName, out var newId);
            if (replaced != GlamourerApiEc.Success || newId == Guid.Empty)
            {
                try { deleteDesign.Invoke(seedId); } catch { }
                seedId = Guid.Empty;
                message = $"Glamourer could not save the edited outfit ({replaced}).";
                return false;
            }

            if (!TryVerifyDesignFolder(newId, folderPath, out var actualFolder))
            {
                try { deleteDesign.Invoke(newId); } catch { }
                try { deleteDesign.Invoke(seedId); } catch { }
                seedId = Guid.Empty;
                var expected = string.IsNullOrWhiteSpace(folderPath) ? "Glamourer's root" : folderPath.Trim();
                message = $"Glamourer created the replacement in '{actualFolder}' instead of '{expected}'. The old design was kept and the incorrect replacement was removed.";
                return false;
            }

            var stored = ParseDesignObject(getDesignJObject.InvokeFunc(newId));
            if (stored is null || !OutfitAppearancePolicy.MatchesSavedAppearance(design, stored))
            {
                try { deleteDesign.Invoke(newId); } catch { }
                try { deleteDesign.Invoke(seedId); } catch { }
                seedId = Guid.Empty;
                message = "Glamourer's saved appearance did not match the requested values and options. The previous design was kept; the unverified replacement was removed.";
                return false;
            }

            var seedDeleted = deleteDesign.Invoke(seedId);
            var oldDeleted = oldId == Guid.Empty ? GlamourerApiEc.NothingDone : deleteDesign.Invoke(oldId);
            preset.GlamourerDesignId = newId;
            preset.GlamourerState = getDesignBase64.Invoke(newId) ?? preset.GlamourerState;
            preset.OutfitAppearanceJson = stored.ToString(Newtonsoft.Json.Formatting.None);
            preset.OutfitAppearanceOverrides.Clear();
            preset.AppearanceValueOverrides.Clear();
            SelectQuickDesign(preset);
            var cleanupSucceeded = seedDeleted is GlamourerApiEc.Success or GlamourerApiEc.NothingDone
                && oldDeleted is GlamourerApiEc.Success or GlamourerApiEc.NothingDone;
            var savedLocation = string.IsNullOrWhiteSpace(folderPath) ? "Glamourer's root" : $"Glamourer folder {NormalizeGlamourerFolder(folderPath)}";
            message = cleanupSucceeded
                ? $"Saved {preset.Name} to {savedLocation} with {preset.Mods.Count} mod association{(preset.Mods.Count == 1 ? string.Empty : "s")}."
                : $"Saved the replacement Glamourer design, but a temporary or previous design could not be removed ({seedDeleted}; {oldDeleted}).";
            return true;
        }
        catch (Exception ex)
        {
            if (seedId != Guid.Empty)
            {
                try { deleteDesign.Invoke(seedId); } catch { }
            }
            message = "Glamourer could not save the outfit design: " + ex.Message;
            return false;
        }
    }

    public bool RefreshCharacterFromGlamourer(WardrobePreset preset, out string error)
    {
        error = string.Empty;
        if (preset.Type != WardrobePresetType.Character || preset.GlamourerDesignId == Guid.Empty)
        {
            error = "This character preset is not linked to a Glamourer design.";
            return false;
        }

        try
        {
            var designs = getDesignList.Invoke();
            if (!designs.TryGetValue(preset.GlamourerDesignId, out var name))
            {
                error = "The linked Glamourer character design no longer exists.";
                return false;
            }
            var design = ParseDesignObject(getDesignJObject.InvokeFunc(preset.GlamourerDesignId));
            if (design is null)
            {
                error = "Glamourer could not read the linked character design.";
                return false;
            }
            RestrictToCharacterAppearance(design);
            preset.Name = string.IsNullOrWhiteSpace(name) ? preset.Name : name.Trim();
            preset.CharacterAppearanceJson = design.ToString(Newtonsoft.Json.Formatting.None);
            preset.GlamourerState = getDesignBase64.Invoke(preset.GlamourerDesignId) ?? preset.GlamourerState;
            preset.GlamourerFolderPath = design.Value<string>("FileSystemFolder")?.Trim() ?? preset.GlamourerFolderPath;
            preset.Mods = ParseAssociatedMods(design).Select(ToWardrobeRule).ToList();
            return true;
        }
        catch (Exception ex)
        {
            error = "Glamourer could not refresh the linked character design: " + ex.Message;
            return false;
        }
    }

    public bool SyncCharacterToGlamourer(WardrobePreset preset, out string message)
    {
        message = string.Empty;
        if (preset.Type != WardrobePresetType.Character)
        {
            message = "Only character presets can be synchronized as Glamourer character designs.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(preset.CharacterAppearanceJson))
        {
            message = "Capture the current physical appearance before saving this character to Glamourer.";
            return false;
        }

        try
        {
            var oldId = preset.GlamourerDesignId;
            var oldDesign = oldId == Guid.Empty ? null : ParseDesignObject(getDesignJObject.InvokeFunc(oldId));
            if (oldId != Guid.Empty && oldDesign is null)
            {
                message = "The linked character design could not be read. The existing design was kept.";
                return false;
            }
            var design = JObject.Parse(preset.CharacterAppearanceJson);
            RestrictToCharacterAppearance(design);

            if (oldDesign is not null)
            {
                foreach (var property in new[]
                {
                    "Description", "ForcedRedraw", "ResetAdvancedDyes", "ResetTemporarySettings",
                    "RevertAdvancedDyes", "Color", "QuickDesign", "Tags", "Links",
                    "FileSystemFolder", "SortOrderName"
                })
                    if (oldDesign[property] is { } value) design[property] = value.DeepClone();
                if (string.IsNullOrWhiteSpace(preset.GlamourerFolderPath))
                    preset.GlamourerFolderPath = oldDesign.Value<string>("FileSystemFolder")?.Trim() ?? string.Empty;
            }

            design["Name"] = preset.Name.Trim();
            design["FileSystemFolder"] = preset.GlamourerFolderPath.Trim();
            design["QuickDesign"] = false;
            design["Mods"] = new JArray(preset.Mods.Select(SerializeModAssociation));
            design.Remove("Links");
            design.Remove("ResetAdvancedDyes");
            design.Remove("RevertAdvancedDyes");
            if (preset.AppearanceValueOverrides.Count > 0)
                OutfitAppearancePolicy.PreserveAndApply(design, oldDesign, preset.OutfitAppearanceOverrides, preset.AppearanceValueOverrides);
            RestrictToCharacterAppearance(design);

            var importName = DesignImportName(preset.GlamourerFolderPath, preset.Name);
            var result = addDesign.Invoke(design.ToString(Newtonsoft.Json.Formatting.None), importName, out var newId);
            if (result != GlamourerApiEc.Success || newId == Guid.Empty)
            {
                message = $"Glamourer could not save the character design ({result}).";
                return false;
            }
            if (!TryVerifyDesignFolder(newId, preset.GlamourerFolderPath, out var actualFolder))
            {
                try { deleteDesign.Invoke(newId); } catch { }
                message = $"Glamourer saved the character design in '{actualFolder}' instead of its existing folder. The old design was kept.";
                return false;
            }

            var stored = ParseDesignObject(getDesignJObject.InvokeFunc(newId));
            if (stored is null || !OutfitAppearancePolicy.MatchesSavedAppearance(design, stored))
            {
                try { deleteDesign.Invoke(newId); } catch { }
                message = "Glamourer did not retain the requested character appearance. The previous design was kept.";
                return false;
            }
            var oldDeleted = oldId == Guid.Empty ? GlamourerApiEc.NothingDone : deleteDesign.Invoke(oldId);
            preset.GlamourerDesignId = newId;
            preset.GlamourerState = getDesignBase64.Invoke(newId) ?? string.Empty;
            preset.CharacterAppearanceJson = stored.ToString(Newtonsoft.Json.Formatting.None);
            preset.AppearanceValueOverrides.Clear();
            preset.OutfitAppearanceOverrides.Clear();
            var location = string.IsNullOrWhiteSpace(preset.GlamourerFolderPath)
                ? "Glamourer's root" : $"Glamourer folder {NormalizeGlamourerFolder(preset.GlamourerFolderPath)}";
            message = oldDeleted is GlamourerApiEc.Success or GlamourerApiEc.NothingDone
                ? $"Saved {preset.Name} to {location} with physical customizations, customize parameters, and {preset.Mods.Count} mod association{(preset.Mods.Count == 1 ? string.Empty : "s")}."
                : $"Saved the replacement character design, but Glamourer could not remove the previous design ({oldDeleted}).";
            return true;
        }
        catch (Exception ex)
        {
            message = "Glamourer could not save the character design: " + ex.Message;
            return false;
        }
    }

    public bool EnsureGlamourerFolder(string folderPath, out string error)
    {
        error = string.Empty;
        var normalized = NormalizeGlamourerFolder(folderPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Enter a valid Glamourer folder name.";
            return false;
        }

        try
        {
            return UpdatePersistedGlamourerFolder(normalized, true, out error);
        }
        catch (Exception ex)
        {
            error = "Glamourer could not create the folder: " + ex.Message;
            return false;
        }
    }

    public bool RemovePersistedGlamourerFolder(string folderPath, out string error)
    {
        error = string.Empty;
        var normalized = NormalizeGlamourerFolder(folderPath);
        if (string.IsNullOrWhiteSpace(normalized)) return true;

        try
        {
            if (!DeleteLiveFolderMarkers(normalized, out error)) return false;
            CleanupAllFolderMarkerBackups();
            var occupied = getDesignListExtended.Invoke().Values
                .Select(design => ParentFolder(design.Item2))
                .Any(parent => parent.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                    || parent.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
            if (occupied)
            {
                error = $"Glamourer folder {normalized} still contains a design that WardrobeManager does not manage. Move that design elsewhere, then remove the folder again.";
                return false;
            }

            return UpdatePersistedGlamourerFolder(normalized, false, out error);
        }
        catch (Exception ex)
        {
            error = "WardrobeManager could not remove the Glamourer folder record: " + ex.Message;
            return false;
        }
    }

    public int CleanupLegacyFolderMarkers(IReadOnlyCollection<string> retainedFolders, out string error)
    {
        error = string.Empty;
        try
        {
            var markers = getDesignListExtended.Invoke()
                .Where(pair => pair.Value.Item1.Equals(FolderSetupDesignName, StringComparison.Ordinal))
                .Select(pair => (Id: pair.Key, Folder: ParentFolder(pair.Value.Item2)))
                .ToList();
            foreach (var marker in markers)
            {
                var deleted = deleteDesign.Invoke(marker.Id);
                if (deleted is not (GlamourerApiEc.Success or GlamourerApiEc.NothingDone))
                {
                    error = $"Glamourer could not remove a legacy WardrobeManager folder marker ({deleted}).";
                    return 0;
                }
                CleanupTemporaryMarkerFiles(marker.Id);
            }
            var backupMarkers = CleanupAllFolderMarkerBackups();
            if (backupMarkers > 0) MirrorCurrentOrganizationToBackup();
            var retained = retainedFolders
                .Select(NormalizeGlamourerFolder)
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in markers.Select(marker => marker.Folder)
                         .Where(folder => !string.IsNullOrWhiteSpace(folder))
                         .Where(folder => !retained.Contains(NormalizeGlamourerFolder(folder)))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!RemovePersistedGlamourerFolder(folder, out var folderError))
                    DalamudServices.Log.Debug("WardrobeManager kept Glamourer folder {Folder} after marker cleanup: {Reason}", folder, folderError);
            }
            return markers.Count + backupMarkers;
        }
        catch (Exception ex)
        {
            error = "WardrobeManager could not clean up legacy Glamourer folder markers: " + ex.Message;
            return 0;
        }
    }

    private static string DesignImportName(string folderPath, string designName)
    {
        var folder = NormalizeGlamourerFolder(folderPath);
        var name = designName.Replace('/', ' ').Replace('\\', ' ').Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "WardrobeManager Outfit";
        return string.IsNullOrWhiteSpace(folder) ? name : $"{folder}/{name}";
    }

    private static string NormalizeGlamourerFolder(string folderPath)
        => (folderPath ?? string.Empty).Replace('\\', '/').Trim('/').Trim();

    private static string ParentFolder(string designPath)
    {
        var normalized = NormalizeGlamourerFolder(designPath);
        var split = normalized.LastIndexOf('/');
        return split < 0 ? string.Empty : normalized[..split];
    }

    private static bool RemoveFolderEntries(JObject? entries, string folderPath)
    {
        if (entries is null) return false;
        var matches = entries.Properties()
            .Where(property => property.Name.Equals(folderPath, StringComparison.OrdinalIgnoreCase)
                || property.Name.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var match in matches) match.Remove();
        return matches.Count > 0;
    }

    private static bool AddFolderEntries(JObject entries, string folderPath)
    {
        var changed = false;
        var segments = NormalizeGlamourerFolder(folderPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var count = 1; count <= segments.Length; ++count)
        {
            var path = string.Join('/', segments.Take(count));
            if (entries.Property(path, StringComparison.OrdinalIgnoreCase) is not null) continue;
            entries[path] = new JObject();
            changed = true;
        }
        return changed;
    }

    private static bool UpdatePersistedGlamourerFolder(string folderPath, bool create, out string error)
    {
        error = string.Empty;
        var pluginConfigs = FindPluginConfigsRoot();
        if (pluginConfigs is null)
        {
            error = "WardrobeManager could not locate Glamourer's filesystem configuration.";
            return false;
        }

        var organizationPath = Path.Combine(pluginConfigs.FullName, "Glamourer", "design_filesystem", "organization.json");
        if (!File.Exists(organizationPath))
        {
            error = "Glamourer's folder configuration is unavailable.";
            return false;
        }

        var paths = new[] { organizationPath, organizationPath + ".bak" };
        foreach (var path in paths)
        {
            var sourcePath = File.Exists(path) ? path : organizationPath;
            var organization = JObject.Parse(File.ReadAllText(sourcePath));
            var folders = organization["Folders"] as JObject;
            if (folders is null)
            {
                folders = new JObject();
                organization["Folders"] = folders;
            }
            var separators = organization["Separators"] as JObject;
            if (separators is null)
            {
                separators = new JObject();
                organization["Separators"] = separators;
            }

            var changed = create
                ? AddFolderEntries(folders, folderPath)
                : RemoveFolderEntries(folders, folderPath) | RemoveFolderEntries(separators, folderPath);
            if (changed || !File.Exists(path)) WriteJsonAtomically(path, organization);
        }
        return true;
    }

    private static void MirrorCurrentOrganizationToBackup()
    {
        var pluginConfigs = FindPluginConfigsRoot();
        if (pluginConfigs is null) return;
        var currentPath = Path.Combine(pluginConfigs.FullName, "Glamourer", "design_filesystem", "organization.json");
        if (!File.Exists(currentPath)) return;
        try
        {
            var organization = JObject.Parse(File.ReadAllText(currentPath));
            WriteJsonAtomically(currentPath + ".bak", organization);
        }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not synchronize Glamourer's folder backup."); }
    }

    private static void WriteJsonAtomically(string path, JObject value)
    {
        var temporaryPath = path + ".wardrobemanager.tmp";
        File.WriteAllText(temporaryPath, value.ToString(Newtonsoft.Json.Formatting.Indented));
        File.Move(temporaryPath, path, true);
    }

    private static DirectoryInfo? FindPluginConfigsRoot()
    {
        DirectoryInfo? root = DalamudServices.PluginInterface.ConfigDirectory;
        while (root is not null && !root.Name.Equals("pluginConfigs", StringComparison.OrdinalIgnoreCase))
            root = root.Parent;
        return root;
    }

    private static string? HonorificConfigPath()
    {
        var root = FindPluginConfigsRoot();
        return root is null ? null : Path.Combine(root.FullName, "Honorific.json");
    }

    private static JObject? ReadHonorificCharacterConfig()
    {
        var path = HonorificConfigPath();
        if (path is null || !File.Exists(path) || !DalamudServices.PlayerState.IsLoaded) return null;
        var root = JObject.Parse(File.ReadAllText(path));
        var worlds = root["WorldCharacterDictionary"] as JObject;
        var identity = ResolveHonorificIdentity(root);
        return worlds?[identity.World.ToString()]?[identity.Name] as JObject;
    }

    private static (string Name, uint World) ResolveHonorificIdentity(JObject root)
    {
        var name = DalamudServices.PlayerState.CharacterName.Trim();
        var world = DalamudServices.PlayerState.HomeWorld.RowId;
        if (DalamudServices.PlayerState.ContentId == 0 || root["IdentifyAs"] is not JObject identities)
            return (name, world);

        var mapped = identities[DalamudServices.PlayerState.ContentId.ToString()];
        if (mapped is JObject tuple)
        {
            var mappedName = tuple.Value<string>("Item1")?.Trim();
            var mappedWorld = tuple.Value<uint?>("Item2");
            if (!string.IsNullOrWhiteSpace(mappedName) && mappedWorld is > 0)
                return (mappedName, mappedWorld.Value);
        }
        else if (mapped is JArray array && array.Count >= 2)
        {
            var mappedName = array[0]?.Value<string>()?.Trim();
            var mappedWorld = array[1]?.Value<uint?>();
            if (!string.IsNullOrWhiteSpace(mappedName) && mappedWorld is > 0)
                return (mappedName, mappedWorld.Value);
        }
        return (name, world);
    }

    public bool StageManualHonorificTitle(WardrobePreset preset, out string error)
    {
        error = string.Empty;
        try
        {
            if (!DalamudServices.PlayerState.IsLoaded)
            {
                error = "Log in before saving an Honorific title.";
                return false;
            }
            var path = HonorificConfigPath();
            if (path is null || !File.Exists(path))
            {
                error = "Honorific's configuration could not be found.";
                return false;
            }
            var titleName = preset.HonorificTitleName.Trim();
            if (titleName.Length is 0 or > 32 || titleName.Any(char.IsControl))
            {
                error = "Honorific titles must contain 1 to 32 printable characters.";
                return false;
            }

            var exposed = DalamudServices.PluginInterface.InstalledPlugins.FirstOrDefault(plugin =>
                plugin.InternalName.Equals("Honorific", StringComparison.OrdinalIgnoreCase) && plugin.IsLoaded);
            if (exposed is null)
            {
                error = "Honorific must be enabled before adding a new title.";
                return false;
            }

            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var localPluginField = exposed.GetType().GetFields(flags).FirstOrDefault(field =>
                field.FieldType.FullName?.Equals("Dalamud.Plugin.Internal.Types.LocalPlugin", StringComparison.Ordinal) == true);
            var localPlugin = localPluginField?.GetValue(exposed);
            var instance = localPlugin?.GetType().GetField("instance", flags)?.GetValue(localPlugin);
            var liveConfig = instance?.GetType().GetProperty("Config", flags)?.GetValue(instance);
            if (liveConfig is null)
            {
                error = "WardrobeManager could not access Honorific's live configuration.";
                return false;
            }

            var root = JObject.Parse(File.ReadAllText(path));
            var identity = ResolveHonorificIdentity(root);
            var characterName = identity.Name;

            var worldsField = liveConfig.GetType().GetField("WorldCharacterDictionary", flags);
            if (worldsField?.GetValue(liveConfig) is not System.Collections.IDictionary worlds)
            {
                error = "Honorific's live character dictionary is unavailable.";
                return false;
            }
            var world = worlds[identity.World] as System.Collections.IDictionary;
            if (world is null)
            {
                var worldType = worlds.GetType().GetGenericArguments()[1];
                world = Activator.CreateInstance(worldType) as System.Collections.IDictionary;
                if (world is null)
                {
                    error = "WardrobeManager could not create Honorific's world title list.";
                    return false;
                }
                worlds[identity.World] = world;
            }
            var character = world[characterName];
            if (character is null)
            {
                var characterType = liveConfig.GetType().Assembly.GetType("Honorific.CharacterConfig");
                character = characterType is null ? null : Activator.CreateInstance(characterType);
                if (character is null)
                {
                    error = "WardrobeManager could not create Honorific's character title list.";
                    return false;
                }
                world[characterName] = character;
            }
            var titlesField = character.GetType().GetField("CustomTitles", flags);
            if (titlesField?.GetValue(character) is not System.Collections.IList titles)
            {
                error = "Honorific's custom title list is unavailable.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(preset.HonorificTitleId))
                preset.HonorificTitleId = "uid:wm" + preset.Id.ToString("N")[..12];
            object? title = null;
            foreach (var candidate in titles)
            {
                if (candidate is null) continue;
                var uid = candidate.GetType().GetField("UniqueId", flags)?.GetValue(candidate) as string;
                if (!string.Equals(uid, preset.HonorificTitleId, StringComparison.Ordinal)) continue;
                title = candidate;
                break;
            }
            if (title is null)
            {
                var titleType = liveConfig.GetType().Assembly.GetType("Honorific.CustomTitle");
                title = titleType is null ? null : Activator.CreateInstance(titleType);
                if (title is null)
                {
                    error = "WardrobeManager could not create an Honorific title entry.";
                    return false;
                }
                titles.Add(title);
            }

            SetHonorificField(title, "Title", titleName, flags);
            SetHonorificField(title, "IsPrefix", preset.HonorificCustomIsPrefix, flags);
            SetHonorificField(title, "IsOriginal", false, flags);
            SetHonorificField(title, "UniqueId", preset.HonorificTitleId, flags);
            SetHonorificField(title, "Enabled", false, flags);
            SetHonorificEnumField(title, "TitleCondition", (int)preset.HonorificCondition, flags);
            SetHonorificField(title, "ConditionParam0", preset.HonorificConditionParam, flags);
            SetHonorificField(title, "GradientColourSet",
                preset.HonorificEffectPalette >= -1 ? preset.HonorificEffectPalette : null, flags);
            SetHonorificEnumField(title, "GradientAnimationStyle",
                preset.HonorificEffectPalette >= -1 ? (int)preset.HonorificEffectAnimation : null, flags);
            SetHonorificField(title, "Color", preset.HonorificUseColor
                ? new System.Numerics.Vector3(preset.HonorificColorR, preset.HonorificColorG, preset.HonorificColorB) : null, flags);
            SetHonorificField(title, "Glow", preset.HonorificEffectPalette == -1 || preset.HonorificUseGlow
                ? new System.Numerics.Vector3(preset.HonorificGlowR, preset.HonorificGlowG, preset.HonorificGlowB) : null, flags);
            SetHonorificField(title, "Color3", preset.HonorificEffectPalette == -1
                ? new System.Numerics.Vector3(preset.HonorificEffectColor2R, preset.HonorificEffectColor2G,
                    preset.HonorificEffectColor2B) : null, flags);

            object? location = null;
            if (preset.HonorificCondition == WardrobeHonorificCondition.Location)
            {
                var locationType = liveConfig.GetType().Assembly.GetType("Honorific.LocationCondition");
                location = locationType is null ? null : Activator.CreateInstance(locationType);
                if (location is not null)
                    SetHonorificField(location, "TerritoryType", preset.HonorificTerritoryId, flags);
            }
            SetHonorificField(title, "LocationCondition", location, flags);

            var retained = titles.Cast<object>().Any(item =>
                string.Equals(item.GetType().GetField("UniqueId", flags)?.GetValue(item) as string,
                    preset.HonorificTitleId, StringComparison.Ordinal)
                && string.Equals(item.GetType().GetField("Title", flags)?.GetValue(item) as string,
                    titleName, StringComparison.Ordinal));
            if (!retained)
            {
                error = $"Honorific did not accept {titleName} in its live title list.";
                return false;
            }
            preset.HonorificUsesExistingTitle = false;
            DalamudServices.Log.Information(
                "WardrobeManager staged Honorific title {Title} ({Uid}) for {Character} on world {World}; Honorific will persist it during unload.",
                titleName, preset.HonorificTitleId, characterName, identity.World);
            return true;
        }
        catch (Exception ex)
        {
            error = "WardrobeManager could not save the Honorific title: " + ex.Message;
            return false;
        }
    }

    private static void SetHonorificField(object target, string name, object? value,
        System.Reflection.BindingFlags flags)
    {
        var field = target.GetType().GetField(name, flags)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static void SetHonorificEnumField(object target, string name, int? value,
        System.Reflection.BindingFlags flags)
    {
        var field = target.GetType().GetField(name, flags)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        if (value is null)
        {
            field.SetValue(target, null);
            return;
        }
        var enumType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        field.SetValue(target, Enum.ToObject(enumType, value.Value));
    }

    public bool IsHonorificReady()
    {
        try
        {
            _ = DalamudServices.PluginInterface
                .GetIpcSubscriber<(uint Major, uint Minor)>("Honorific.ApiVersion").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    public bool IsHonorificLoaded()
    {
        try
        {
            return DalamudServices.PluginInterface.InstalledPlugins.Any(plugin =>
                plugin.InternalName.Equals("Honorific", StringComparison.OrdinalIgnoreCase) && plugin.IsLoaded);
        }
        catch { return IsHonorificReady(); }
    }

    public DateTime HonorificConfigLastWriteUtc()
    {
        try
        {
            var path = HonorificConfigPath();
            return path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    public bool IsHonorificTitleAvailable(WardrobePreset preset)
    {
        if (!preset.HonorificTitleConfigured || preset.HonorificUsesExistingTitle
            || string.IsNullOrWhiteSpace(preset.HonorificTitleName)) return true;
        if (IsHonorificReady() && TryReadLiveHonorificTitles(out var live))
            return live.Any(title =>
                string.Equals(title.Value<string>("Title")?.Trim(), preset.HonorificTitleName.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && (title.Value<bool?>("IsPrefix") ?? false) == preset.HonorificCustomIsPrefix);
        return GetHonorificTitles().Any(title =>
            (!string.IsNullOrWhiteSpace(preset.HonorificTitleId)
                && title.Id.Equals(preset.HonorificTitleId, StringComparison.Ordinal))
            || (title.Name.Equals(preset.HonorificTitleName.Trim(), StringComparison.OrdinalIgnoreCase)
                && title.IsPrefix == preset.HonorificCustomIsPrefix));
    }

    public bool IsHonorificTitlePersisted(WardrobePreset preset)
    {
        if (!preset.HonorificTitleConfigured || preset.HonorificUsesExistingTitle
            || string.IsNullOrWhiteSpace(preset.HonorificTitleName)
            || string.IsNullOrWhiteSpace(preset.HonorificTitleId)) return true;
        try
        {
            var character = ReadHonorificCharacterConfig();
            return character?["CustomTitles"]?.OfType<JObject>().Any(title =>
                string.Equals(title.Value<string>("UniqueId"), preset.HonorificTitleId,
                    StringComparison.Ordinal)
                && string.Equals(title.Value<string>("Title")?.Trim(), preset.HonorificTitleName.Trim(),
                    StringComparison.Ordinal)
                && (title.Value<bool?>("IsPrefix") ?? false) == preset.HonorificCustomIsPrefix) == true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "WardrobeManager could not verify the persisted Honorific title.");
            return false;
        }
    }

    private static JObject SerializeHonorificColor(float r, float g, float b) => new()
    {
        ["$type"] = "System.Nullable`1[[System.Numerics.Vector3, System.Private.CoreLib]], System.Private.CoreLib",
        ["X"] = Math.Clamp(r, 0f, 1f),
        ["Y"] = Math.Clamp(g, 0f, 1f),
        ["Z"] = Math.Clamp(b, 0f, 1f),
    };

    private static JObject NewHonorificCharacterConfig() => new()
    {
        ["$type"] = "Honorific.CharacterConfig, Honorific",
        ["DefaultTitle"] = new JObject { ["$type"] = "Honorific.CustomTitle, Honorific", ["Title"] = string.Empty },
        ["Override"] = new JObject { ["$type"] = "Honorific.CustomTitle, Honorific", ["Title"] = string.Empty },
        ["CustomTitles"] = new JArray(),
        ["UseRandom"] = false,
        ["RandomOnZoneChange"] = false,
        ["RandomOnTimer"] = false,
        ["RandomTimerDuration"] = 10,
    };

    private bool DeleteLiveFolderMarkers(string folderPath, out string error)
    {
        error = string.Empty;
        try
        {
            var markers = getDesignListExtended.Invoke()
                .Where(pair => pair.Value.Item1.Equals(FolderSetupDesignName, StringComparison.Ordinal)
                    && ParentFolder(pair.Value.Item2).Equals(folderPath, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var marker in markers)
            {
                var deleted = deleteDesign.Invoke(marker);
                if (deleted is not (GlamourerApiEc.Success or GlamourerApiEc.NothingDone))
                {
                    error = $"Glamourer could not remove WardrobeManager's temporary folder marker ({deleted}).";
                    return false;
                }
                CleanupTemporaryMarkerFiles(marker);
            }

            var remaining = getDesignListExtended.Invoke().Any(pair =>
                pair.Value.Item1.Equals(FolderSetupDesignName, StringComparison.Ordinal)
                && ParentFolder(pair.Value.Item2).Equals(folderPath, StringComparison.OrdinalIgnoreCase));
            if (!remaining) return true;
            error = "Glamourer still reports WardrobeManager's temporary folder marker after deletion.";
            return false;
        }
        catch (Exception ex)
        {
            error = "WardrobeManager could not clean up its temporary Glamourer folder marker: " + ex.Message;
            return false;
        }
    }

    private static void CleanupTemporaryMarkerFiles(Guid markerId)
    {
        var root = FindPluginConfigsRoot();
        if (root is null) return;
        var designFolder = Path.Combine(root.FullName, "Glamourer", "designs");
        foreach (var suffix in new[] { ".json.bak", ".json.tmp" })
        {
            var path = Path.Combine(designFolder, markerId + suffix);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not remove temporary marker file {Path}.", path); }
        }
    }

    private static int CleanupAllFolderMarkerBackups()
    {
        var root = FindPluginConfigsRoot();
        if (root is null) return 0;
        var designFolder = Path.Combine(root.FullName, "Glamourer", "designs");
        if (!Directory.Exists(designFolder)) return 0;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(designFolder, "*.json.bak", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var data = JObject.Parse(File.ReadAllText(path));
                if (!string.Equals(data.Value<string>("Name"), FolderSetupDesignName, StringComparison.Ordinal)) continue;
                File.Delete(path);
                ++deleted;
            }
            catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager skipped temporary marker backup {Path}.", path); }
        }
        return deleted;
    }

    private bool TryVerifyDesignFolder(Guid designId, string expectedFolder, out string actualFolder)
    {
        actualFolder = "an unknown location";
        try
        {
            var designs = getDesignListExtended.Invoke();
            if (!designs.TryGetValue(designId, out var design)) return false;
            var fullPath = NormalizeGlamourerFolder(design.Item2);
            var split = fullPath.LastIndexOf('/');
            actualFolder = split < 0 ? string.Empty : fullPath[..split];
            return actualFolder.Equals(NormalizeGlamourerFolder(expectedFolder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not verify Glamourer folder placement for {DesignId}.", designId);
            return false;
        }
    }

    public bool DeleteLinkedGlamourerDesign(WardrobePreset preset, out string error)
    {
        error = string.Empty;
        if (preset.GlamourerDesignId == Guid.Empty) return true;
        try
        {
            var result = deleteDesign.Invoke(preset.GlamourerDesignId);
            if (result is GlamourerApiEc.Success or GlamourerApiEc.NothingDone) return true;
            error = $"Glamourer could not delete the linked design ({result}).";
            return false;
        }
        catch (Exception ex)
        {
            error = "Glamourer could not delete the linked design: " + ex.Message;
            return false;
        }
    }

    private static JObject SerializeModAssociation(WardrobeModRule rule)
    {
        var result = new JObject
        {
            ["Name"] = rule.Name,
            ["Directory"] = rule.Directory,
            ["Priority"] = rule.Priority,
            ["Settings"] = new JObject(rule.Options.Select(option =>
                new JProperty(option.Key, new JArray(option.Value))))
        };
        switch (rule.AssociationState)
        {
            case GlamourerModAssociationState.Enabled: result["Enabled"] = true; break;
            case GlamourerModAssociationState.Disabled: result["Enabled"] = false; break;
            case GlamourerModAssociationState.Inherit: result["Inherit"] = true; break;
            case GlamourerModAssociationState.Remove: result["Remove"] = true; break;
            default: result["Enabled"] = JValue.CreateNull(); break;
        }
        return result;
    }

    public void OpenLinkedDesign(WardrobePreset preset)
    {
        try { openDesign.Invoke(preset.GlamourerDesignId); }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not open the linked Glamourer design."); }
    }

    public IReadOnlyList<GlamourerQuickDesign> GetQuickDesigns()
    {
        try
        {
            return getDesignListExtended.Invoke()
                .Where(pair => pair.Value.Item4)
                .Select(pair => new GlamourerQuickDesign(pair.Key, pair.Value.Item1, pair.Value.Item2, pair.Value.Item3))
                .OrderBy(design => design.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not read Glamourer's Quick Design list.");
            return [];
        }
    }

    public Guid GetSelectedQuickDesign()
    {
        try
        {
            var root = FindPluginConfigsRoot();
            if (root is null) return Guid.Empty;
            var path = Path.Combine(root.FullName, "Glamourer", "ephemeral_config.json");
            if (!File.Exists(path)) return Guid.Empty;
            var data = JObject.Parse(File.ReadAllText(path));
            return Guid.TryParse(data.Value<string>("SelectedQuickDesign"), out var selected)
                ? selected : Guid.Empty;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not read Glamourer's selected Quick Design.");
            return Guid.Empty;
        }
    }

    public ApplyResult ApplyQuickDesign(Guid designId, string name)
    {
        try
        {
            var result = applyDesign.Invoke(designId, 0, 0, ApplyFlag.Once | ApplyFlag.Equipment | ApplyFlag.Customization);
            if (result != GlamourerApiEc.Success) return ApplyResult.Fail($"Glamourer could not apply {name} ({result}).");
            openQuickDesignBar.Invoke(false, designId);
            return ApplyResult.Ok($"Applied {name} through Glamourer.");
        }
        catch (Exception ex) { return ApplyResult.Fail("Glamourer is unavailable: " + ex.Message); }
    }

    public void SelectQuickDesign(WardrobePreset preset)
    {
        if (preset.GlamourerDesignId == Guid.Empty) return;
        try { openQuickDesignBar.Invoke(false, preset.GlamourerDesignId); }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not select the linked Glamourer Quick Design."); }
    }

    public bool IsAppearanceCurrent(WardrobePreset preset)
    {
        if (preset.EquipmentItemIds.Count == 0) return !string.IsNullOrWhiteSpace(preset.GlamourerState);
        try
        {
            var current = ParseEquipmentItems(ParseStateObject(getStateObject.InvokeFunc(0, 0)));
            // Weapons are job-dependent rather than part of the persistent outfit.
            // Comparing them caused an applied outfit to deactivate as soon as the
            // player changed jobs (and encoded empty off-hand values can also differ
            // across Glamourer state round-trips). Keep the outfit active while its
            // clothing, accessories, and facewear still match.
            return preset.EquipmentItemIds
                .Where(pair => NormalizeSlot(pair.Key) is not ("Main Hand" or "Off Hand"))
                .All(pair => current.TryGetValue(pair.Key, out var id) && id == pair.Value);
        }
        catch { return false; }
    }

    public GlamourerDesignScan ScanGlamourerDesigns()
    {
        try
        {
            var designs = new List<GlamourerDesign>();
            foreach (var pair in getDesignListExtended.Invoke().OrderBy(pair => pair.Value.Item1, StringComparer.OrdinalIgnoreCase))
            {
                var state = getDesignBase64.Invoke(pair.Key) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(state)) continue;
                var data = ParseDesignObject(getDesignJObject.InvokeFunc(pair.Key));
                designs.Add(new GlamourerDesign(
                    pair.Key,
                    pair.Value.Item1,
                    state,
                    DesignAppliesEquipment(data),
                    data?.Value<string>("FileSystemFolder")?.Trim() ?? string.Empty,
                    ParseEquipmentItems(data),
                    ParseAssociatedMods(data).Select(ToWardrobeRule).ToList(),
                    pair.Value.Item4,
                    CharacterAppearanceJson(data),
                    OutfitAppearanceJson(data)));
            }
            return new GlamourerDesignScan(true, designs, string.Empty);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not scan Glamourer designs.");
            return new GlamourerDesignScan(false, [], "Glamourer is unavailable: " + ex.Message);
        }
    }

    public ApplyResult Apply(WardrobePreset preset)
    {
        try
        {
            // Outfit designs are owned by Glamourer. Their saved mod associations,
            // automation rules, redraw behavior, and appearance are applied by
            // Glamourer itself; WardrobeManager only mirrors and selects the design.
            if (preset.Type == WardrobePresetType.Outfit)
            {
                if (preset.GlamourerDesignId == Guid.Empty)
                    return ApplyResult.Fail("Save this outfit to Glamourer before applying it.");
                return ApplyQuickDesign(preset.GlamourerDesignId, preset.Name);
            }

            var collection = ResolveCollection(preset);
            if (collection is null) return ApplyResult.Fail("Penumbra has no collection assigned to Yourself.");
            var collectionId = collection.Value.Id;
            var installed = getModList.Invoke();
            var failures = new List<string>();
            if (preset.Type == WardrobePresetType.Character && preset.PenumbraCollectionId != Guid.Empty)
            {
                var collectionName = collection.Value.Name.Trim();
                if (collectionName.Length == 0 || collectionName.Contains('|') || collectionName.Contains('\n') || collectionName.Contains('\r'))
                    failures.Add("Penumbra collection assignment (invalid collection name)");
                else
                {
                    // Use Penumbra's documented command form, then verify through its public
                    // object-assignment IPC so the current character is changed immediately.
                    DalamudServices.CommandManager.ProcessCommand($"/penumbra collection individual | {collectionName} | <me>");
                    var assigned = setCollectionForObject.Invoke(0, collection.Value.Id, true, false);
                    if (!IsSuccess(assigned.Item1)) failures.Add($"Penumbra collection assignment ({assigned.Item1})");
                }
            }
            if (preset.Type == WardrobePresetType.Character)
            {
                if (preset.GlamourerDesignId == Guid.Empty)
                    failures.Add("Glamourer character design (save this character to Glamourer first)");
                else
                {
                    var characterResult = applyDesign.Invoke(preset.GlamourerDesignId, 0, 0,
                        ApplyFlag.Once | ApplyFlag.Customization);
                    if (characterResult != GlamourerApiEc.Success)
                        failures.Add($"Glamourer character appearance ({characterResult})");
                }
                ApplyCustomizePlusProfile(preset, failures);
                ApplyHonorificTitle(preset, failures);
                redraw.Invoke(0, RedrawType.Redraw);
                return failures.Count == 0
                    ? ApplyResult.Ok($"Applied character preset {preset.Name}.")
                    : ApplyResult.Fail($"Applied with failures: {string.Join(", ", failures.Distinct())}.");
            }
            var layers = preset.Mods.Where(x => installed.ContainsKey(x.Directory))
                .GroupBy(x => x.Directory, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            var enabled = 0;
            var disabled = 0;
            foreach (var rule in layers)
            {
                var stateChanged = setMod.Invoke(collectionId, rule.Directory, rule.Enabled, Source);
                if (!IsSuccess(stateChanged))
                {
                    failures.Add(rule.Name);
                    continue;
                }
                if (!rule.Enabled)
                {
                    disabled++;
                    continue;
                }

                enabled++;
                var prioritized = setPriority.Invoke(collectionId, rule.Directory, rule.Priority, Source);
                if (!IsSuccess(prioritized)) failures.Add(rule.Name);
                foreach (var option in rule.Options)
                    if (!IsSuccess(setOptions.Invoke(collectionId, rule.Directory, option.Key, option.Value, Source))) failures.Add($"{rule.Name}: {option.Key}");
            }

            redraw.Invoke(0, RedrawType.Redraw);
            return failures.Count == 0
                ? ApplyResult.Ok($"Applied {preset.Name}: {enabled} listed layer(s) enabled, {disabled} listed layer(s) disabled; unlisted mods were unchanged.")
                : ApplyResult.Fail($"Applied with failures: {string.Join(", ", failures.Distinct())}.");
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "WardrobeManager could not apply {Preset}.", preset.Name);
            return ApplyResult.Fail("Penumbra or Glamourer is unavailable: " + ex.Message);
        }
    }

    public void ClearTemporaryOutfitOverrides(bool redrawPlayer = true)
    {
        if (activeTemporaryCollectionId == Guid.Empty) return;
        try
        {
            var collectionId = activeTemporaryCollectionId;
            var result = removeTemporarySettings.Invoke(collectionId, TemporarySettingsKey);
            activeTemporaryCollectionId = Guid.Empty;
            DalamudServices.Log.Information(
                "WardrobeManager removed temporary Penumbra priorities from collection {Collection} ({Result}).",
                collectionId, result);
            if (redrawPlayer) redraw.Invoke(0, RedrawType.Redraw);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "WardrobeManager could not clear its temporary Penumbra settings.");
        }
    }

    private static Dictionary<string, int> BuildCapturedPriorityBand(
        IReadOnlyList<WardrobeModRule> selected, WardrobePreset preset, int topPriority)
    {
        var registeredDirectories = preset.RegisteredOutfitMods
            .Select(rule => rule.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var captured = selected.Where(rule => registeredDirectories.Contains(rule.Directory)).ToList();
        var distinctPriorities = captured.Select(rule => rule.CapturedPriority).Distinct().OrderBy(value => value).ToList();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < distinctPriorities.Count; index++)
        {
            // Compress arbitrary permanent priorities into consecutive temporary values,
            // keeping equal priorities equal and the original highest layer at the
            // configured top value.
            var temporary = topPriority - (distinctPriorities.Count - 1 - index);
            foreach (var rule in captured.Where(rule => rule.CapturedPriority == distinctPriorities[index]))
                result[rule.Directory] = temporary;
        }
        return result;
    }

    public bool ActiveOverridesUseCurrentCollection()
    {
        if (activeTemporaryCollectionId == Guid.Empty) return true;
        try { return ResolveCurrentCollection()?.Id == activeTemporaryCollectionId; }
        catch { return false; }
    }

    private (Guid Id, string Name)? ResolveCollection(WardrobePreset preset)
    {
        if (preset.PenumbraCollectionId != Guid.Empty)
        {
            var match = GetCollections().FirstOrDefault(item => item.Id == preset.PenumbraCollectionId);
            if (match is not null) return (match.Id, match.Name);
        }
        try
        {
            var (validObject, _, effectiveCollection) = getCollectionForObject.Invoke(0);
            if (validObject && effectiveCollection.Id != Guid.Empty) return effectiveCollection;
        }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not resolve the current character's effective Penumbra collection."); }
        return getCollection.Invoke(ApiCollectionType.Yourself);
    }

    private (Guid Id, string Name)? ResolveCurrentCollection()
    {
        try
        {
            var (validObject, _, effectiveCollection) = getCollectionForObject.Invoke(0);
            if (validObject && effectiveCollection.Id != Guid.Empty) return effectiveCollection;
        }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "WardrobeManager could not resolve the current Penumbra collection."); }
        return getCollection.Invoke(ApiCollectionType.Yourself);
    }

    private static IEnumerable<string> ClassifyResourceSlots(string gamePath)
    {
        var key = "file:" + gamePath;
        var nailSlots = ClassifyNailSlots(key);
        if (nailSlots.Count > 0) return nailSlots;
        var slot = ClassifyConflictSlot(key);
        return slot is null ? [] : [NormalizeSlot(slot)];
    }

    private static IReadOnlyList<string> MatchEquipmentSlots(string gamePath,
        IReadOnlyList<EquipmentModel> targetModels)
    {
        var normalized = gamePath.Replace('\\', '/');
        return targetModels.Where(model => model.Tokens.All(token => normalized.Contains(token,
                StringComparison.OrdinalIgnoreCase)))
            .Select(model => NormalizeSlot(model.Slot)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsEquipmentConflictKey(string key, IReadOnlyList<EquipmentModel> targetModels)
        => MatchEquipmentConflictSlots(key, targetModels).Count > 0;

    private static IReadOnlyList<string> MatchEquipmentConflictSlots(string key,
        IReadOnlyList<EquipmentModel> targetModels)
    {
        if (!key.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return [];
        return MatchEquipmentSlots(key[5..], targetModels);
    }

    private static IEnumerable<string> ClassifyConflictKeySlots(string key)
    {
        var nailSlots = ClassifyNailSlots(key);
        if (nailSlots.Count > 0) return nailSlots;
        var slot = ClassifyConflictSlot(key);
        return slot is null ? [] : [NormalizeSlot(slot)];
    }

    private bool TryGetEnabledSettings(Guid collectionId, string directory, string name, out int priority)
        => TryGetEnabledSettings(collectionId, directory, name, out priority, out _);

    private bool TryGetEnabledSettings(Guid collectionId, string directory, string name, out int priority,
        out Dictionary<string, List<string>> options)
    {
        priority = 0;
        options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = getCurrentSettings.Invoke(collectionId, directory, name, false);
            if (!IsSuccess(result.Item1) || result.Item2 is not { } settings || !settings.Item1) return false;
            priority = settings.Item2;
            options = settings.Item3.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch { return false; }
    }

    private bool TryGetEffectiveEnabledSettings(Guid collectionId, string directory, string name, out int priority,
        out Dictionary<string, List<string>> options)
    {
        priority = 0;
        options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = getCurrentSettingsWithTemp.Invoke(collectionId, directory, name, false, false, TemporarySettingsKey);
            if (!IsSuccess(result.Item1) || result.Item2 is not { } settings || !settings.Item1) return false;
            priority = settings.Item2;
            options = settings.Item3.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch { return false; }
    }

    private static bool DesignAppliesEquipment(JObject? design)
    {
        if (design is null) return true;
        return AppliesInSection(design["Equipment"]) || AppliesInSection(design["Bonus"]);

        static bool AppliesInSection(JToken? section)
            => section is JContainer container && container.DescendantsAndSelf()
                .OfType<JProperty>()
                .Any(property => property.Name.StartsWith("Apply", StringComparison.OrdinalIgnoreCase)
                    && property.Value.Type == JTokenType.Boolean
                    && property.Value.Value<bool>());
    }

    private void ApplyCustomizePlusProfile(WardrobePreset preset, ICollection<string> failures)
    {
        if (preset.CustomizePlusProfileId == Guid.Empty) return;
        try
        {
            var profiles = getCustomizeProfiles.InvokeFunc();
            var selected = profiles.FirstOrDefault(profile => profile.UniqueId == preset.CustomizePlusProfileId);
            if (selected.UniqueId == Guid.Empty)
            {
                failures.Add("Customize+ profile (no longer exists)");
                return;
            }

            var characterName = DalamudServices.PlayerState.CharacterName.Trim();
            var homeWorld = (ushort)DalamudServices.PlayerState.HomeWorld.RowId;
            foreach (var profile in profiles.Where(profile => profile.IsEnabled
                         && profile.UniqueId != selected.UniqueId
                         && ProfileAppliesToCharacter(profile.Characters, characterName, homeWorld)))
                if (disableCustomizeProfile.InvokeFunc(profile.UniqueId) != 0)
                    failures.Add($"Customize+ disable {profile.Name}");

            if (enableCustomizeProfile.InvokeFunc(selected.UniqueId) != 0)
                failures.Add($"Customize+ enable {selected.Name}");
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not apply the selected Customize+ profile.");
            failures.Add("Customize+ profile");
        }
    }

    private void ApplyHonorificTitle(WardrobePreset preset, ICollection<string> failures)
    {
        if (!preset.HonorificTitleConfigured) return;
        try
        {
            // Clear the old WardrobeManager temporary IPC override once, then use
            // Honorific's own persistent enable/disable commands so its UI remains
            // authoritative and the user can change the active title afterward.
            try { clearHonorificTitle.InvokeAction(0); } catch { }
            DalamudServices.CommandManager.ProcessCommand("/honorific force clear");
            DalamudServices.CommandManager.ProcessCommand("/honorific title disable meta:all");
            if (!string.IsNullOrWhiteSpace(preset.HonorificTitleName))
            {
                var idExists = !string.IsNullOrWhiteSpace(preset.HonorificTitleId)
                    && GetHonorificTitles().Any(title =>
                        title.Id.Equals(preset.HonorificTitleId, StringComparison.Ordinal));
                var selector = idExists ? preset.HonorificTitleId.Trim() : preset.HonorificTitleName.Trim();
                if (selector.Any(char.IsControl)) throw new InvalidOperationException("The saved title selector is invalid.");
                DalamudServices.CommandManager.ProcessCommand($"/honorific title enable {selector}");
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not apply the selected Honorific title.");
            failures.Add("Honorific title");
        }
    }

    private static bool ProfileAppliesToCharacter(
        IReadOnlyCollection<(string Name, ushort WorldId, byte CharacterType, ushort CharacterSubType)> characters,
        string name, ushort world)
        => characters.Any(character => character.CharacterType == 1
            && character.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (character.WorldId == world || character.WorldId == ushort.MaxValue));

    private static void RestrictToCharacterAppearance(JObject design, bool initializeApply = false)
    {
        if (design["Equipment"] is JObject equipment)
            foreach (var property in equipment.DescendantsAndSelf().OfType<JProperty>()
                         .Where(property => property.Name.StartsWith("Apply", StringComparison.OrdinalIgnoreCase)).ToList())
                property.Value = false;

        if (design["Bonus"] is JObject bonus)
            foreach (var property in bonus.DescendantsAndSelf().OfType<JProperty>()
                         .Where(property => property.Name.StartsWith("Apply", StringComparison.OrdinalIgnoreCase)).ToList())
                property.Value = false;

        if (initializeApply && design["Customize"] is JObject customize)
            foreach (var property in customize.Properties())
            {
                if (property.Value is not JObject entry) continue;
                entry["Apply"] = !property.Name.Equals("Wetness", StringComparison.OrdinalIgnoreCase);
            }

        if (initializeApply && design["Parameters"] is JObject parameters)
            foreach (var entry in parameters.Properties().Select(property => property.Value).OfType<JObject>())
                entry["Apply"] = true;

        design.Remove("Materials");
    }

    private static void RestrictToOutfitAppearance(JObject design)
    {
        if (design["Customize"] is JObject customize)
            foreach (var entry in customize.Properties().Select(property => property.Value).OfType<JObject>())
                entry["Apply"] = false;

        if (design["Parameters"] is JObject parameters)
            foreach (var entry in parameters.Properties().Select(property => property.Value).OfType<JObject>())
                entry["Apply"] = false;
    }

    private static string CharacterAppearanceJson(JObject? source)
    {
        if (source is null) return string.Empty;
        var character = (JObject)source.DeepClone();
        RestrictToCharacterAppearance(character);
        return character.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string OutfitAppearanceJson(JObject? source)
    {
        if (source is null) return string.Empty;
        var outfit = (JObject)source.DeepClone();
        return outfit.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static JObject? ParseDesignObject(object? value)
    {
        if (value is null) return null;
        var json = value.ToString();
        return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
    }

    private static JObject? ParseStateObject(object? value)
    {
        if (value is null) return null;
        var type = value.GetType();
        var resultField = type.GetField("Item1");
        var resultProperty = type.GetProperty("Item1");
        var stateField = type.GetField("Item2");
        var stateProperty = type.GetProperty("Item2");

        // Depending on the Dalamud IPC bridge and plugin load contexts, an object-
        // typed subscriber can receive either the complete boxed result tuple or the
        // JObject payload directly. Some bridges serialize the tuple itself into a
        // JObject with Item1/Item2 keys, so unwrap that JSON shape as well.
        if (resultField is null && resultProperty is null && stateField is null && stateProperty is null)
        {
            var serialized = ParseDesignObject(value);
            if (serialized?["Equipment"] is JObject) return serialized;
            if (serialized?["Item2"] is JObject payload && IsSuccessfulStateResult(serialized["Item1"]?.ToString()))
                return payload;
            return null;
        }

        var result = resultField?.GetValue(value) ?? resultProperty?.GetValue(value);
        var state = stateField?.GetValue(value) ?? stateProperty?.GetValue(value);

        // Glamourer's public wrapper exposes GlamourerApiEc, while the underlying
        // IPC provider boxes its success code as an integer. The object boundary is
        // intentional because Glamourer's JObject lives in another plugin load
        // context, so accept both representations before serializing Item2 to text.
        var success = result is GlamourerApiEc code
            ? code == GlamourerApiEc.Success
            : IsSuccessfulStateResult(result?.ToString());
        return success ? ParseDesignObject(state) : null;
    }

    private static bool IsSuccessfulStateResult(string? value)
        => string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, GlamourerApiEc.Success.ToString(), StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, uint> ParseEquipmentItems(JObject? design)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (design?["Equipment"] is JObject equipment)
        {
            foreach (var property in equipment.Properties())
            {
                if (property.Value is not JObject item || !TryReadPositiveUInt32(item["ItemId"], out var id)) continue;
                if (item.Value<bool?>("Apply") != true) continue;
                result[NormalizeSlot(property.Name)] = id;
            }
        }
        if (design?["Bonus"]?["Glasses"] is JObject glasses
            && glasses.Value<bool?>("Apply") == true
            && TryReadPositiveUInt64(glasses["BonusId"], out var encodedGlassesId))
        {
            // Glamourer's BonusItemId is an encoded 64-bit value. The actual
            // Glasses sheet row is its ushort BonusItem component; the upper bits
            // identify the custom item kind. A zero row is Glamourer's Nothing item.
            var glassesId = (uint)(encodedGlassesId & ushort.MaxValue);
            if (glassesId != 0) result["Facewear"] = glassesId;
        }
        return result;
    }

    private static bool TryReadPositiveUInt32(JToken? token, out uint value)
    {
        value = 0;
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined) return false;

        // Glamourer uses negative sentinel values for some unapplied bonus/equipment
        // entries. Newtonsoft's direct uint conversion throws on those values, which
        // previously aborted the entire outfit scan. Parse defensively so one sentinel
        // or malformed entry is ignored while all real equipment continues to scan.
        return ulong.TryParse(token.ToString(), System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var parsed)
               && parsed is > 0 and <= uint.MaxValue
               && (value = (uint)parsed) > 0;
    }

    private static bool TryReadPositiveUInt64(JToken? token, out ulong value)
    {
        value = 0;
        return token is not null
               && token.Type is not (JTokenType.Null or JTokenType.Undefined)
               && ulong.TryParse(token.ToString(), System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out value)
               && value > 0;
    }

    private static IReadOnlyList<DesignModAssociation> ParseAssociatedMods(JObject? design)
    {
        if (design?["Mods"] is not JArray mods) return [];
        var result = new List<DesignModAssociation>();
        foreach (var mod in mods.OfType<JObject>())
        {
            var directory = mod.Value<string>("Directory")?.Trim() ?? string.Empty;
            if (directory.Length == 0) continue;
            var name = mod.Value<string>("Name")?.Trim();
            var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (mod["Settings"] is JObject settings)
            {
                foreach (var setting in settings.Properties())
                    options[setting.Name] = setting.Value is JArray values
                        ? values.Values<string>().OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
                        : [];
            }
            var state = mod.Value<bool?>("Remove") == true
                ? GlamourerModAssociationState.Remove
                : mod.Value<bool?>("Inherit") == true
                    ? GlamourerModAssociationState.Inherit
                    : mod["Enabled"]?.Type == JTokenType.Null
                        ? GlamourerModAssociationState.Ignore
                        : mod.Value<bool?>("Enabled") switch
                        {
                            true => GlamourerModAssociationState.Enabled,
                            false => GlamourerModAssociationState.Disabled,
                            _ => GlamourerModAssociationState.Ignore,
                        };
            result.Add(new DesignModAssociation(
                directory,
                string.IsNullOrWhiteSpace(name) ? directory : name,
                state,
                mod.Value<int?>("Priority"),
                options));
        }
        return result;
    }

    private static WardrobeModRule ToWardrobeRule(DesignModAssociation association) => new()
    {
        Directory = association.Directory,
        Name = association.Name,
        Enabled = association.State == GlamourerModAssociationState.Enabled,
        AssociationState = association.State,
        Priority = association.Priority ?? 0,
        Options = association.Options.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase),
    };

    private static Dictionary<string, string> ResolveEquipmentNames(IReadOnlyDictionary<string, uint> equipment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var itemSheet = DalamudServices.DataManager.GetExcelSheet<Item>();
        var glassesSheet = DalamudServices.DataManager.GetExcelSheet<Glasses>();
        foreach (var pair in equipment)
        {
            var slot = NormalizeSlot(pair.Key);
            string? name;
            try
            {
                name = slot == "Facewear"
                    ? glassesSheet.GetRowOrDefault(pair.Value)?.Name.ToString()
                    : itemSheet.GetRowOrDefault(pair.Value)?.Name.ToString();
            }
            catch (OverflowException)
            {
                // Glamourer can retain encoded/sentinel IDs for empty or custom slots.
                // They are meaningful to Glamourer but are not rows in Lumina sheets.
                continue;
            }
            if (!string.IsNullOrWhiteSpace(name)) result[name] = NormalizeSlot(pair.Key);
        }
        return result;
    }

    private static IReadOnlyList<EquipmentModel> ResolveEquipmentModels(IReadOnlyDictionary<string, uint> equipment)
    {
        var result = new List<EquipmentModel>();
        var itemSheet = DalamudServices.DataManager.GetExcelSheet<Item>();
        var glassesSheet = DalamudServices.DataManager.GetExcelSheet<Glasses>();
        foreach (var pair in equipment)
        {
            var slot = NormalizeSlot(pair.Key);
            try
            {
                if (slot == "Facewear")
                {
                    var glasses = glassesSheet.GetRowOrDefault(pair.Value);
                    if (glasses is null || glasses.Value.Model == 0) continue;
                    result.Add(new EquipmentModel([$"/accessory/a{glasses.Value.Model:D4}/"], slot, glasses.Value.Name.ToString()));
                    continue;
                }
                var item = itemSheet.GetRowOrDefault(pair.Value);
                if (item is null) continue;
                if (slot is "Main Hand" or "Off Hand")
                {
                    var packed = slot == "Main Hand" ? item.Value.ModelMain : item.Value.ModelSub;
                    var weapon = (ushort)(packed & 0xFFFF);
                    var body = (ushort)((packed >> 16) & 0xFFFF);
                    if (weapon != 0 && body != 0)
                        result.Add(new EquipmentModel([$"/weapon/w{weapon:D4}/", $"/body/b{body:D4}/"], slot, item.Value.Name.ToString()));
                    continue;
                }
                var suffix = slot switch
                {
                    "Head" => "met", "Body" => "top", "Hands" => "glv", "Legs" => "dwn", "Feet" => "sho",
                    "Ears" => "ear", "Neck" => "nek", "Wrists" => "wrs",
                    "Right Ring" => "rir", "Left Ring" => "ril", "Rings" => "rir", _ => string.Empty,
                };
                var modelId = (ushort)(item.Value.ModelMain & 0xFFFF);
                if (modelId == 0 || suffix.Length == 0) continue;
                result.Add(new EquipmentModel([$"e{modelId:D4}_{suffix}"], slot, item.Value.Name.ToString()));
            }
            catch (OverflowException)
            {
                continue;
            }
        }
        return result;
    }

    private static IReadOnlyList<ChangedItem> MatchOutfitModels(IReadOnlySet<string> conflictKeys,
        IReadOnlyList<EquipmentModel> targetModels)
    {
        var matches = new List<ChangedItem>();
        foreach (var model in targetModels)
        {
            if (!conflictKeys.Any(key => key.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    && model.Tokens.All(token => key.Contains(token, StringComparison.OrdinalIgnoreCase)))) continue;
            matches.Add(new ChangedItem(model.ItemName, model.Slot));
        }
        return matches;
    }

    private static IReadOnlyList<ChangedItem> BuildSharedConflictItems(IReadOnlyList<string> sharedKeys,
        IReadOnlyList<EquipmentModel> targetModels)
    {
        var result = new List<ChangedItem>();
        foreach (var key in sharedKeys)
        {
            var matchedSlots = targetModels
                .Where(model => model.Tokens.All(token => key.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .Select(model => model.Slot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matchedSlots.Count == 0)
                matchedSlots.AddRange(ClassifyNailSlots(key));
            if (matchedSlots.Count == 0)
            {
                var classified = ClassifyConflictSlot(key);
                if (classified is not null) matchedSlots.Add(classified);
            }
            // A generic shared resource (for example a body texture) is still a real
            // Penumbra conflict, but it is not evidence that the conflict belongs to
            // every equipment slot touched by the seed mod. Keep it visible under
            // Other instead of inheriting unrelated Body/Legs/etc. categories.
            if (matchedSlots.Count == 0) matchedSlots.Add("Other");
            foreach (var slot in matchedSlots)
                result.Add(new ChangedItem(key, NormalizeSlot(slot)));
        }
        return result;
    }

    private static IReadOnlyList<string> ClassifyNailSlots(string key)
    {
        if (!key.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return [];
        var path = key[5..].Replace('\\', '/');
        if (path.Contains("yafinger", StringComparison.OrdinalIgnoreCase)
            || path.Contains("fingernail", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/fingers_", StringComparison.OrdinalIgnoreCase)) return ["Hands"];
        if (path.Contains("yatoe", StringComparison.OrdinalIgnoreCase)
            || path.Contains("toenail", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/toes_", StringComparison.OrdinalIgnoreCase)) return ["Feet"];
        return path.Contains("/nails/", StringComparison.OrdinalIgnoreCase) ? ["Hands", "Feet"] : [];
    }

    private static string? ClassifyConflictSlot(string key)
    {
        if (key.StartsWith("meta:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (serialized, slot) in MetaSlotNames)
                if (key.Contains($"\"EquipSlot\":\"{serialized}\"", StringComparison.OrdinalIgnoreCase)
                    || key.Contains($"\"Slot\":\"{serialized}\"", StringComparison.OrdinalIgnoreCase)) return slot;
            return null;
        }
        if (!key.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        var path = key[5..].Replace('\\', '/');
        return path.Contains("_met", StringComparison.OrdinalIgnoreCase) ? "Head"
            : path.Contains("_top", StringComparison.OrdinalIgnoreCase) ? "Body"
            : path.Contains("_glv", StringComparison.OrdinalIgnoreCase) ? "Hands"
            : path.Contains("_dwn", StringComparison.OrdinalIgnoreCase) ? "Legs"
            : path.Contains("_sho", StringComparison.OrdinalIgnoreCase) ? "Feet"
            : path.Contains("_ear", StringComparison.OrdinalIgnoreCase) ? "Ears"
            : path.Contains("_nek", StringComparison.OrdinalIgnoreCase) ? "Neck"
            : path.Contains("_wrs", StringComparison.OrdinalIgnoreCase) ? "Wrists"
            : path.Contains("_rir", StringComparison.OrdinalIgnoreCase) ? "Right Ring"
            : path.Contains("_ril", StringComparison.OrdinalIgnoreCase) ? "Left Ring"
            : null;
    }

    private static string NormalizeSlot(string slot) => slot.ToLowerInvariant() switch
    {
        "mainhand" or "main hand" => "Main Hand", "offhand" or "off hand" => "Off Hand",
        "head" => "Head", "body" => "Body", "hands" => "Hands", "legs" => "Legs", "feet" => "Feet",
        "ears" => "Ears", "neck" => "Neck", "wrists" or "wrist" => "Wrists",
        "rfinger" or "rightfinger" or "right finger" or "right ring" => "Right Ring",
        "lfinger" or "leftfinger" or "left finger" or "left ring" => "Left Ring",
        "glasses" or "facewear" or "eyewear" => "Facewear",
        "finger" or "ring" or "rings" => "Rings", _ => slot,
    };

    private static int SlotSortOrder(string slot) => NormalizeSlot(slot) switch
    {
        "Head" => 0, "Facewear" => 1, "Body" => 2, "Hands" => 3, "Legs" => 4, "Feet" => 5,
        "Ears" => 6, "Neck" => 7, "Wrists" => 8, "Right Ring" => 9, "Left Ring" => 10,
        "Rings" => 11, "Main Hand" => 12, "Off Hand" => 13, "Equipment" => 14, _ => 15,
    };

    private HashSet<string> GetConflictKeys(string directory, string name,
        IReadOnlyDictionary<string, List<string>> settings, bool includeAllOptions)
    {
        var cacheKey = (includeAllOptions ? "all|" : "selected|") + directory + "|" + string.Join(";",
            settings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + string.Join(',', pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))));
        if (effectiveConflictCache.TryGetValue(cacheKey, out var cached)) return cached;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var modRoot = getModDirectory.Invoke();
            if (string.IsNullOrWhiteSpace(modRoot)) return keys;
            var root = Path.GetFullPath(Path.Combine(modRoot, directory));
            var normalizedModRoot = Path.GetFullPath(modRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(normalizedModRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root)) return keys;

            AddContainerFile(Path.Combine(root, "default_mod.json"));
            foreach (var groupFile in Directory.EnumerateFiles(root, "group_*.json", SearchOption.TopDirectoryOnly))
            {
                var group = JObject.Parse(File.ReadAllText(groupFile));
                var groupName = group.Value<string>("Name") ?? string.Empty;
                settings.TryGetValue(groupName, out var selected);
                selected ??= [];
                var type = group.Value<string>("Type") ?? string.Empty;
                if (type.Equals("Imc", StringComparison.OrdinalIgnoreCase))
                {
                    if (group["Identifier"] is JObject identifier)
                        keys.Add(BuildManipulationKey("Imc", identifier));
                    continue;
                }
                if (includeAllOptions)
                {
                    if (group["Options"] is JArray allOptions)
                        foreach (var option in allOptions) AddContainerToken(option);
                    if (group["Containers"] is JArray allContainers)
                        foreach (var container in allContainers) AddContainerToken(container);
                    continue;
                }
                if (type.Equals("Combining", StringComparison.OrdinalIgnoreCase))
                {
                    var options = group["Options"] as JArray ?? [];
                    var mask = 0;
                    for (var index = 0; index < options.Count; index++)
                        if (selected.Contains(options[index]?.Value<string>("Name") ?? string.Empty,
                                StringComparer.OrdinalIgnoreCase)) mask |= 1 << index;
                    if (group["Containers"] is JArray containers && mask < containers.Count)
                        AddContainerToken(containers[mask]);
                    continue;
                }
                if (group["Options"] is not JArray groupOptions) continue;
                foreach (var option in groupOptions.OfType<JObject>())
                    if (selected.Contains(option.Value<string>("Name") ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)) AddContainerToken(option);
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "WardrobeManager could not inspect Penumbra conflict files for {Mod}.", name);
        }
        effectiveConflictCache[cacheKey] = keys;
        return keys;

        void AddContainerFile(string file)
        {
            if (File.Exists(file)) AddContainerToken(JObject.Parse(File.ReadAllText(file)));
        }

        void AddContainerToken(JToken? container)
        {
            if (container?["Files"] is JObject files)
                foreach (var property in files.Properties()) AddGamePath(property.Name);
            if (container?["FileSwaps"] is JObject swaps)
                foreach (var property in swaps.Properties()) AddGamePath(property.Name);
            if (container?["Manipulations"] is JArray manipulations)
            {
                foreach (var manipulation in manipulations.OfType<JObject>())
                {
                    var identifier = manipulation["Manipulation"]?.DeepClone() as JObject;
                    identifier?.Property("Entry")?.Remove();
                    identifier?.Property("ShiftedEntry")?.Remove();
                    if (identifier is not null)
                        keys.Add(BuildManipulationKey(manipulation.Value<string>("Type") ?? string.Empty, identifier));
                }
            }

            void AddGamePath(string value)
            {
                var path = value.Replace('\\', '/').TrimStart('/');
                if (GamePathRoots.Any(gameRoot => path.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase)))
                    keys.Add("file:" + path);
            }
        }
    }

    private static string BuildManipulationKey(string type, JObject identifier)
        => "meta:" + type.Trim().ToLowerInvariant() + ":"
            + CanonicalizeJson(identifier).ToString(Newtonsoft.Json.Formatting.None);

    private static JToken CanonicalizeJson(JToken token) => token switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new JProperty(property.Name, CanonicalizeJson(property.Value)))),
        JArray array => new JArray(array.Select(CanonicalizeJson)),
        JValue { Type: JTokenType.String } value when long.TryParse(value.Value<string>(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            => new JValue(number),
        _ => token.DeepClone(),
    };

    private IReadOnlyList<ChangedItem> GetChangedItems(string directory, string name)
    {
        if (changedItemCache.TryGetValue(directory, out var cached)) return cached;
        try { cached = getChangedItems.Invoke(directory, name).Select(pair => new ChangedItem(pair.Key, ClassifySlot(pair.Key, pair.Value))).ToList(); }
        catch { cached = []; }
        changedItemCache[directory] = cached;
        return cached;
    }

    private static ChangedItem? MatchOutfitItem(ChangedItem item, IReadOnlyDictionary<string, string> targetItems)
    {
        if (targetItems.TryGetValue(item.Key, out var exactSlot))
            return item with { Slot = exactSlot };

        // Penumbra may decorate a changed-item label with a variant or slot suffix. Match
        // the complete normalized equipment name, never loose individual words or slots.
        var changedName = NormalizeItemName(item.Key);
        foreach (var target in targetItems)
        {
            var targetName = NormalizeItemName(target.Key);
            if (targetName.Length >= 4 && (changedName.Contains(targetName, StringComparison.Ordinal)
                || targetName.Contains(changedName, StringComparison.Ordinal)))
                return item with { Slot = target.Value };
        }
        return null;
    }

    private static string NormalizeItemName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string ClassifySlot(string key, object? value)
    {
        var tokens = new List<string> { key, value?.GetType().Name ?? string.Empty, value?.ToString() ?? string.Empty };
        if (value is not null)
        {
            foreach (var property in value.GetType().GetProperties().Where(property => property.GetIndexParameters().Length == 0))
            {
                try
                {
                    if (property.Name.Contains("slot", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("type", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("category", StringComparison.OrdinalIgnoreCase))
                        tokens.Add(property.GetValue(value)?.ToString() ?? string.Empty);
                }
                catch { }
            }
            foreach (var field in value.GetType().GetFields())
            {
                try
                {
                    if (field.Name.Contains("slot", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("type", StringComparison.OrdinalIgnoreCase)
                        || field.Name.Contains("category", StringComparison.OrdinalIgnoreCase))
                        tokens.Add(field.GetValue(value)?.ToString() ?? string.Empty);
                }
                catch { }
            }
        }
        var text = string.Join(' ', tokens);
        foreach (var (slot, terms) in SlotTerms)
            if (terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))) return slot;
        return text.Contains("equip", StringComparison.OrdinalIgnoreCase) || text.Contains("item", StringComparison.OrdinalIgnoreCase) ? "Equipment" : "Other";
    }

    private static IEnumerable<List<WardrobeModRule>> BuildOverlapGroups(List<WardrobeModRule> rules)
    {
        var remaining = new HashSet<WardrobeModRule>(rules);
        while (remaining.Count > 0)
        {
            var group = new List<WardrobeModRule>();
            var queue = new Queue<WardrobeModRule>();
            queue.Enqueue(remaining.First());
            while (queue.TryDequeue(out var current))
            {
                if (!remaining.Remove(current)) continue;
                group.Add(current);
                foreach (var candidate in remaining.Where(candidate => candidate.AffectedItems.Intersect(current.AffectedItems, StringComparer.OrdinalIgnoreCase).Any()).ToList()) queue.Enqueue(candidate);
            }
            yield return group;
        }
    }

    private static readonly (string Slot, string[] Terms)[] SlotTerms =
    [
        ("Main Hand", ["mainhand", "main hand", "weapon"]), ("Off Hand", ["offhand", "off hand", "shield"]),
        ("Facewear", ["facewear", "glasses", "eyewear"]), ("Head", ["head", "hat", "helmet"]),
        ("Body", ["body", "chest", "top", "dress", "shirt", "coat", "jacket"]), ("Hands", ["hands", "glove"]),
        ("Legs", ["legs", "pants", "trousers", "skirt"]), ("Feet", ["feet", "foot", "boots", "shoes"]),
        ("Ears", ["ears", "earring"]), ("Neck", ["neck", "necklace"]), ("Wrists", ["wrist", "bracelet"]),
        ("Right Ring", ["rfinger", "right finger", "right ring"]), ("Left Ring", ["lfinger", "left finger", "left ring"]),
        ("Rings", ["finger", "ring"]),
    ];

    private static readonly string[] GamePathRoots =
    [
        "bg/", "chara/", "common/", "cut/", "exd/", "game_script/", "music/", "shader/", "sound/", "ui/", "vfx/",
    ];

    private static readonly (string Serialized, string Slot)[] MetaSlotNames =
    [
        ("MainHand", "Main Hand"), ("OffHand", "Off Hand"), ("Head", "Head"), ("Body", "Body"),
        ("Hands", "Hands"), ("Legs", "Legs"), ("Feet", "Feet"), ("Ears", "Ears"), ("Neck", "Neck"),
        ("Wrists", "Wrists"), ("RFinger", "Right Ring"), ("LFinger", "Left Ring"), ("Glasses", "Facewear"),
    ];

    private static bool IsSuccess(PenumbraApiEc result) => result is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged;

    public void Dispose() => ClearTemporaryOutfitOverrides(false);
}

internal sealed record AvailableMod(string Directory, string Name);
internal sealed record PenumbraCollection(Guid Id, string Name);
internal sealed record CustomizePlusProfile(Guid Id, string Name, string Path,
    IReadOnlyList<(string Name, ushort WorldId, byte CharacterType, ushort CharacterSubType)> Characters,
    bool Enabled);
internal sealed record HonorificTitle(string Name, bool IsPrefix, string Id, string Json);
internal sealed record ChangedItem(string Key, string Slot);
internal sealed record EquipmentModel(IReadOnlyList<string> Tokens, string Slot, string ItemName);
internal sealed record ScannedRule(string Directory, string Name, bool Enabled, int Priority, Dictionary<string, List<string>> Options, IReadOnlyList<ChangedItem> Items, HashSet<string> ConflictKeys);
internal sealed record DesignModAssociation(string Directory, string Name, GlamourerModAssociationState State, int? Priority,
    Dictionary<string, List<string>> Options);
internal sealed record LayerScanResult(bool Success, string Message)
{
    public static LayerScanResult Ok(string message) => new(true, message);
    public static LayerScanResult Fail(string message) => new(false, message);
}
internal sealed record GlamourerDesign(Guid Id, string Name, string State, bool AppliesEquipment, string FolderPath,
    Dictionary<string, uint> EquipmentItemIds, IReadOnlyList<WardrobeModRule> ModAssociations, bool QuickDesign,
    string CharacterJson, string OutfitJson);
internal sealed record GlamourerDesignScan(bool Success, IReadOnlyList<GlamourerDesign> Designs, string Error);
internal sealed record GlamourerQuickDesign(Guid Id, string Name, string Path, uint Color);
internal sealed record ModOptionGroup(string Name, IReadOnlyList<string> Choices, bool AllowsMultiple);

internal sealed record IntegrationRequirementState(bool PenumbraConnected, bool GlamourerConnected)
{
    public bool Connected => PenumbraConnected && GlamourerConnected;
    public string Message => Connected ? "connected" : "connection required";
}
