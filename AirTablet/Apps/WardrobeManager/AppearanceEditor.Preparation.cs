using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private Guid preparingPreset;
    private string preparingJson = string.Empty;
    private JObject? preparationDesign;
    private readonly Queue<string> preparationFields = [];

    public void WarmSavedAppearance(WardrobePreset owner)
    {
        // Warm from the already-cached preset, never by polling Glamourer IPC.
        // The real editor still fetches its authoritative design when opened.
        var json = owner.Type == WardrobePresetType.Character ? owner.CharacterAppearanceJson : owner.OutfitAppearanceJson;
        if (preparingPreset != owner.Id || preparingJson != json)
        {
            preparingPreset = owner.Id;
            preparingJson = json;
            preparationFields.Clear();
            preparationDesign = null;
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                preparationDesign = JObject.Parse(json);
                if (preparationDesign["Customize"] is JObject fields)
                    foreach (var field in fields.Properties().Where(field => field.Name != "BodyType" && field.Value is JObject))
                        preparationFields.Enqueue(field.Name);
            }
            catch (JsonException) { return; } // Existing editor reports invalid sources on open.
        }
        if (preparationDesign is null) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (preparationFields.TryDequeue(out var field))
        {
            catalog.Choices(preparationDesign, field);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) break;
        }
    }
}
