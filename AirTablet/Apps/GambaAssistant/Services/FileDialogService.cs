using System.Threading;
using System.Windows.Forms;

namespace GambaAssistant.Services;

internal static class FileDialogService
{
    public static string? PickJsonToOpen(string initialDirectory, string title) => RunDialog(() =>
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            InitialDirectory = SafeDirectory(initialDirectory),
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    });

    public static string? PickJsonToSave(string initialDirectory, string defaultFileName, string title) => RunDialog(() =>
    {
        using var dialog = new SaveFileDialog
        {
            Title = title,
            InitialDirectory = SafeDirectory(initialDirectory),
            FileName = defaultFileName,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = "json",
            OverwritePrompt = true,
            CheckPathExists = true,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    });

    private static string SafeDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    private static string? RunDialog(Func<string?> showDialog)
    {
        string? result = null;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { result = showDialog(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
            DalamudServices.Log.Warning(exception, "GambaAssistant file dialog failed.");
        return result;
    }
}
