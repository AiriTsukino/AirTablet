using Dalamud.Bindings.ImGui;
namespace GambaAssistant.UI.Components;
internal static class MoneyDisplay { public static void Draw(string label, long amount) => ImGui.Text($"{label}: {amount:N0} gil"); }
