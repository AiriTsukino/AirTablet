using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace WardrobeManager;

internal sealed class NativeImageDialog : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<Action> completed = [];
    private bool disposed;
    public bool IsOpen { get; private set; }

    public void Pump()
    {
        while (true)
        {
            Action? action;
            lock (gate) { if (completed.Count == 0) return; action = completed.Dequeue(); }
            action();
        }
    }

    public void Pick(Action<string> selected)
        => StartPicker(selected, PickWindows, PickLinux, "image");

    public void PickFolder(Action<string> selected)
        => StartPicker(selected, PickWindowsFolder, PickLinuxFolder, "folder");

    private void StartPicker(Action<string> selected, Func<string?> windows, Func<string?> linux, string kind)
    {
        lock (gate) { if (disposed || IsOpen) return; IsOpen = true; }
        var thread = new Thread(() =>
        {
            string? path = null;
            try { path = OperatingSystem.IsWindows() ? windows() : linux(); }
            catch (Exception ex) { DalamudServices.Log.Warning(ex, "WardrobeManager native {Kind} picker failed.", kind); }
            lock (gate)
            {
                IsOpen = false;
                if (!disposed && !string.IsNullOrWhiteSpace(path)) completed.Enqueue(() => selected(path));
            }
        }) { IsBackground = true, Name = "WardrobeManager native picker" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static string? PickWindows()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose WardrobeManager portrait",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }

    private static string? PickLinux()
    {
        foreach (var (program, arguments) in new (string Program, string[] Arguments)[]
        {
            ("zenity", ["--file-selection", "--title=Choose WardrobeManager portrait", "--file-filter=Images | *.png *.jpg *.jpeg *.webp *.bmp"]),
            ("kdialog", ["--getopenfilename", ".", "Images (*.png *.jpg *.jpeg *.webp *.bmp)"]),
        })
        {
            try
            {
                var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start);
                if (process is null) continue;
                var path = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && File.Exists(path)) return path;
            }
            catch { }
        }
        throw new InvalidOperationException("No supported Linux file picker was found (zenity or kdialog).");
    }

    private static string? PickWindowsFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where WardrobeManager selfies are saved",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static string? PickLinuxFolder()
    {
        foreach (var (program, arguments) in new (string Program, string[] Arguments)[]
        {
            ("zenity", ["--file-selection", "--directory", "--title=Choose WardrobeManager selfie folder"]),
            ("kdialog", ["--getexistingdirectory", "."]),
        })
        {
            try
            {
                var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start);
                if (process is null) continue;
                var path = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && Directory.Exists(path)) return path;
            }
            catch { }
        }
        throw new InvalidOperationException("No supported Linux folder picker was found (zenity or kdialog).");
    }

    public void Dispose() { lock (gate) { disposed = true; completed.Clear(); } }
}
