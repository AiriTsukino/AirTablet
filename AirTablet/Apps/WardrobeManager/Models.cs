namespace WardrobeManager;

internal enum WardrobePresetType { Outfit, Character, Emote }
internal enum WardrobeLayerRole { Base, Highest, Conflict }
internal enum GlamourerModAssociationState { Ignore, Enabled, Disabled, Inherit, Remove }
internal enum WardrobeHonorificCondition { None, ClassJob, JobRole, GearSet, OriginalTitle, Location }
internal enum WardrobeHonorificAnimation { Pulse, Wave, Static }

internal sealed class WardrobeData
{
    public int Version { get; set; } = 29;
    public List<WardrobePreset> Presets { get; set; } = [];
    public List<WardrobeFolder> Folders { get; set; } = [];
}

internal sealed class WardrobeFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Folder";
    public string GlamourerPath { get; set; } = string.Empty;
}

internal sealed class WardrobePreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public WardrobePresetType Type { get; set; }
    public string Name { get; set; } = "New Preset";
    public bool IsFavorite { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public string GlamourerState { get; set; } = string.Empty;
    public string OutfitAppearanceJson { get; set; } = string.Empty;
    public string CharacterAppearanceJson { get; set; } = string.Empty;
    public Guid GlamourerDesignId { get; set; }
    public string GlamourerFolderPath { get; set; } = string.Empty;
    public Guid FolderId { get; set; }
    public Guid PenumbraCollectionId { get; set; }
    public string PenumbraCollectionName { get; set; } = string.Empty;
    public Guid CustomizePlusProfileId { get; set; }
    public string CustomizePlusProfileName { get; set; } = string.Empty;
    public string CustomizePlusProfilePath { get; set; } = string.Empty;
    public string HonorificTitleName { get; set; } = string.Empty;
    public string HonorificTitleJson { get; set; } = string.Empty;
    public string HonorificTitleId { get; set; } = string.Empty;
    public bool HonorificTitleConfigured { get; set; }
    public bool HonorificUsesExistingTitle { get; set; }
    public bool HonorificCustomIsPrefix { get; set; } = true;
    public bool HonorificUseColor { get; set; }
    public float HonorificColorR { get; set; } = 1f;
    public float HonorificColorG { get; set; } = 1f;
    public float HonorificColorB { get; set; } = 1f;
    public bool HonorificUseGlow { get; set; }
    public float HonorificGlowR { get; set; }
    public float HonorificGlowG { get; set; }
    public float HonorificGlowB { get; set; }
    public int HonorificEffectPalette { get; set; } = -2;
    public WardrobeHonorificAnimation HonorificEffectAnimation { get; set; } = WardrobeHonorificAnimation.Static;
    public float HonorificEffectColor2R { get; set; } = 1f;
    public float HonorificEffectColor2G { get; set; } = 1f;
    public float HonorificEffectColor2B { get; set; } = 1f;
    public WardrobeHonorificCondition HonorificCondition { get; set; }
    public int HonorificConditionParam { get; set; }
    public uint HonorificTerritoryId { get; set; }
    public bool AutomaticLayersScanned { get; set; }
    public Dictionary<string, uint> EquipmentItemIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WardrobeModRule> RegisteredOutfitMods { get; set; } = [];
    public List<WardrobeModRule> Mods { get; set; } = [];
}

internal sealed class WardrobeModRule
{
    public string Directory { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public GlamourerModAssociationState AssociationState { get; set; } = GlamourerModAssociationState.Enabled;
    public WardrobeLayerRole Role { get; set; }
    public bool CapturedFromResources { get; set; }
    public int CapturedPriority { get; set; }
    public List<string> Slots { get; set; } = [];
    public List<string> AffectedItems { get; set; } = [];
    public Dictionary<string, List<string>> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
