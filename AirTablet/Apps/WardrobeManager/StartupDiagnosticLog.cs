using System.Diagnostics;
using System.Text;

namespace WardrobeManager;

// Temporary, tightly bounded startup trace for diagnosing reports where opening
// WardrobeManager stalls the game. The file is replaced on every app load and
// accepts each event key only once during the first ten seconds.
internal sealed class StartupDiagnosticLog : IDisposable
{
    private static readonly TimeSpan CaptureWindow = TimeSpan.FromSeconds(10);
    private const long MaximumBytes = 64 * 1024;
    private readonly object gate = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly HashSet<string> writtenKeys = new(StringComparer.Ordinal);
    private StreamWriter? writer;

    public string Path { get; }

    public StartupDiagnosticLog(DirectoryInfo configDirectory)
    {
        Path = System.IO.Path.Combine(configDirectory.FullName, "wardrobe-startup-diagnostics.log");
        OpenWriter("module-load");
    }

    public void BeginVisibleSession()
    {
        lock (gate)
        {
            try { writer?.Dispose(); }
            catch { }
            writer = null;
            writtenKeys.Clear();
            elapsed.Restart();
            OpenWriter("app-open");
        }
    }

    private void OpenWriter(string reason)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            WriteCore("session", $"WardrobeManager diagnostics started; reason={reason}; UTC={DateTime.UtcNow:O}; capture-window=10s; path={Path}");
        }
        catch
        {
            writer = null;
        }
    }

    public void Once(string key, string message)
    {
        lock (gate)
        {
            if (elapsed.Elapsed > CaptureWindow || !writtenKeys.Add(key)) return;
            WriteCore(key, message);
        }
    }

    public IDisposable MeasureOnce(string key, string operation)
    {
        lock (gate)
        {
            if (elapsed.Elapsed > CaptureWindow || !writtenKeys.Add(key)) return EmptyScope.Instance;
            WriteCore(key + ".begin", operation + " started");
            return new Measurement(this, key, operation, Stopwatch.GetTimestamp());
        }
    }

    private void Complete(string key, string operation, long started)
    {
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (gate)
        {
            if (elapsed.Elapsed > CaptureWindow) return;
            WriteCore(key + ".end", $"{operation} completed in {duration:F2} ms");
        }
    }

    private void WriteCore(string key, string message)
    {
        try
        {
            if (writer is null || writer.BaseStream.Length >= MaximumBytes) return;
            writer.WriteLine($"[{elapsed.Elapsed.TotalMilliseconds,8:F1} ms] {key}: {message.Replace('\r', ' ').Replace('\n', ' ')}");
        }
        catch
        {
            // Diagnostics must never be able to affect the app being diagnosed.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { writer?.Dispose(); }
            catch { }
        }
    }

    private sealed class Measurement(StartupDiagnosticLog owner, string key, string operation, long started) : IDisposable
    {
        private bool completed;
        public void Dispose()
        {
            if (completed) return;
            completed = true;
            owner.Complete(key, operation, started);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
