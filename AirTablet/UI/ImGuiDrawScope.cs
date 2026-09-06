using Dalamud.Bindings.ImGui;

namespace AirTablet.UI;

// Restores only state entered after this scope. This is a final safety net for
// bundled-app exceptions; it never resets the user's or another plugin's theme.
internal sealed unsafe class ImGuiDrawScope : IDisposable
{
    private readonly ImGuiContextPtr context = ImGui.GetCurrentContext();
    private readonly ImGuiStyle style;
    private readonly int colors;
    private readonly int variables;
    private readonly int windows;

    public ImGuiDrawScope()
    {
        style = *ImGui.GetStyle().Handle;
        colors = context.ColorStack.Size;
        variables = context.StyleVarStack.Size;
        windows = context.CurrentWindowStack.Size;
    }

    public void Dispose()
    {
        while (context.CurrentWindowStack.Size > windows)
        {
            ImGuiP.ErrorCheckEndWindowRecover(null!);
            var flags = context.CurrentWindow.Flags;
            if ((flags & ImGuiWindowFlags.ChildWindow) != 0) ImGui.EndChild();
            else if ((flags & ImGuiWindowFlags.Popup) != 0) ImGui.EndPopup();
            else ImGui.End();
        }
        if (context.StyleVarStack.Size > variables) ImGui.PopStyleVar(context.StyleVarStack.Size - variables);
        if (context.ColorStack.Size > colors) ImGui.PopStyleColor(context.ColorStack.Size - colors);
        *ImGui.GetStyle().Handle = style;
    }
}
