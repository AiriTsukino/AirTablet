using System.Globalization;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Newtonsoft.Json.Linq;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor : IDisposable
{
    private readonly MaterialReadback materialReadback = new();
    private MaterialTarget? captureTarget;
    private Half[]? capturedMaterialColors;
    private nint capturedMaterialTexture;
    private readonly MaterialHoverPreview materialHover = new(GetMaterialTextureSlot);

    public void Dispose()
    {
        materialHover.Dispose();
        materialReadback.Dispose();
        equipmentSearch?.Dispose();
        equipmentSearch = null;
        equipmentSearchKey = string.Empty;
        if (facewearScan is not null)
        {
            facewearScan.Dispose();
            facewearScan = null;
            facewearChoices.Clear();
            facewearLoaded = false;
        }
    }
    public void Tick() => materialHover.Tick();
    private sealed record MaterialTarget(uint Key, string Path, string Mode, int Rows);
    private readonly List<MaterialTarget> materialTargets = [];
    private int materialScanStep = -1;
    private ulong materialScanCharacter;
    private int materialTargetIndex;
    private int newMaterialRow = 1;
    private string materialScanStatus = string.Empty;

    private void DrawMaterialTargets()
    {
        ImGui.TextWrapped("Add advanced dyes from your currently worn appearance. Apply the outfit first, then scan its materials.");
        if (ImGui.Button("Scan worn materials"))
        {
            materialHover.Dispose();
            materialReadback.Dispose();
            captureTarget = null;
            capturedMaterialColors = null;
            materialTargets.Clear();
            materialTargetIndex = 0;
            materialScanCharacter = DalamudServices.PlayerState.ContentId;
            materialScanStep = 0;
            materialScanStatus = "Scanning one equipment slot per frame...";
        }
        if (materialScanStep >= 0)
        {
            if (!DalamudServices.PlayerState.IsLoaded || materialScanCharacter != DalamudServices.PlayerState.ContentId)
            {
                materialTargets.Clear();
                materialScanStep = -1;
                materialScanStatus = "Character changed or is unavailable. Scan again after loading.";
            }
            else
            {
                materialTargets.AddRange(ReadMaterialTargets(materialScanStep++));
                if (materialScanStep >= 20)
                {
                    materialScanStep = -1;
                    materialScanStatus = $"Found {materialTargets.Count} supported materials. Scan again after changing equipment.";
                }
            }
        }
        if (materialScanStatus.Length > 0) ImGui.TextWrapped(materialScanStatus);
        if (materialTargets.Count == 0) return;
        materialTargetIndex = Math.Clamp(materialTargetIndex, 0, materialTargets.Count - 1);
        var target = materialTargets[materialTargetIndex];
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##new-material-target", TargetLabel(target)))
        {
            for (var i = 0; i < materialTargets.Count; i++)
                if (ImGui.Selectable(TargetLabel(materialTargets[i]) + "##" + i, materialTargetIndex == i))
                { materialTargetIndex = i; newMaterialRow = 1; }
            ImGui.EndCombo();
        }
        target = materialTargets[materialTargetIndex];
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(target.Path);
        if (materialReadback.Pending)
        {
            var colors = materialReadback.Poll();
            if (colors is not null) { capturedMaterialColors = colors; materialScanStatus = "Live colours captured. Select a row and add it below."; }
            else if (!materialReadback.Pending) materialScanStatus = "The live colour capture was unavailable or timed out. You can retry.";
        }
        ImGui.BeginDisabled(materialReadback.Pending);
        if (ImGui.Button("Capture live colours"))
        {
            materialHover.Dispose();
            capturedMaterialColors = null;
            captureTarget = target;
            var captureSlot = (target.Key >> 24) switch { 2 => 18, 3 => 19, _ => (int)((target.Key >> 16) & 255) };
            var address = materialScanCharacter == DalamudServices.PlayerState.ContentId
                && ReadMaterialTargets(captureSlot).Any(item => item == target) ? GetMaterialTexture(target.Key) : 0;
            capturedMaterialTexture = address;
            materialScanStatus = materialReadback.Begin(address)
                ? "Waiting for a non-blocking live colour capture..."
                : "This material's live texture is unavailable or has an unsupported format.";
        }
        ImGui.EndDisabled();
        newMaterialRow = Math.Clamp(newMaterialRow, 1, target.Rows);
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderInt("##new-material-row", ref newMaterialRow, 1, target.Rows, "Row %d");
        if (captureTarget == target && capturedMaterialColors is not null
            && materialScanCharacter == DalamudServices.PlayerState.ContentId)
        {
            ImGui.Button("Hover to highlight this row");
            if (ImGui.IsItemHovered())
            {
                var shown = materialHover.Show(target.Key, newMaterialRow - 1, capturedMaterialColors, capturedMaterialTexture);
                ImGui.SetTooltip(shown ? "Highlighted in magenta on the worn character. Moving away restores the original texture."
                    : "The material changed or the preview is unavailable. Capture live colours again.");
            }
        }
        var key = (target.Key | (uint)(newMaterialRow - 1)).ToString("X8", CultureInfo.InvariantCulture);
        var exists = draft!["Materials"]?[key] is JObject;
        if (ImGui.Button(exists ? "Edit existing row" : "Add dye row"))
        {
            // Re-resolve native resources instead of trusting a stale scan or
            // retaining a pointer across frames/equipment changes.
            var scanSlot = (target.Key >> 24) switch { 2 => 18, 3 => 19, _ => (int)((target.Key >> 16) & 255) };
            var live = ReadMaterialTargets(scanSlot).Any(item => item == target);
            if (materialScanCharacter != DalamudServices.PlayerState.ContentId || !live)
                materialScanStatus = "This material changed. Scan the worn appearance again before adding a row.";
            else
            {
                if (draft["Materials"] is not JObject materials) draft["Materials"] = materials = new JObject();
                if (!exists)
                {
                    try
                    {
                        materials[key] = captureTarget == target && capturedMaterialColors is not null
                            ? MaterialRowDraft.FromLiveColors(target.Mode, capturedMaterialColors.AsSpan((newMaterialRow - 1) * 32, 32))
                            : MaterialRowDraft.Create(target.Mode);
                    }
                    catch (ArgumentException ex) { materialScanStatus = ex.Message; return; }
                }
                selected = key;
                hexOwner = string.Empty;
            }
        }
        ImGui.TextWrapped(captureTarget == target && capturedMaterialColors is not null
            ? "New rows use captured live colours and start disabled. Enable Apply for the row before saving."
            : "New rows start disabled with neutral colours. Capture live colours first to preserve the worn colours, or choose your own before enabling Apply.");
        AirTablet.UI.TabletSeparator.Draw();
    }

    private static string TargetLabel(MaterialTarget target)
    {
        var label = MaterialLabel(target.Key.ToString("X8", CultureInfo.InvariantCulture));
        var row = label.LastIndexOf(" · Row", StringComparison.Ordinal);
        return row >= 0 ? label[..row] : label;
    }

    private static unsafe List<MaterialTarget> ReadMaterialTargets(int scanSlot)
    {
        var result = new List<MaterialTarget>();
        var player = DalamudServices.ObjectTable.LocalPlayer;
        if (player is null || !DalamudServices.PlayerState.IsLoaded || scanSlot is < 0 or > 19) return result;
        var character = (Character*)player.Address;
        var draw = scanSlot < 18 ? character->DrawObject
            : character->DrawData.WeaponData[scanSlot - 18].DrawData.DrawObject;
        if (draw is null) return result;
        var model = (CharacterBase*)draw;
        var slot = scanSlot < 18 ? scanSlot : 0;
        if (model->SlotCount is <= 0 or > 32 || slot >= model->SlotCount || model->Materials is null) return result;
        for (var materialIndex = 0; materialIndex < CharacterBase.MaterialsPerSlot; materialIndex++)
        {
            var material = model->Materials[slot * CharacterBase.MaterialsPerSlot + materialIndex];
            if (material is null || material->AdditionalData is null || material->Strings is null
                || !material->HasColorTable || material->ColorTable is null) continue;
            var shader = material->ShpkName.ToString();
            var mode = shader switch { "character.shpk" => "Dawntrail", "characterlegacy.shpk" => "Legacy", _ => null };
            if (mode is null || material->ColorTableHeight is <= 0 or > 32) continue;
            var kind = scanSlot < 18 ? 1u : scanSlot == 18 ? 2u : 3u;
            var key = (kind << 24) | ((uint)slot << 16) | ((uint)materialIndex << 8);
            result.Add(new MaterialTarget(key, material->FileName.ToString(), mode, material->ColorTableHeight));
        }
        return result;
    }

    private static unsafe nint GetMaterialTexture(uint key)
    {
        var slot = (FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)GetMaterialTextureSlot(key);
        return slot is null || *slot is null ? 0 : (nint)(*slot)->D3D11Texture2D;
    }

    private static unsafe nint GetMaterialTextureSlot(uint key)
    {
        var player = DalamudServices.ObjectTable.LocalPlayer;
        if (player is null || !DalamudServices.PlayerState.IsLoaded) return 0;
        var kind = key >> 24;
        if (kind is < 1 or > 3) return 0;
        var character = (Character*)player.Address;
        var draw = kind == 1 ? character->DrawObject : character->DrawData.WeaponData[(int)kind - 2].DrawData.DrawObject;
        if (draw is null) return 0;
        var model = (CharacterBase*)draw;
        var slot = (int)((key >> 16) & 255);
        var material = (int)((key >> 8) & 255);
        if (model->SlotCount is <= 0 or > 32 || slot >= model->SlotCount || material >= CharacterBase.MaterialsPerSlot
            || model->ColorTableTextures is null) return 0;
        return (nint)(model->ColorTableTextures + slot * CharacterBase.MaterialsPerSlot + material);
    }
}
