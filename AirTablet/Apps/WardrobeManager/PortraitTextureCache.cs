using Dalamud.Interface.Textures.TextureWraps;

namespace WardrobeManager;

internal sealed class PortraitTextureCache : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, IDalamudTextureWrap?> textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public IDalamudTextureWrap? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var shouldLoad = false;
        lock (sync)
        {
            if (disposed) return null;
            if (textures.TryGetValue(path, out var texture)) return texture;
            if (retryAfter.TryGetValue(path, out var retry) && DateTime.UtcNow < retry) return null;
            shouldLoad = loading.Add(path);
        }
        if (shouldLoad) _ = Load(path);
        return null;
    }

    public void Invalidate(string path)
    {
        IDalamudTextureWrap? texture = null;
        lock (sync)
        {
            textures.Remove(path, out texture);
            // Removing this marker also rejects an in-flight load when it completes.
            loading.Remove(path);
            retryAfter.Remove(path);
        }
        texture?.Dispose();
    }

    private async Task Load(string path)
    {
        IDalamudTextureWrap? texture = null;
        Exception? failure = null;
        try
        {
            await using var stream = File.OpenRead(path);
            texture = await DalamudServices.TextureProvider.CreateFromImageAsync(stream, true, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        IDalamudTextureWrap? replaced = null;
        var accepted = false;
        lock (sync)
        {
            accepted = !disposed && loading.Remove(path);
            if (accepted && texture is not null)
            {
                textures.Remove(path, out replaced);
                textures[path] = texture;
                retryAfter.Remove(path);
            }
            else if (accepted && failure is not null)
            {
                retryAfter[path] = DateTime.UtcNow.AddSeconds(5);
            }
        }
        replaced?.Dispose();
        if (!accepted) texture?.Dispose();
        if (failure is not null) DalamudServices.Log.Debug(failure, "WardrobeManager could not load portrait {Path}.", path);
    }

    public void Dispose()
    {
        List<IDalamudTextureWrap?> owned;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            owned = textures.Values.ToList();
            textures.Clear();
            loading.Clear();
            retryAfter.Clear();
        }
        foreach (var texture in owned) texture?.Dispose();
    }
}
