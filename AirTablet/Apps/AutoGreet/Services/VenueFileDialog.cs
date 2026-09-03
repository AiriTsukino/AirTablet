using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace AutoGreet.Services;

internal sealed class VenueFileDialog : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<Action> completed = new();
    private bool disposed;
    public bool IsOpen { get; private set; }

    public void Pump()
    {
        while (true)
        {
            Action action;
            lock (gate)
            {
                if (completed.Count == 0) return;
                action = completed.Dequeue();
            }
            action();
        }
    }

    public void Pick(bool save, string name, Action<string> selected, Action<string> failed)
    {
        lock (gate) { if (disposed || IsOpen) return; IsOpen = true; }
        var thread = new Thread(() =>
        {
            string? path = null;
            string? error = null;
            try { path = OperatingSystem.IsWindows() ? Windows(save, name) : Linux(save, name); }
            catch (Exception ex) { error = ex.Message; }
            lock (gate)
            {
                IsOpen = false;
                if (disposed) return;
                if (error is not null) completed.Enqueue(() => failed(error));
                else if (!string.IsNullOrWhiteSpace(path)) completed.Enqueue(() => selected(path));
            }
        }) { IsBackground = true, Name = "AutoGreet venue file picker" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static string FileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '-');
        return (string.IsNullOrWhiteSpace(name) ? "AutoGreet-venue" : name) + ".json";
    }

    private static string? Windows(bool save, string name)
    {
        using FileDialog dialog = save
            ? new SaveFileDialog { FileName = FileName(name), AddExtension = true, DefaultExt = "json", OverwritePrompt = true }
            : new OpenFileDialog { CheckFileExists = true, Multiselect = false };
        dialog.Title = save ? "Export AutoGreet venue profile" : "Import AutoGreet venue profile";
        dialog.Filter = "AutoGreet venue profiles (*.json)|*.json|All files (*.*)|*.*";
        dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        dialog.CheckPathExists = true;
        dialog.RestoreDirectory = true;
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }

    private static string? Linux(bool save, string name)
    {
        var title = save ? "Export AutoGreet venue profile" : "Import AutoGreet venue profile";
        foreach (var program in new[] { "zenity", "kdialog" })
        {
            var start = new ProcessStartInfo(program)
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var initial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), save ? FileName(name) : string.Empty);
            var args = program == "zenity"
                ? new List<string> { "--file-selection", $"--title={title}", "--file-filter=JSON profiles | *.json", $"--filename={initial}" }
                : new List<string> { save ? "--getsavefilename" : "--getopenfilename", initial, "JSON profiles (*.json)", "--title", title };
            if (program == "zenity" && save) args.AddRange(["--save", "--confirm-overwrite"]);
            foreach (var argument in args) start.ArgumentList.Add(argument);
            Process? process;
            try { process = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { continue; }
            if (process is null) continue;
            using (process)
            {
                var path = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 1) return null; // Cancel is not a reason to open another picker.
                if (process.ExitCode != 0) continue;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }
        throw new InvalidOperationException("No supported Linux file picker is available. Install zenity or kdialog.");
    }

    public void Dispose() { lock (gate) { disposed = true; completed.Clear(); } }
}
