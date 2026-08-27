using System.Threading;
using System.Windows.Forms;

namespace PartyRefresh;

internal sealed class FileDialogService : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<Action> completedActions = new();
    private bool disposed;

    public bool DialogOpen { get; private set; }

    public void Draw()
    {
        while (true)
        {
            Action? completed;
            lock (gate)
            {
                if (completedActions.Count == 0)
                    return;
                completed = completedActions.Dequeue();
            }
            completed();
        }
    }

    public void Export(string suggestedName, Action<string> selected)
    {
        StartDialog(() =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Export PartyRefresh venue profile",
                Filter = "PartyRefresh venue profile (*.json)|*.json|All files (*.*)|*.*",
                FileName = SanitizeFileName(suggestedName),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AddExtension = true,
                DefaultExt = "json",
                OverwritePrompt = true,
                CheckPathExists = true,
                RestoreDirectory = true,
            };
            return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
        }, selected, "PartyRefresh export file picker failed.");
    }

    public void Import(Action<string> selected)
    {
        StartDialog(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Import PartyRefresh venue profile",
                Filter = "PartyRefresh and JSON profiles (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                RestoreDirectory = true,
            };
            return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
        }, selected, "PartyRefresh import file picker failed.");
    }

    private void StartDialog(Func<string?> showDialog, Action<string> selected, string logMessage)
    {
        lock (gate)
        {
            if (disposed || DialogOpen)
                return;
            DialogOpen = true;
        }

        var thread = new Thread(() =>
        {
            string? path = null;
            try
            {
                path = showDialog();
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(ex, logMessage);
            }
            finally
            {
                lock (gate)
                {
                    DialogOpen = false;
                    if (!disposed && !string.IsNullOrWhiteSpace(path))
                        completedActions.Enqueue(() => selected(path));
                }
            }
        })
        {
            IsBackground = true,
            Name = "PartyRefresh file picker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static string SanitizeFileName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "PartyRefresh-profile.json" : name.Trim();
        foreach (var character in Path.GetInvalidFileNameChars())
            name = name.Replace(character, '-');
        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json";
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            completedActions.Clear();
        }
    }
}
