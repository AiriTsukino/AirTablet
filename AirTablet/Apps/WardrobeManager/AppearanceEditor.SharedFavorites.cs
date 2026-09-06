namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private GlamourerFavoritesSnapshot sharedFavorites = new([], []);
    private System.Threading.Tasks.Task<GlamourerFavoritesSnapshot>? sharedFavoritesRead;

    private void BeginSharedFavoritesRead()
    {
        // The app's interface is scoped to AirTablet/Apps/WardrobeManager.
        // Resolve sibling plugins from the tablet's actual config directory.
        var configRoot = AirTablet.DalamudServices.PluginInterface.ConfigDirectory.Parent;
        if (configRoot is null) return;
        var path = System.IO.Path.Combine(configRoot.FullName, "Glamourer", "favorites.json");
        sharedFavoritesRead = System.Threading.Tasks.Task.Run(() => GlamourerFavoritesSnapshot.Read(path));
    }

    private void FinishSharedFavoritesRead()
    {
        if (sharedFavoritesRead is not { IsCompleted: true } read) return;
        sharedFavoritesRead = null;
        if (!read.IsCompletedSuccessfully) return;
        sharedFavorites = read.Result;
        equipmentFilter = string.Empty;
        equipmentSearchKey = string.Empty;
    }

    private bool IsItemFavorite(uint id)
        => sharedFavorites.Items.Contains(id) || preferences?.EquipmentFavorites.Contains(id) == true;

    private bool IsFacewearFavorite(uint id)
        => sharedFavorites.Facewear.Contains(id) || preferences?.FacewearFavorites.Contains(id) == true;
}
