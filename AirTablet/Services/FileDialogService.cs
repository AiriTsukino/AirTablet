using Dalamud.Interface.ImGuiFileDialog;

namespace AirTablet.Services;

internal sealed class FileDialogService : IDisposable
{
    private readonly FileDialogManager manager = new();

    public bool IsOpen { get; private set; }

    public void Draw() => manager.Draw();

    public void PickWallpaper(Action<string> selected)
    {
        if (IsOpen)
            return;

        IsOpen = true;
        manager.OpenFileDialog(
            "Choose an AirTablet wallpaper",
            ".png,.jpg,.jpeg,.webp",
            (success, paths) =>
            {
                IsOpen = false;
                var path = paths.FirstOrDefault();
                if (success && !string.IsNullOrWhiteSpace(path))
                    selected(path);
            },
            1,
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            true);
    }

    public void Dispose()
    {
        manager.Reset();
        IsOpen = false;
    }
}
