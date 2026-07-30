using AirTablet.Models;
using Dalamud.Interface.Textures.TextureWraps;

namespace AirTablet.Services;

internal sealed class TextureCache : IDisposable
{
    private const string DefaultWallpaperPath = @"Resources\DefaultWallpaper.png";

    private sealed class Entry
    {
        public IDalamudTextureWrap? Texture;
        public bool Loading;
        public string Source = string.Empty;
    }

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly Dictionary<string, Entry> entries = [];
    private readonly object gate = new();

    public IDalamudTextureWrap? GetIcon(AppDescriptor app)
    {
        var key = $"app:{app.Id}";
        EnsureLoading(key, app.IconUrl);
        lock (gate)
            return entries.TryGetValue(key, out var entry) ? entry.Texture : null;
    }

    public IDalamudTextureWrap? GetResourceIcon(string id, string relativePath)
    {
        var key = $"resource:{id}";
        EnsureLoading(key, relativePath);
        lock (gate)
            return entries.TryGetValue(key, out var entry) ? entry.Texture : null;
    }

    public IDalamudTextureWrap? GetWallpaper(string path)
    {
        var source = string.IsNullOrWhiteSpace(path) ? DefaultWallpaperPath : path;
        EnsureLoading("wallpaper", source);
        lock (gate)
            return entries.TryGetValue("wallpaper", out var entry) ? entry.Texture : null;
    }

    public void InvalidateWallpaper()
    {
        lock (gate)
        {
            if (entries.Remove("wallpaper", out var entry))
                (entry.Texture as IDisposable)?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var entry in entries.Values)
                (entry.Texture as IDisposable)?.Dispose();
            entries.Clear();
        }
        http.Dispose();
    }

    private void EnsureLoading(string key, string source)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                if (existing.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
                {
                    if (existing.Texture is not null || existing.Loading)
                        return;
                }
                else
                {
                    (existing.Texture as IDisposable)?.Dispose();
                    entries.Remove(key);
                }
            }

            entries[key] = new Entry
            {
                Loading = true,
                Source = source,
            };
        }

        _ = LoadAsync(key, source);
    }

    private async Task LoadAsync(string key, string source)
    {
        IDalamudTextureWrap? texture = null;
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await using var remote = await http.GetStreamAsync(uri);
                texture = await DalamudServices.TextureProvider.CreateFromImageAsync(
                    remote,
                    true,
                    key,
                    default);
            }
            else
            {
                var localPath = Path.IsPathRooted(source)
                    ? source
                    : Path.Combine(
                        DalamudServices.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty,
                        source);
                if (File.Exists(localPath))
                {
                    await using var local = File.OpenRead(localPath);
                    texture = await DalamudServices.TextureProvider.CreateFromImageAsync(
                        local,
                        true,
                        Path.GetFileName(localPath),
                        default);
                }
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(
                ex,
                "AirTablet failed to load texture {Key} from {Source}.",
                key,
                source);
        }
        finally
        {
            lock (gate)
            {
                if (entries.TryGetValue(key, out var entry) &&
                    entry.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
                {
                    (entry.Texture as IDisposable)?.Dispose();
                    entry.Texture = texture;
                    entry.Loading = false;
                    texture = null;
                }
            }
            (texture as IDisposable)?.Dispose();
        }
    }
}
