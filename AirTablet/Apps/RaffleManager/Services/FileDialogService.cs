using System.Threading;
using System.Windows.Forms;

namespace RaffleManager.Services;

internal static class FileDialogService
{
    private static readonly object Gate = new();
    private static string? pickedImagePath;
    private static string? pickedProfileImportPath;
    private static string? pickedProfileExportPath;

    public static bool DialogOpen { get; private set; }

    public static void PickImageToOpenAsync(string initialDirectory, string title)
    {
        StartDialog(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = title,
                InitialDirectory = SafeDirectory(initialDirectory),
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
            };

            var result = dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            lock (Gate) pickedImagePath = result;
        }, "RaffleManager image picker failed.");
    }

    public static void PickProfileToImportAsync(string initialDirectory)
    {
        StartDialog(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Import venue profile",
                InitialDirectory = SafeDirectory(initialDirectory),
                Filter = "RaffleManager profile (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
            };

            var result = dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            lock (Gate) pickedProfileImportPath = result;
        }, "RaffleManager profile import picker failed.");
    }

    public static void PickProfileExportPathAsync(string initialDirectory, string profileName)
    {
        StartDialog(() =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Export venue profile",
                InitialDirectory = SafeDirectory(initialDirectory),
                FileName = $"RaffleManager-{SafeFileName(profileName)}.json",
                Filter = "RaffleManager profile (*.json)|*.json|All files (*.*)|*.*",
                AddExtension = true,
                DefaultExt = "json",
                OverwritePrompt = true,
                CheckPathExists = true,
            };

            var result = dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            lock (Gate) pickedProfileExportPath = result;
        }, "RaffleManager profile export picker failed.");
    }

    public static string? ConsumePickedImagePath()
    {
        lock (Gate)
        {
            var result = pickedImagePath;
            pickedImagePath = null;
            return result;
        }
    }

    public static string? ConsumeProfileImportPath()
    {
        lock (Gate)
        {
            var result = pickedProfileImportPath;
            pickedProfileImportPath = null;
            return result;
        }
    }

    public static string? ConsumeProfileExportPath()
    {
        lock (Gate)
        {
            var result = pickedProfileExportPath;
            pickedProfileExportPath = null;
            return result;
        }
    }

    private static void StartDialog(Action action, string logMessage)
    {
        lock (Gate)
        {
            if (DialogOpen) return;
            DialogOpen = true;
        }

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(ex, logMessage);
            }
            finally
            {
                lock (Gate) DialogOpen = false;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (File.Exists(path))
                path = Path.GetDirectoryName(path) ?? string.Empty;

            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    private static string SafeFileName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Default" : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name;
    }
}
