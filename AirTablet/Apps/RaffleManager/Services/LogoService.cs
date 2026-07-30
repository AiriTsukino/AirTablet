using Dalamud.Interface.Textures.TextureWraps;

namespace RaffleManager.Services;

internal sealed class LogoService : IDisposable
{
    private readonly Configuration config;
    private IDalamudTextureWrap? texture;
    private string loadedPath = string.Empty;
    private bool loading;
    private readonly object gate = new();

    public LogoService(Configuration config)
    {
        this.config = config;
    }

    public bool HasCustomLogoPath => !string.IsNullOrWhiteSpace(config.Profile.CustomLogoPath);

    public IDalamudTextureWrap? Texture
    {
        get
        {
            EnsureLoadStarted();
            return texture;
        }
    }

    public string Status { get; private set; } = "No custom logo selected.";

    public void Refresh()
    {
        lock (gate)
        {
            texture?.Dispose();
            texture = null;
            loadedPath = string.Empty;
            loading = false;
        }
        EnsureLoadStarted();
    }

    public void Dispose() => texture?.Dispose();

    private void EnsureLoadStarted()
    {
        var path = config.Profile.CustomLogoPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "No custom logo selected.";
            return;
        }

        lock (gate)
        {
            if (loadedPath.Equals(path, StringComparison.OrdinalIgnoreCase) && texture is not null)
                return;
            if (loading) return;
            if (!File.Exists(path))
            {
                Status = "Logo file was not found.";
                return;
            }
            loading = true;
            loadedPath = path;
            Status = $"Loading {Path.GetFileName(path)}...";
        }

        _ = LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var newTexture = await DalamudServices.TextureProvider.CreateFromImageAsync(stream, true, Path.GetFileName(path));
            lock (gate)
            {
                texture?.Dispose();
                texture = newTexture;
                Status = $"Loaded {Path.GetFileName(path)}.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Logo load failed: {ex.Message}";
            DalamudServices.Log.Warning(ex, "RaffleManager failed to load custom logo.");
        }
        finally
        {
            lock (gate) loading = false;
        }
    }
}
