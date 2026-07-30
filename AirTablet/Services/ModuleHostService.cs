using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin;

namespace AirTablet.Services;

internal sealed class ModuleHostService : IDisposable
{
    private sealed class ModuleLoadContext(string modulePath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(modulePath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null ||
                assemblyName.Name.StartsWith("Dalamud", StringComparison.Ordinal) ||
                assemblyName.Name is "Newtonsoft.Json" or "FFXIVClientStructs" or "Lumina")
                return null;

            var dependency = resolver.ResolveAssemblyToPath(assemblyName);
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
    }

    private sealed class HostedModule(
        string id,
        object instance,
        MethodInfo draw,
        MethodInfo? tick,
        MethodInfo? navigateBack,
        MethodInfo dispose,
        ModuleLoadContext context)
    {
        public string Id { get; } = id;
        public object Instance { get; } = instance;
        public MethodInfo DrawMethod { get; } = draw;
        public MethodInfo? TickMethod { get; } = tick;
        public MethodInfo? NavigateBackMethod { get; } = navigateBack;
        public MethodInfo DisposeMethod { get; } = dispose;
        public ModuleLoadContext Context { get; } = context;
    }

    private readonly Dictionary<string, HostedModule> modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> errors = [];

    public ModuleHostService()
    {
        LoadModules();
    }

    public IReadOnlyCollection<string> AvailableModuleIds => modules.Keys;
    public string Status => errors.Count == 0
        ? $"{modules.Count} bundled module(s) loaded."
        : $"{modules.Count} module(s) loaded; {errors.Count} failed.";

    public bool IsAvailable(string id) => modules.ContainsKey(id);

    public void TickAll()
    {
        foreach (var module in modules.Values)
        {
            if (module.TickMethod is null)
                continue;
            try
            {
                module.TickMethod.Invoke(module.Instance, null);
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Error(Unwrap(ex), "AirTablet module {Module} failed during its background tick.", module.Id);
            }
        }
    }

    public bool Draw(string id)
    {
        if (!modules.TryGetValue(id, out var module))
            return false;

        try
        {
            module.DrawMethod.Invoke(module.Instance, null);
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(Unwrap(ex), "AirTablet module {Module} failed while drawing.", id);
            return false;
        }
    }

    public bool NavigateBack(string id)
    {
        if (!modules.TryGetValue(id, out var module) || module.NavigateBackMethod is null)
            return false;

        try
        {
            return module.NavigateBackMethod.Invoke(module.Instance, null) as bool? ?? false;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(Unwrap(ex), "AirTablet module {Module} could not navigate back.", id);
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var module in modules.Values.Reverse())
        {
            try
            {
                module.DisposeMethod.Invoke(module.Instance, null);
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(Unwrap(ex), "AirTablet module {Module} failed to dispose.", module.Id);
            }
            module.Context.Unload();
        }
        modules.Clear();
    }

    private void LoadModules()
    {
        var assemblyDirectory = DalamudServices.PluginInterface.AssemblyLocation.Directory?.FullName;
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            errors.Add("Modules directory");
            DalamudServices.Log.Error("Dalamud did not provide the AirTablet installation directory.");
            return;
        }
        var modulesDirectory = Path.Combine(assemblyDirectory, "Modules");
        if (!Directory.Exists(modulesDirectory))
            return;

        var configRoot = new DirectoryInfo(Path.Combine(
            DalamudServices.PluginInterface.ConfigDirectory.FullName,
            "Modules"));

        foreach (var path in Directory.EnumerateFiles(modulesDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            ModuleLoadContext? context = null;
            try
            {
                context = new ModuleLoadContext(path);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
                var moduleType = assembly.GetTypes().FirstOrDefault(type =>
                    type.IsClass &&
                    !type.IsAbstract &&
                    type.Name.Equals("AirTabletModule", StringComparison.Ordinal));
                if (moduleType is null)
                    throw new TypeLoadException("No public AirTabletModule entry point was found.");

                var instance = Activator.CreateInstance(moduleType)
                    ?? throw new InvalidOperationException("The module entry point could not be constructed.");
                var id = moduleType.GetProperty("InternalName", BindingFlags.Public | BindingFlags.Instance)?
                    .GetValue(instance) as string;
                if (string.IsNullOrWhiteSpace(id))
                    id = assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(path);

                var configType = assembly.GetTypes().First(type =>
                    typeof(Dalamud.Configuration.IPluginConfiguration).IsAssignableFrom(type) &&
                    !type.IsAbstract);
                var scopedInterface = ScopedPluginInterfaceProxy.CreateScoped(
                    DalamudServices.PluginInterface,
                    id,
                    new DirectoryInfo(Path.Combine(configRoot.FullName, id, "config")),
                    configType);

                var initialize = moduleType.GetMethod("Initialize", [typeof(IDalamudPluginInterface)])
                    ?? throw new MissingMethodException(moduleType.FullName, "Initialize(IDalamudPluginInterface)");
                var draw = moduleType.GetMethod("Draw", Type.EmptyTypes)
                    ?? throw new MissingMethodException(moduleType.FullName, "Draw()");
                var dispose = moduleType.GetMethod("Dispose", Type.EmptyTypes)
                    ?? throw new MissingMethodException(moduleType.FullName, "Dispose()");
                var tick = moduleType.GetMethod("Tick", Type.EmptyTypes);
                var navigateBack = moduleType.GetMethod("NavigateBack", Type.EmptyTypes);

                initialize.Invoke(instance, [scopedInterface]);
                modules.Add(id, new HostedModule(id, instance, draw, tick, navigateBack, dispose, context));
                context = null;
            }
            catch (Exception ex)
            {
                var moduleName = Path.GetFileNameWithoutExtension(path);
                errors.Add(moduleName);
                DalamudServices.Log.Error(Unwrap(ex), "AirTablet could not load bundled module {Module}.", moduleName);
                context?.Unload();
            }
        }
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException!
            : exception;
}
