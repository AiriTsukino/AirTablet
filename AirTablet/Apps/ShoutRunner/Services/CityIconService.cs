using Dalamud.Interface.Textures.TextureWraps;

namespace ShoutRunner;

internal sealed class CityIconService : IDisposable
{
    private readonly Dictionary<CityTarget, IDalamudTextureWrap?> textures = [];
    private readonly object gate = new();
    private bool loading;

    public IDalamudTextureWrap? Get(CityTarget city)
    {
        EnsureLoading();
        lock (gate)
            return textures.GetValueOrDefault(city);
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var texture in textures.Values)
                texture?.Dispose();
            textures.Clear();
        }
    }

    private void EnsureLoading()
    {
        lock (gate)
        {
            if (loading || textures.Count > 0)
                return;
            loading = true;
        }

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var loaded = new Dictionary<CityTarget, IDalamudTextureWrap?>();
        try
        {
            loaded[CityTarget.LimsaLominsa] = await LoadOneAsync("LimsaLominsa.png");
            loaded[CityTarget.Gridania] = await LoadOneAsync("Gridania.png");
            loaded[CityTarget.Uldah] = await LoadOneAsync("Uldah.png");

            lock (gate)
            {
                foreach (var (city, texture) in loaded)
                    textures[city] = texture;
                loaded.Clear();
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShoutRunner failed to load its bundled city icons.");
        }
        finally
        {
            foreach (var texture in loaded.Values)
                texture?.Dispose();
            lock (gate)
                loading = false;
        }
    }

    private static async Task<IDalamudTextureWrap?> LoadOneAsync(string fileName)
    {
        var root = DalamudServices.PluginInterface.AssemblyLocation.Directory?.FullName;
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var path = Path.Combine(root, "Resources", "Apps", "ShoutRunner", fileName);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await DalamudServices.TextureProvider.CreateFromImageAsync(stream, true, fileName);
    }
}
