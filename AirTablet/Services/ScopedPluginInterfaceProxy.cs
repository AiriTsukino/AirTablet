using System.Reflection;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace AirTablet.Services;

internal class ScopedPluginInterfaceProxy : DispatchProxy
{
    private IDalamudPluginInterface inner = null!;
    private DirectoryInfo configDirectory = null!;
    private FileInfo configFile = null!;
    private Type configType = null!;
    private string internalName = string.Empty;

    public ScopedPluginInterfaceProxy()
    {
    }

    public static IDalamudPluginInterface CreateScoped(
        IDalamudPluginInterface inner,
        string internalName,
        DirectoryInfo configDirectory,
        Type configType)
    {
        Directory.CreateDirectory(configDirectory.FullName);
        var proxy = Create<IDalamudPluginInterface, ScopedPluginInterfaceProxy>();
        var state = (ScopedPluginInterfaceProxy)(object)proxy;
        state.inner = inner;
        state.internalName = internalName;
        state.configDirectory = configDirectory;
        state.configFile = new FileInfo(Path.Combine(configDirectory.FullName, "config.json"));
        state.configType = configType;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            throw new MissingMethodException("Hosted module called an unknown plugin-interface member.");

        switch (targetMethod.Name)
        {
            case "get_InternalName":
                return internalName;
            case "get_ConfigDirectory":
                return configDirectory;
            case "get_ConfigFile":
                return configFile;
            case "GetPluginConfig":
                return LoadConfiguration();
            case "SavePluginConfig":
                SaveConfiguration(args?.FirstOrDefault() as IPluginConfiguration);
                return null;
            case "GetPluginConfigDirectory":
                return configDirectory.FullName;
        }

        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private object? LoadConfiguration()
    {
        try
        {
            if (!configFile.Exists)
                return null;
            return JsonConvert.DeserializeObject(File.ReadAllText(configFile.FullName), configType);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AirTablet could not load hosted config for {Module}.", internalName);
            return null;
        }
    }

    private void SaveConfiguration(IPluginConfiguration? configuration)
    {
        if (configuration is null)
            return;

        try
        {
            Directory.CreateDirectory(configDirectory.FullName);
            var temporaryPath = configFile.FullName + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(configuration, Formatting.Indented));
            File.Copy(temporaryPath, configFile.FullName, true);
            File.Delete(temporaryPath);
            configFile.Refresh();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AirTablet could not save hosted config for {Module}.", internalName);
        }
    }
}
