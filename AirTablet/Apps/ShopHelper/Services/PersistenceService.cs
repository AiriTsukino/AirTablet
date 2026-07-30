namespace ShopHelper.Services;

internal sealed class PersistenceService(Configuration config) : IDisposable
{
    private readonly Configuration config = config;
    private bool disposed;

    public void SaveNow()
    {
        if (disposed)
            return;

        config.Clamp();
        DalamudServices.PluginInterface.SavePluginConfig(config);
    }

    public void Dispose()
    {
        disposed = true;
    }
}
