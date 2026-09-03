using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace WardrobeManager;

internal sealed class PersistenceService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string dataPath;
    private readonly string imagesDirectory;
    public WardrobeData Data { get; }

    public PersistenceService()
    {
        var root = DalamudServices.PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(root);
        imagesDirectory = Path.Combine(root, "Images");
        Directory.CreateDirectory(imagesDirectory);
        dataPath = Path.Combine(root, "wardrobe.json");
        try { Data = File.Exists(dataPath) ? JsonSerializer.Deserialize<WardrobeData>(File.ReadAllText(dataPath), Options) ?? new WardrobeData() : new WardrobeData(); }
        catch (Exception ex) { DalamudServices.Log.Warning(ex, "WardrobeManager could not load wardrobe.json."); Data = new WardrobeData(); }
        var loadedVersion = Data.Version;
        Normalize();
        var recoveredImages = RecoverManagedImagePaths();
        if (loadedVersion != Data.Version || recoveredImages > 0) Save();
    }

    public void Save()
    {
        Normalize();
        var temp = dataPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Data, Options));
        File.Move(temp, dataPath, true);
    }

    public string ImportImage(string source, Guid presetId)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")) throw new InvalidOperationException("Choose a PNG, JPG, WEBP, or BMP image.");
        var destination = Path.Combine(imagesDirectory, presetId.ToString("N") + extension);
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, true);
        foreach (var sibling in Directory.EnumerateFiles(imagesDirectory, presetId.ToString("N") + ".*")
                     .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)))
            File.Delete(sibling);
        return destination;
    }

    public ImageCleanupResult DeleteUnusedImages(string selfieFolder)
    {
        RecoverManagedImagePaths();
        var referenced = Data.Presets
            .Where(preset => !string.IsNullOrWhiteSpace(preset.ImagePath))
            .Select(preset => Path.GetFullPath(preset.ImagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managedDeleted = 0;
        foreach (var file in Directory.EnumerateFiles(imagesDirectory).Where(IsSupportedImage))
        {
            if (referenced.Contains(Path.GetFullPath(file))) continue;
            File.Delete(file);
            managedDeleted++;
        }

        var selfiesDeleted = 0;
        if (Directory.Exists(selfieFolder))
        {
            var selfieFiles = Directory.EnumerateFiles(selfieFolder, "*", SearchOption.AllDirectories)
                .Where(IsSupportedImage)
                .ToList();
            foreach (var preset in Data.Presets)
            {
                var id = preset.Id.ToString("N");
                var captures = selfieFiles
                    .Where(path => Path.GetFileNameWithoutExtension(path).Contains(id, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
                foreach (var oldCapture in captures.Skip(1))
                {
                    if (referenced.Contains(Path.GetFullPath(oldCapture))) continue;
                    File.Delete(oldCapture);
                    selfiesDeleted++;
                }
            }
        }

        return new ImageCleanupResult(managedDeleted, selfiesDeleted);
    }

    public IReadOnlyList<ImageRelink> RescanImages(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The selected image folder does not exist.");
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsSupportedImage)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        var relinked = new List<ImageRelink>();
        foreach (var preset in Data.Presets.Where(x => string.IsNullOrWhiteSpace(x.ImagePath) || !File.Exists(x.ImagePath)))
        {
            var compactId = preset.Id.ToString("N");
            var dashedId = preset.Id.ToString("D");
            var match = files.FirstOrDefault(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                return stem.Contains(compactId, StringComparison.OrdinalIgnoreCase)
                    || stem.Contains(dashedId, StringComparison.OrdinalIgnoreCase);
            });

            if (match is null)
            {
                var nameKey = ImageKey(preset.Name);
                if (nameKey.Length >= 3)
                {
                    var matchingPresets = Data.Presets.Count(x => ImageKey(x.Name).Equals(nameKey, StringComparison.Ordinal));
                    if (matchingPresets == 1)
                        match = files.FirstOrDefault(path => ImageKey(Path.GetFileNameWithoutExtension(path)).Contains(nameKey, StringComparison.Ordinal));
                }
            }

            if (match is null) continue;
            var oldPath = preset.ImagePath;
            preset.ImagePath = ImportImage(match, preset.Id);
            relinked.Add(new ImageRelink(preset, oldPath, match));
        }

        if (relinked.Count > 0) Save();
        return relinked;
    }

    public static string ImageKey(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        return result.ToString();
    }

    private static bool IsSupportedImage(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

    public string ImportCroppedImage(string source, Guid presetId, NormalizedCrop crop)
    {
        var destination = Path.Combine(imagesDirectory, presetId.ToString("N") + ".png");
        using var image = new Bitmap(source);
        var requested = crop.ToPixels(image.Width, image.Height);
        var portrait = FitPortrait(requested, image.Width, image.Height);
        using var cropped = image.Clone(portrait, PixelFormat.Format32bppArgb);
        var temp = destination + ".tmp.png";
        cropped.Save(temp, ImageFormat.Png);
        File.Move(temp, destination, true);
        return destination;
    }

    private static Rectangle FitPortrait(Rectangle area, int imageWidth, int imageHeight)
    {
        var x = Math.Clamp(area.X, 0, Math.Max(0, imageWidth - 1));
        var y = Math.Clamp(area.Y, 0, Math.Max(0, imageHeight - 1));
        var width = Math.Clamp(area.Width, 1, imageWidth - x);
        var height = Math.Clamp(area.Height, 1, imageHeight - y);
        var target = 9f / 16f;
        if (width / (float)height > target)
        {
            var fittedWidth = Math.Max(1, (int)MathF.Round(height * target));
            x += (width - fittedWidth) / 2;
            width = fittedWidth;
        }
        else
        {
            var fittedHeight = Math.Max(1, (int)MathF.Round(width / target));
            y += (height - fittedHeight) / 2;
            height = fittedHeight;
        }

        return new Rectangle(x, y, width, height);
    }

    private void Normalize()
    {
        var loadedVersion = Data.Version;
        Data.Presets ??= [];
        Data.Folders ??= [];
        foreach (var folder in Data.Folders)
        {
            if (folder.Id == Guid.Empty) folder.Id = Guid.NewGuid();
            folder.Name = string.IsNullOrWhiteSpace(folder.Name) ? "Unnamed Folder" : folder.Name.Trim();
            folder.GlamourerPath ??= string.Empty;
        }
        var folderIds = Data.Folders.Select(folder => folder.Id).ToHashSet();
        foreach (var preset in Data.Presets)
        {
            if (preset.Id == Guid.Empty) preset.Id = Guid.NewGuid();
            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "Unnamed Preset" : preset.Name.Trim();
            preset.ImagePath ??= string.Empty;
            preset.OutfitAppearanceOverrides ??= new(StringComparer.Ordinal);
            preset.AppearanceValueOverrides ??= new(StringComparer.Ordinal);
            preset.GlamourerState ??= string.Empty;
            preset.OutfitAppearanceJson ??= string.Empty;
            preset.CharacterAppearanceJson ??= string.Empty;
            preset.GlamourerFolderPath ??= string.Empty;
            preset.PenumbraCollectionName ??= string.Empty;
            preset.CustomizePlusProfileName ??= string.Empty;
            preset.CustomizePlusProfilePath ??= string.Empty;
            preset.HonorificTitleName ??= string.Empty;
            preset.HonorificTitleJson ??= string.Empty;
            preset.HonorificTitleId ??= string.Empty;
            preset.HonorificColorR = Math.Clamp(preset.HonorificColorR, 0f, 1f);
            preset.HonorificColorG = Math.Clamp(preset.HonorificColorG, 0f, 1f);
            preset.HonorificColorB = Math.Clamp(preset.HonorificColorB, 0f, 1f);
            preset.HonorificGlowR = Math.Clamp(preset.HonorificGlowR, 0f, 1f);
            preset.HonorificGlowG = Math.Clamp(preset.HonorificGlowG, 0f, 1f);
            preset.HonorificGlowB = Math.Clamp(preset.HonorificGlowB, 0f, 1f);
            preset.HonorificEffectColor2R = Math.Clamp(preset.HonorificEffectColor2R, 0f, 1f);
            preset.HonorificEffectColor2G = Math.Clamp(preset.HonorificEffectColor2G, 0f, 1f);
            preset.HonorificEffectColor2B = Math.Clamp(preset.HonorificEffectColor2B, 0f, 1f);
            if (loadedVersion < 29) preset.HonorificEffectPalette = -2;
            preset.HonorificEffectPalette = Math.Clamp(preset.HonorificEffectPalette, -2, 15);
            if (loadedVersion < 28 && preset.HonorificTitleConfigured && !string.IsNullOrWhiteSpace(preset.HonorificTitleJson))
            {
                // Existing selections were previously stored as raw Honorific title JSON.
                // Keep them as existing-title references; newly typed titles are assigned
                // a stable WardrobeManager UID the next time they are saved.
                preset.HonorificUsesExistingTitle = true;
                try
                {
                    var legacy = Newtonsoft.Json.Linq.JObject.Parse(preset.HonorificTitleJson);
                    preset.HonorificTitleId = legacy.Value<string>("UniqueId") ?? string.Empty;
                }
                catch { }
            }
            preset.EquipmentItemIds = preset.EquipmentItemIds is null
                ? new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, uint>(preset.EquipmentItemIds, StringComparer.OrdinalIgnoreCase);
            if (loadedVersion < 11 && preset.Type == WardrobePresetType.Outfit && preset.AutomaticLayersScanned)
            {
                preset.Mods = [];
                preset.AutomaticLayersScanned = false;
            }
            // Version 12 replaces the failed per-mod settings scan with Penumbra's
            // collection-wide settings API. Re-run automatic scans once so existing
            // empty outfits are repaired without requiring the user to press Rescan.
            if (loadedVersion < 12 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 13 corrects physical Penumbra mod-folder resolution.
            if (loadedVersion < 13 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 14 restricts layers to current enabled/selected-option conflicts.
            if (loadedVersion < 14 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 15 refreshed automatic layers after the conflict scanner changed.
            if (loadedVersion < 15 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 16 restores legitimate shared equipment conflicts and assigns
            // layers to the expanded equipment-slot categories.
            if (loadedVersion < 16 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 17 refreshes layers from Glamourer's saved mod associations and
            // exact overlapping game paths instead of inferred changed-item IDs.
            if (loadedVersion < 17 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 18 tolerates Glamourer's signed sentinel equipment IDs and
            // refreshes any layers left stale by an aborted version 17 scan.
            if (loadedVersion < 18 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 19 adds IMC conflicts, canonical metadata identities, and
            // prevents generic resources from inheriting incorrect equipment slots.
            if (loadedVersion < 19 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 20 prevents disabled mods from seeding unrelated conflict
            // chains and corrects exact left/right ring categorization.
            if (loadedVersion < 20 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 21 assigns shared finger, toe, and nail-mask resources to
            // Hands and Feet instead of leaving genuine nail conflicts under Other.
            if (loadedVersion < 21 && preset.Type == WardrobePresetType.Outfit)
                preset.AutomaticLayersScanned = false;
            // Version 22 replaces inferred conflict graphs with capture-time attribution
            // from Penumbra's live player resource tree. Remove only recognizably
            // generated legacy entries; manually added entries have no affected paths.
            if (loadedVersion < 22 && preset.Type == WardrobePresetType.Outfit)
            {
                if (preset.AutomaticLayersScanned)
                    preset.Mods?.RemoveAll(mod => mod.AffectedItems is { Count: > 0 });
                if (preset.Mods is not null)
                    foreach (var mod in preset.Mods.Where(mod => mod.Role == WardrobeLayerRole.Conflict))
                    {
                        mod.Role = WardrobeLayerRole.Base;
                        mod.Enabled = true;
                    }
                preset.AutomaticLayersScanned = true;
            }
            // Version 23 keeps only pairwise conflicts among mods observed on the
            // captured outfit. Version 22 entries included every loaded player mod,
            // so discard only those generated entries and leave manual layers intact.
            if (loadedVersion < 23 && preset.Type == WardrobePresetType.Outfit)
            {
                preset.Mods?.RemoveAll(mod => mod.CapturedFromResources);
                preset.AutomaticLayersScanned = false;
            }
            // Version 24 separates all captured clothing/equipment mods used by Apply
            // from the smaller pairwise-conflict list displayed in the editor.
            if (loadedVersion < 24 && preset.Type == WardrobePresetType.Outfit)
            {
                preset.Mods?.RemoveAll(mod => mod.CapturedFromResources);
                preset.RegisteredOutfitMods = [];
                preset.AutomaticLayersScanned = false;
            }
            // Version 25 registers every active equipment resource provider, including
            // mods that do not expose parseable conflict metadata. Older captures can
            // therefore be incomplete and must be captured again from the worn outfit.
            if (loadedVersion < 25 && preset.Type == WardrobePresetType.Outfit)
            {
                preset.Mods?.RemoveAll(mod => mod.CapturedFromResources);
                preset.RegisteredOutfitMods = [];
                preset.AutomaticLayersScanned = false;
            }
            // Version 26 replaces automatic resource/conflict capture with a direct
            // mirror of Glamourer's own manual mod associations. Old outfit rules are
            // not compatible with that model; linked designs repopulate them on load.
            if (loadedVersion < 26 && preset.Type == WardrobePresetType.Outfit)
            {
                preset.Mods = [];
                preset.RegisteredOutfitMods = [];
                preset.AutomaticLayersScanned = false;
                preset.EquipmentItemIds.Clear();
            }
            if (preset.Type != WardrobePresetType.Outfit || !folderIds.Contains(preset.FolderId)) preset.FolderId = Guid.Empty;
            preset.Mods ??= [];
            preset.RegisteredOutfitMods ??= [];
            foreach (var mod in preset.Mods)
            {
                mod.Directory ??= string.Empty;
                mod.Name ??= string.Empty;
                mod.Priority = Math.Clamp(mod.Priority, -9999, 9999);
                mod.CapturedPriority = Math.Clamp(mod.CapturedPriority, -999999, 999999);
                mod.Slots ??= [];
                mod.AffectedItems ??= [];
                mod.Options = mod.Options is null
                    ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, List<string>>(mod.Options, StringComparer.OrdinalIgnoreCase);
            }
            foreach (var mod in preset.RegisteredOutfitMods)
            {
                mod.Directory ??= string.Empty;
                mod.Name ??= string.Empty;
                mod.Slots ??= [];
                mod.AffectedItems ??= [];
                mod.Options = mod.Options is null
                    ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, List<string>>(mod.Options, StringComparer.OrdinalIgnoreCase);
            }
        }
        Data.Version = 29;
    }

    private int RecoverManagedImagePaths()
    {
        var recovered = 0;
        foreach (var preset in Data.Presets.Where(preset => string.IsNullOrWhiteSpace(preset.ImagePath) || !File.Exists(preset.ImagePath)))
        {
            var match = Directory.EnumerateFiles(imagesDirectory, preset.Id.ToString("N") + ".*")
                .Where(IsSupportedImage)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (match is null) continue;
            preset.ImagePath = match;
            recovered++;
        }
        return recovered;
    }
}

internal sealed record ImageRelink(WardrobePreset Preset, string OldPath, string SourcePath);
internal sealed record ImageCleanupResult(int ManagedDeleted, int SelfiesDeleted)
{
    public int TotalDeleted => ManagedDeleted + SelfiesDeleted;
}

internal readonly record struct NormalizedCrop(float X, float Y, float Width, float Height)
{
    public Rectangle ToPixels(int width, int height) => new(
        (int)MathF.Round(Math.Clamp(X, 0f, 1f) * width),
        (int)MathF.Round(Math.Clamp(Y, 0f, 1f) * height),
        Math.Max(1, (int)MathF.Round(Math.Clamp(Width, 0f, 1f) * width)),
        Math.Max(1, (int)MathF.Round(Math.Clamp(Height, 0f, 1f) * height)));
}
