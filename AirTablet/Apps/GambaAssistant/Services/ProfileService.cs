using GambaAssistant.Games.Blackjack;

namespace GambaAssistant.Services;

[Serializable]
public sealed class VenueProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    public BlackjackRules BlackjackRules { get; set; } = new();

    // Active templates used by the game/chat systems. Kept as a direct dictionary for backward compatibility.
    public Dictionary<string, string> ChatTemplates { get; set; } = ChatTemplateDefaults.CreateFormal();

    // Editable template library owned by this venue profile.
    public List<ChatTemplateSet> ChatTemplateLibrary { get; set; } = [];
    public Guid ActiveChatTemplateId { get; set; }

    public OverlaySettings Overlay { get; set; } = new();
    public float MessagePacingSeconds { get; set; } = 1.0f;
    public string ExportPrefix { get; set; } = "gamba-session";
    public string DefaultTipType { get; set; } = "Dealer tip";
    public string NaturalBlackjackChatChannel { get; set; } = "/party";
}

[Serializable]
public sealed class ChatTemplateSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Template";
    public Dictionary<string, string> Templates { get; set; } = ChatTemplateDefaults.CreateFormal();
}

public sealed class ProfileService
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly LogService log;
    public List<VenueProfile> Profiles { get; } = [];
    public VenueProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == config.ActiveProfileId) ?? Profiles[0];

    public ProfileService(Configuration config, PersistenceService persistence, LogService log)
    {
        this.config = config;
        this.persistence = persistence;
        this.log = log;
        LoadOrCreateDefaults();
    }

    public bool TrySwitchProfile(Guid id, BlackjackSession session, out string reason)
    {
        if (session.IsActive) { reason = "Profile switching is locked while a night/session is active."; return false; }
        if (Profiles.All(p => p.Id != id)) { reason = "Profile was not found."; return false; }
        config.ActiveProfileId = id;
        persistence.SaveNow();
        reason = string.Empty;
        return true;
    }

    public bool TryCreateProfile(string name, BlackjackSession session, out VenueProfile? profile, out string reason)
    {
        profile = null;
        name = string.IsNullOrWhiteSpace(name) ? "New Venue" : name.Trim();
        if (session.IsActive) { reason = "Profile creation is locked while a night/session is active."; return false; }
        if (Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "A venue profile with that name already exists.";
            return false;
        }

        profile = new VenueProfile { Name = name, ChatTemplates = ChatTemplateDefaults.CreateFormal() };
        EnsureTemplateLibrary(profile);
        NormalizeDiceCommand(profile);
        NormalizeBlackjackRuleDefaults(profile);
        NormalizeProfileChatChannels(profile);
        Profiles.Add(profile);
        config.ActiveProfileId = profile.Id;
        SaveProfile(profile);
        persistence.SaveNow();
        log.Add(LogCategory.Info, $"Created venue profile: {profile.Name}.");
        reason = string.Empty;
        return true;
    }

    public bool TryDeleteProfile(Guid id, BlackjackSession session, out string reason)
    {
        if (session.IsActive) { reason = "Profile deletion is locked while a night/session is active."; return false; }
        if (Profiles.Count <= 1) { reason = "At least one venue profile must remain."; return false; }
        var profile = Profiles.FirstOrDefault(p => p.Id == id);
        if (profile == null) { reason = "Profile was not found."; return false; }

        Profiles.Remove(profile);
        TryDeleteProfileFile(profile.Id);
        if (config.ActiveProfileId == id)
            config.ActiveProfileId = Profiles[0].Id;
        persistence.SaveNow();
        log.Add(LogCategory.Info, $"Removed venue profile: {profile.Name}.");
        reason = string.Empty;
        return true;
    }

    public void RenameProfile(VenueProfile profile, string name)
    {
        profile.Name = string.IsNullOrWhiteSpace(name) ? "Unnamed Venue" : name.Trim();
        SaveProfile(profile);
        persistence.SaveNow();
    }

    public void ApplyTemplatePreset(VenueProfile profile, string presetName)
    {
        EnsureTemplateLibrary(profile);
        var set = profile.ChatTemplateLibrary.FirstOrDefault(t => string.Equals(t.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (set == null)
        {
            set = new ChatTemplateSet
            {
                Name = presetName,
                Templates = string.Equals(presetName, "Minimal Spam", StringComparison.OrdinalIgnoreCase)
                    ? ChatTemplateDefaults.CreateMinimal()
                    : ChatTemplateDefaults.CreateFormal(),
            };
            profile.ChatTemplateLibrary.Add(set);
        }

        profile.ActiveChatTemplateId = set.Id;
        profile.ChatTemplates = CopyTemplates(set.Templates);
        SaveProfile(profile);
        log.Add(LogCategory.Info, $"Assigned {set.Name} chat templates to {profile.Name}.");
    }

    public void UpdateTemplate(VenueProfile profile, string key, string value)
    {
        EnsureTemplateLibrary(profile);
        var active = GetActiveTemplateSet(profile);
        active.Templates[key] = value;
        profile.ChatTemplates[key] = value;
        SaveProfile(profile);
    }

    public IReadOnlyList<ChatTemplateSet> GetTemplateSets(VenueProfile profile)
    {
        EnsureTemplateLibrary(profile);
        return profile.ChatTemplateLibrary;
    }

    public ChatTemplateSet GetActiveTemplateSet(VenueProfile profile)
    {
        EnsureTemplateLibrary(profile);
        var active = profile.ChatTemplateLibrary.FirstOrDefault(t => t.Id == profile.ActiveChatTemplateId);
        if (active != null)
            return active;

        active = profile.ChatTemplateLibrary[0];
        profile.ActiveChatTemplateId = active.Id;
        profile.ChatTemplates = CopyTemplates(active.Templates);
        SaveProfile(profile);
        return active;
    }

    public bool TryAssignTemplate(VenueProfile profile, Guid templateId, BlackjackSession session, out string reason)
    {
        if (session.IsActive) { reason = "Chat template assignment is locked while a session is active."; return false; }
        EnsureTemplateLibrary(profile);
        var set = profile.ChatTemplateLibrary.FirstOrDefault(t => t.Id == templateId);
        if (set == null) { reason = "Template was not found."; return false; }

        EnsureRequiredTemplates(set.Templates);
        profile.ActiveChatTemplateId = set.Id;
        profile.ChatTemplates = CopyTemplates(set.Templates);
        SaveProfile(profile);
        log.Add(LogCategory.Info, $"Assigned chat template '{set.Name}' to {profile.Name}.");
        reason = string.Empty;
        return true;
    }

    public ChatTemplateSet CreateTemplate(VenueProfile profile, string name)
    {
        EnsureTemplateLibrary(profile);
        name = UniqueTemplateName(profile, string.IsNullOrWhiteSpace(name) ? "New Template" : name.Trim());
        var set = new ChatTemplateSet { Name = name, Templates = ChatTemplateDefaults.CreateFormal() };
        profile.ChatTemplateLibrary.Add(set);
        SaveProfile(profile);
        log.Add(LogCategory.Info, $"Created chat template '{set.Name}' for {profile.Name}.");
        return set;
    }

    public ChatTemplateSet CloneTemplate(VenueProfile profile, Guid sourceId, string? name = null)
    {
        EnsureTemplateLibrary(profile);
        var source = profile.ChatTemplateLibrary.FirstOrDefault(t => t.Id == sourceId) ?? GetActiveTemplateSet(profile);
        var clone = new ChatTemplateSet
        {
            Name = UniqueTemplateName(profile, string.IsNullOrWhiteSpace(name) ? $"{source.Name} Copy" : name.Trim()),
            Templates = CopyTemplates(source.Templates),
        };
        profile.ChatTemplateLibrary.Add(clone);
        SaveProfile(profile);
        log.Add(LogCategory.Info, $"Cloned chat template '{source.Name}' to '{clone.Name}'.");
        return clone;
    }

    public bool TryDeleteTemplate(VenueProfile profile, Guid templateId, BlackjackSession session, out string reason)
    {
        if (session.IsActive) { reason = "Chat template deletion is locked while a session is active."; return false; }
        EnsureTemplateLibrary(profile);
        if (profile.ChatTemplateLibrary.Count <= 1) { reason = "At least one chat template must remain."; return false; }
        var set = profile.ChatTemplateLibrary.FirstOrDefault(t => t.Id == templateId);
        if (set == null) { reason = "Template was not found."; return false; }

        profile.ChatTemplateLibrary.Remove(set);
        if (profile.ActiveChatTemplateId == templateId)
        {
            var next = profile.ChatTemplateLibrary[0];
            profile.ActiveChatTemplateId = next.Id;
            profile.ChatTemplates = CopyTemplates(next.Templates);
        }

        SaveProfile(profile);
        log.Add(LogCategory.Info, $"Removed chat template '{set.Name}' from {profile.Name}.");
        reason = string.Empty;
        return true;
    }

    public void RenameTemplate(VenueProfile profile, Guid templateId, string name)
    {
        EnsureTemplateLibrary(profile);
        var set = profile.ChatTemplateLibrary.FirstOrDefault(t => t.Id == templateId);
        if (set == null)
            return;

        set.Name = UniqueTemplateName(profile, string.IsNullOrWhiteSpace(name) ? "Unnamed Template" : name.Trim(), templateId);
        SaveProfile(profile);
    }

    public void UpdateTemplateValue(VenueProfile profile, Guid templateId, string key, string value)
    {
        EnsureTemplateLibrary(profile);
        var set = profile.ChatTemplateLibrary.FirstOrDefault(t => t.Id == templateId);
        if (set == null)
            return;

        set.Templates[key] = value;
        if (profile.ActiveChatTemplateId == templateId)
            profile.ChatTemplates[key] = value;
        SaveProfile(profile);
    }

    public void UpdateNaturalBlackjackChatChannel(VenueProfile profile, string channel)
    {
        profile.NaturalBlackjackChatChannel = NormalizeChatChannel(channel);
        SaveProfile(profile);
        log.Add(LogCategory.Info, $"Natural Blackjack broadcast channel for {profile.Name} set to {profile.NaturalBlackjackChatChannel}.");
    }

    public void SaveProfile(VenueProfile profile)
    {
        EnsureTemplateLibrary(profile);
        persistence.SaveJson($"Profiles/{profile.Id}.json", profile);
    }

    private void TryDeleteProfileFile(Guid id)
    {
        var path = Path.Combine(persistence.ConfigRoot, "Profiles", $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    private void LoadOrCreateDefaults()
    {
        var dir = Path.Combine(persistence.ConfigRoot, "Profiles");
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var profile = persistence.LoadJson<VenueProfile>(Path.Combine("Profiles", Path.GetFileName(file)));
            if (profile != null) Profiles.Add(profile);
        }

        ReplaceOldGeneratedDefaultsIfNeeded();

        if (Profiles.Count == 0)
        {
            var profile = new VenueProfile { Name = "Default", ChatTemplates = ChatTemplateDefaults.CreateFormal() };
            EnsureTemplateLibrary(profile);
            NormalizeDiceCommand(profile);
            NormalizeBlackjackRuleDefaults(profile);
            NormalizeProfileChatChannels(profile);
            Profiles.Add(profile);
            SaveProfile(profile);
        }
        else
        {
            foreach (var profile in Profiles)
            {
                EnsureTemplateLibrary(profile);
                NormalizeDiceCommand(profile);
                NormalizeBlackjackRuleDefaults(profile);
                NormalizeProfileChatChannels(profile);
                SaveProfile(profile);
            }
        }

        if (config.ActiveProfileId == Guid.Empty || Profiles.All(p => p.Id != config.ActiveProfileId)) config.ActiveProfileId = Profiles[0].Id;
        log.Add(LogCategory.Info, $"Loaded {Profiles.Count} venue profile(s).");
    }

    private void ReplaceOldGeneratedDefaultsIfNeeded()
    {
        if (Profiles.Count != 3)
            return;

        var names = Profiles.Select(p => p.Name).OrderBy(n => n).ToArray();
        var oldDefaults = new[] { "Default Casual", "Formal Casino", "Minimal Spam" }.OrderBy(n => n).ToArray();
        if (!names.SequenceEqual(oldDefaults, StringComparer.OrdinalIgnoreCase))
            return;

        foreach (var profile in Profiles.ToList())
            TryDeleteProfileFile(profile.Id);

        Profiles.Clear();
        var replacement = new VenueProfile { Name = "Default", ChatTemplates = ChatTemplateDefaults.CreateFormal() };
        EnsureTemplateLibrary(replacement);
        NormalizeDiceCommand(replacement);
        NormalizeBlackjackRuleDefaults(replacement);
        NormalizeProfileChatChannels(replacement);
        Profiles.Add(replacement);
        config.ActiveProfileId = replacement.Id;
        log.Add(LogCategory.Info, "Replaced old generated sample venues with a single Default venue profile.");
    }

    private static void NormalizeDiceCommand(VenueProfile profile)
    {
        profile.BlackjackRules ??= new BlackjackRules();

        // Older GambaAssistant profiles used /dice 13, which depends on the
        // currently selected chat mode and can fail to visibly roll in party
        // when sent through the game shell. FFXIV supports explicit party dice
        // with /dice party 13, so migrate the default to that safer form.
        if (string.IsNullOrWhiteSpace(profile.BlackjackRules.DiceCommand)
            || string.Equals(profile.BlackjackRules.DiceCommand.Trim(), "/dice 13", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.BlackjackRules.DiceCommand.Trim(), "/dice p 13", StringComparison.OrdinalIgnoreCase))
        {
            profile.BlackjackRules.DiceCommand = $"/dice party {profile.BlackjackRules.DiceSides}";
        }
    }

    private static void NormalizeBlackjackRuleDefaults(VenueProfile profile)
    {
        profile.BlackjackRules ??= new BlackjackRules();
        var rules = profile.BlackjackRules;

        // Migrate older generated defaults to the currently documented venue
        // defaults. User-customized values remain editable in the profile UI.
        if (rules.MaxSplitHands <= 0 || rules.MaxSplitHands == 4)
            rules.MaxSplitHands = 2;
        if (rules.DoubleAfterSplit)
            rules.DoubleAfterSplit = false;
        if (rules.ResplitPairs)
            rules.ResplitPairs = false;
        if (rules.CustomBlackjackMultiplier <= 0 || rules.CustomBlackjackMultiplier == 1.5m)
            rules.CustomBlackjackMultiplier = 2.5m;
        if (rules.StandardWinTotalMultiplier <= 0)
            rules.StandardWinTotalMultiplier = 2.0m;
        if (rules.DoubleDownWinTotalMultiplier <= 0)
            rules.DoubleDownWinTotalMultiplier = 3.0m;
        if (rules.NaturalBlackjackTotalMultiplier <= 0)
            rules.NaturalBlackjackTotalMultiplier = 3.5m;
    }

    private static void NormalizeProfileChatChannels(VenueProfile profile)
    {
        profile.NaturalBlackjackChatChannel = NormalizeChatChannel(profile.NaturalBlackjackChatChannel);
    }

    private static string NormalizeChatChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "/say" or "say" or "/s" or "s" => "/say",
        "/shout" or "shout" or "/sh" or "sh" => "/shout",
        "/yell" or "yell" or "/y" or "y" => "/yell",
        "/party" or "party" or "/p" or "p" => "/party",
        _ => "/party",
    };

    private void EnsureTemplateLibrary(VenueProfile profile)
    {
        profile.ChatTemplates ??= ChatTemplateDefaults.CreateFormal();
        EnsureRequiredTemplates(profile.ChatTemplates);
        profile.ChatTemplateLibrary ??= [];

        if (profile.ChatTemplateLibrary.Count == 0)
        {
            var formal = new ChatTemplateSet { Name = "Formal Casino", Templates = ChatTemplateDefaults.CreateFormal() };
            var minimal = new ChatTemplateSet { Name = "Minimal Spam", Templates = ChatTemplateDefaults.CreateMinimal() };
            profile.ChatTemplateLibrary.Add(formal);
            profile.ChatTemplateLibrary.Add(minimal);
            profile.ActiveChatTemplateId = formal.Id;
            profile.ChatTemplates = CopyTemplates(formal.Templates);
        }

        foreach (var set in profile.ChatTemplateLibrary)
        {
            if (string.IsNullOrWhiteSpace(set.Name))
                set.Name = "Unnamed Template";
            set.Templates ??= ChatTemplateDefaults.CreateFormal();
            EnsureRequiredTemplates(set.Templates);
        }

        if (profile.ActiveChatTemplateId == Guid.Empty || profile.ChatTemplateLibrary.All(t => t.Id != profile.ActiveChatTemplateId))
        {
            var active = MatchExistingTemplate(profile) ?? profile.ChatTemplateLibrary[0];
            profile.ActiveChatTemplateId = active.Id;
            profile.ChatTemplates = CopyTemplates(active.Templates);
        }
    }

    private static ChatTemplateSet? MatchExistingTemplate(VenueProfile profile)
    {
        foreach (var set in profile.ChatTemplateLibrary)
        {
            if (ChatTemplateDefaults.RequiredKeys.All(k => profile.ChatTemplates.TryGetValue(k, out var value) && set.Templates.TryGetValue(k, out var templateValue) && value == templateValue))
                return set;
        }

        return null;
    }

    private static void EnsureRequiredTemplates(Dictionary<string, string> templates)
    {
        var defaults = ChatTemplateDefaults.CreateFormal();
        foreach (var key in ChatTemplateDefaults.RequiredKeys)
        {
            if (!templates.ContainsKey(key))
                templates[key] = defaults[key];
        }
    }

    private static Dictionary<string, string> CopyTemplates(Dictionary<string, string> templates) =>
        templates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    private static string UniqueTemplateName(VenueProfile profile, string desired, Guid? selfId = null)
    {
        desired = string.IsNullOrWhiteSpace(desired) ? "New Template" : desired.Trim();
        var candidate = desired;
        var suffix = 2;
        while (profile.ChatTemplateLibrary.Any(t => t.Id != selfId && string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{desired} {suffix}";
            suffix++;
        }

        return candidate;
    }
}

public static class ChatTemplateDefaults
{
    public static readonly string[] RequiredKeys =
    [
        "CardReceived",
        "NaturalBlackjack",
        "Bust",
        "Settlement",
        "DealerDraw",
        "AllPlayersBust",
        "PlayerTurnOptions",
        "BetPlaced"
    ];

    public static readonly string[] PresetNames = ["Formal Casino", "Minimal Spam"];

    public static Dictionary<string, string> Create() => CreateFormal();

    public static Dictionary<string, string> CreateFormal() => new()
    {
        ["CardReceived"] = "{player} receives {card}. Current hand: {hand}, total {total}.",
        ["NaturalBlackjack"] = "Congratulations {player}: Natural Blackjack with {hand}.",
        ["Bust"] = "{player} busts at {total}. Bet resolved: {bet} gil.",
        ["Settlement"] = "{player}: {bet} gil wager, {hand} versus dealer {dealer}. Result: {outcome}. Bank: {bank} gil.",
        ["DealerDraw"] = "Dealer draws {card}. Dealer total is {total}.",
        ["AllPlayersBust"] = "All active players have bust. The round is over.",
        ["PlayerTurnOptions"] = "{player}'s turn. {handLabel}: {hand} = {total}. Legal options: {options}.",
        ["BetPlaced"] = "{player} has bet {amount} gil for this round."
    };

    public static Dictionary<string, string> CreateMinimal() => new()
    {
        ["CardReceived"] = "{player}: {card} ({total})",
        ["NaturalBlackjack"] = "{player}: Natural Blackjack!",
        ["Bust"] = "{player}: Bust {total}.",
        ["Settlement"] = "{player}: {outcome}. Bank {bank}.",
        ["DealerDraw"] = "Dealer: {card} ({total})",
        ["AllPlayersBust"] = "All players bust. Round over.",
        ["PlayerTurnOptions"] = "{player}: {handLabel} {hand} ({total}). Options: {options}.",
        ["BetPlaced"] = "{player}: bet {amount} gil."
    };
}
