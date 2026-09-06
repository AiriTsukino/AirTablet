using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

// Deliberately isolated, unsupported integration. Never creates or deletes a
// design and never writes Glamourer's files. Resolve anew after plugin reloads.
internal sealed class GlamourerLiveDesign
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly object manager, converter, saveService, fileSystem, customize;
    private readonly Type designType, baseType;
    private readonly MethodInfo parse, apply, setData, rename, move, queue;
    private readonly FieldInfo data, application;
    private readonly PropertyInfo mods, node, lastEdit;

    internal GlamourerLiveDesign(object plugin) : this(plugin.GetType().Assembly, ServiceResolver(plugin)) { }

    private static Func<string, object> ServiceResolver(object plugin)
    {
        var assembly = plugin.GetType().Assembly;
        var services = Required(plugin.GetType().GetField("_services", Flags)?.GetValue(plugin), "service container");
        var get = services.GetType().GetMethods().Single(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return name => Required(get.MakeGenericMethod(assembly.GetType(name, true)!).Invoke(services, null), name);
    }

    internal GlamourerLiveDesign(Assembly assembly, Func<string, object> Service)
    {
        manager = Service("Glamourer.Designs.DesignManager");
        converter = Service("Glamourer.Designs.DesignConverter");
        saveService = Service("Glamourer.Services.SaveService");
        fileSystem = Service("Glamourer.Designs.DesignFileSystem");
        customize = Service("Glamourer.Services.CustomizeService");
        designType = assembly.GetType("Glamourer.Designs.Design", true)!;
        baseType = designType.BaseType!;
        parse = converter.GetType().GetMethods().Single(m => m.Name is "FromJObject" or "FromJsonElement" && m.GetParameters().Length == 3);
        apply = manager.GetType().GetMethods().Single(m => m.Name == "ApplyDesign" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == baseType);
        setData = baseType.GetMethod("SetDesignData", Flags) ?? throw new MissingMethodException("SetDesignData");
        rename = manager.GetType().GetMethod("Rename", [designType, typeof(string)]) ?? throw new MissingMethodException("Rename");
        move = fileSystem.GetType().GetMethods().Single(m => m.Name == "RenameAndMoveWithDuplicates" && m.GetParameters().Length == 2);
        queue = saveService.GetType().GetMethods().Single(m => m.Name == "QueueSave" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1).MakeGenericMethod(designType);
        data = baseType.GetField("_designData", Flags) ?? throw new MissingFieldException("_designData");
        application = baseType.GetField("Application", Flags) ?? throw new MissingFieldException("Application");
        mods = designType.GetProperty("AssociatedMods") ?? throw new MissingMemberException("AssociatedMods");
        node = designType.GetProperty("Node") ?? throw new MissingMemberException("Node");
        lastEdit = designType.GetProperty("LastEdit") ?? throw new MissingMemberException("LastEdit");
    }

    internal static GlamourerLiveDesign Connect()
    {
        var exposed = DalamudServices.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "Glamourer" && p.IsLoaded)
            ?? throw new InvalidOperationException("Glamourer is not loaded.");
        var wrapper = exposed.GetType().GetFields(Flags).Single(f => f.FieldType.FullName == "Dalamud.Plugin.Internal.Types.LocalPlugin").GetValue(exposed);
        var plugin = wrapper?.GetType().GetField("instance", Flags)?.GetValue(wrapper);
        return new GlamourerLiveDesign(Required(plugin, "Glamourer instance"));
    }

    internal JObject Decode(string input)
    {
        var method = converter.GetType().GetMethods().Single(m => m.Name == "FromBase64" && m.GetParameters()[0].ParameterType == typeof(string));
        var parsed = Required(method.Invoke(converter, [input, true, true, (byte)0]), "captured design");
        var share = converter.GetType().GetMethods().Single(m => m.Name == "ShareJObject" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == baseType);
        return JObject.Parse(Required(share.Invoke(converter, [parsed]), "captured JSON").ToString()!);
    }

    internal void Update(Guid id, JObject wanted, string folder, Func<bool> verify)
    {
        var storage = Required(manager.GetType().GetField("Designs")?.GetValue(manager), "design storage");
        var live = Required(storage.GetType().GetMethod("ByIdentifier", [typeof(Guid)])?.Invoke(storage, [id]), "linked design (it may have been deleted)");
        if ((bool)baseType.GetMethod("WriteProtected")!.Invoke(live, null)!)
            throw new InvalidOperationException("This Glamourer design is write-protected.");
        var json = (JObject)wanted.DeepClone();
        json["Identifier"] = id.ToString();
        object parseInput;
        if (parse.Name == "FromJObject")
        {
            var jsonType = parse.GetParameters()[0].ParameterType;
            parseInput = Required(jsonType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])?.Invoke(null, [json.ToString()]), "design JSON");
        }
        else
        {
            using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
            parseInput = document.RootElement.Clone();
        }
        var parsed = Required(parse.Invoke(converter, [parseInput, true, true]), "validated design");
        if (!designType.IsInstanceOfType(parsed)) throw new InvalidOperationException("Glamourer did not parse a complete design.");
        var export = converter.GetType().GetMethods().Single(m => m.Name == "ShareJObject" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == designType);
        JObject Export(object value) => JObject.Parse(Required(export.Invoke(converter, [value]), "verification export").ToString()!);
        var expected = Export(parsed);

        var liveNode = Required(node.GetValue(live), "design folder node");
        var oldPath = (string)Required(liveNode.GetType().GetProperty("FullPath")?.GetValue(liveNode), "design path");
        var oldName = (string)designType.GetProperty("Name")!.GetValue(live)!;
        var oldData = data.GetValue(live)!;
        var oldApplication = application.GetValue(live)!;
        var oldEdit = lastEdit.GetValue(live);
        var liveMods = (IDictionary)Required(mods.GetValue(live), "associated mods");
        var savedMods = SnapshotMods(liveMods);
        var newMods = SnapshotMods((IDictionary)Required(mods.GetValue(parsed), "parsed mods"));
        var materials = Required(baseType.GetMethod("GetMaterialDataRef")?.Invoke(live, null), "materials");
        var parsedMaterials = Required(baseType.GetMethod("GetMaterialDataRef")?.Invoke(parsed, null), "parsed materials");
        var values = materials.GetType().GetProperty("Values") ?? throw new MissingMemberException("material Values");
        var oldRows = ((IEnumerable)values.GetValue(materials)!).Cast<object>().ToArray();
        var newRows = ((IEnumerable)values.GetValue(parsedMaterials)!).Cast<object>().ToArray();
        var clear = materials.GetType().GetMethod("Clear", Type.EmptyTypes) ?? throw new MissingMethodException("material Clear");
        var add = materials.GetType().GetMethods().Single(m => m.Name == "AddOrUpdateValue" && m.GetParameters()[0].ParameterType == typeof(uint));
        void Rows(object[] rows)
        {
            clear.Invoke(materials, null);
            foreach (var row in rows) add.Invoke(materials, [row.GetType().GetField("Item1")!.GetValue(row), row.GetType().GetField("Item2")!.GetValue(row)]);
        }
        void Mods(DictionaryEntry[] entries) { liveMods.Clear(); foreach (var entry in entries) liveMods.Add(entry.Key, entry.Value); }
        try
        {
            // Use Glamourer's editor for normal edit notifications. Then copy
            // the validated complete data, including unchecked stored values,
            // which ApplyDesign intentionally skips when applying a preset.
            var editorSwitch = apply.DeclaringType?.GetField("_forceFullItemOff", Flags);
            var previousSwitch = editorSwitch?.GetValue(manager);
            try { apply.Invoke(manager, [live, parsed, Activator.CreateInstance(apply.GetParameters()[2].ParameterType)]); }
            finally { if (editorSwitch is not null) editorSwitch.SetValue(manager, previousSwitch); }
            setData.Invoke(live, [customize, data.GetValue(parsed)]);
            application.SetValue(live, application.GetValue(parsed));
            Rows(newRows);
            Mods(newMods);
            rename.Invoke(manager, [live, wanted.Value<string>("Name") ?? oldName]);
            var targetFolder = folder.Replace('\\', '/').Trim('/');
            var currentFolder = oldPath.Contains('/') ? oldPath[..oldPath.LastIndexOf('/')] : string.Empty;
            if (!string.Equals(targetFolder, currentFolder, StringComparison.Ordinal))
            {
                var leaf = oldPath[(oldPath.LastIndexOf('/') + 1)..];
                move.Invoke(fileSystem, [liveNode, targetFolder.Length == 0 ? leaf : targetFolder + "/" + leaf]);
            }
            lastEdit.SetValue(live, DateTimeOffset.UtcNow);
            var actual = Export(live);
            foreach (var section in new[] { "Customize", "Parameters", "Equipment", "Bonus", "Materials", "Mods" })
                if (!JToken.DeepEquals(expected[section], actual[section]))
                    throw new InvalidOperationException("Live design verification failed for " + section + ".");
            if (!verify()) throw new InvalidOperationException("Glamourer did not retain all requested edits.");
            queue.Invoke(saveService, [live]);
        }
        catch (Exception failure)
        {
            try
            {
                setData.Invoke(live, [customize, oldData]); application.SetValue(live, oldApplication);
                Rows(oldRows); Mods(savedMods); rename.Invoke(manager, [live, oldName]);
                move.Invoke(fileSystem, [liveNode, oldPath]); lastEdit.SetValue(live, oldEdit);
                queue.Invoke(saveService, [live]);
            }
            catch (Exception rollback) { throw new AggregateException("Save failed and restoring the original values also failed. The design ID was not deleted.", failure, rollback); }
            throw new InvalidOperationException("Save failed; the original design values were restored.", failure);
        }
    }

    private static object Required(object? value, string name)
        => value ?? throw new InvalidOperationException("Glamourer compatibility check failed: " + name + ".");

    internal static DictionaryEntry[] SnapshotMods(IDictionary source)
    {
        var entries = new List<DictionaryEntry>();
        foreach (DictionaryEntry entry in source) entries.Add(entry);
        return entries.ToArray();
    }
}
