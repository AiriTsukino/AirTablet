using Dalamud.Interface.ImGuiFileDialog;

namespace ShiftKeeper.Services;

public sealed class FileDialogService : IDisposable
{
    private readonly FileDialogManager manager = new();
    private static string StartPath => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public bool IsOpen { get; private set; }

    public void Pump() => manager.Draw();

    public void SaveJson(string suggestedName, Action<string> success, Action<string> error)
    {
        if (IsOpen) return;
        IsOpen = true;
        try
        {
            manager.SaveFileDialog(
                "Export ShiftKeeper venue profile",
                ".json",
                suggestedName,
                ".json",
                (selected, path) => Complete(selected, path, success, error),
                StartPath,
                true);
        }
        catch (Exception ex)
        {
            IsOpen = false;
            error(ex.Message);
        }
    }

    public void OpenJson(Action<string> success, Action<string> error)
    {
        if (IsOpen) return;
        IsOpen = true;
        try
        {
            manager.OpenFileDialog(
                "Import ShiftKeeper venue profile",
                ".json",
                (bool selected, List<string> paths) => Complete(selected, paths.FirstOrDefault() ?? string.Empty, success, error),
                1,
                StartPath,
                true);
        }
        catch (Exception ex)
        {
            IsOpen = false;
            error(ex.Message);
        }
    }

    private void Complete(bool selected, string path, Action<string> success, Action<string> error)
    {
        IsOpen = false;
        if (selected && !string.IsNullOrWhiteSpace(path))
            success(path);
        else
            error("File selection cancelled.");
    }

    public void Dispose()
    {
        manager.Reset();
        IsOpen = false;
    }
}
