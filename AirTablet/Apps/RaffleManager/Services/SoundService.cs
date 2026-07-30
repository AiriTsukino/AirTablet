using System.Runtime.InteropServices;
using RaffleManager.Models;

namespace RaffleManager.Services;

internal sealed class SoundService : IDisposable
{
    private const int AliasCount = 6;
    private static readonly string[] Aliases = Enumerable.Range(0, AliasCount)
        .Select(i => $"RaffleManagerTick{i}")
        .ToArray();

    private readonly Configuration config;
    private readonly object gate = new();
    private string? extractedDefaultSoundPath;
    private string? openedPath;
    private int nextAlias;

    public SoundService(Configuration config)
    {
        this.config = config;
    }

    private VenueProfile Profile => config.Profile;

    public string LastSoundStatus { get; private set; } = "Sound not played yet.";

    public void Prepare()
    {
        if (!Profile.TickSoundEnabled || Profile.TickVolume <= 0f) return;

        try
        {
            var path = EnsureDefaultSoundFile();
            if (!File.Exists(path))
            {
                LastSoundStatus = $"Tick sound was not found: {path}";
                return;
            }

            lock (gate)
            {
                OpenPoolIfNeeded(path);
                ApplyVolumeToPool();
                foreach (var alias in Aliases)
                {
                    Send($"stop {alias}");
                    Send($"seek {alias} to start");
                }
            }
        }
        catch (Exception ex)
        {
            LastSoundStatus = ex.Message;
        }
    }

    public void PlayTick()
    {
        if (!Profile.TickSoundEnabled || Profile.TickVolume <= 0f) return;

        try
        {
            var path = EnsureDefaultSoundFile();
            if (!File.Exists(path))
            {
                LastSoundStatus = $"Tick sound was not found: {path}";
                return;
            }

            lock (gate)
            {
                OpenPoolIfNeeded(path);
                var alias = Aliases[nextAlias++ % Aliases.Length];
                ApplyVolume(alias);
                Send($"stop {alias}");
                Send($"seek {alias} to start");
                var playResult = Send($"play {alias}");
                LastSoundStatus = playResult == 0
                    ? $"Tick at {Profile.TickVolume:P0}."
                    : $"MCI play failed ({playResult}).";
            }
        }
        catch (Exception ex)
        {
            LastSoundStatus = ex.Message;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var alias in Aliases)
            {
                Send($"stop {alias}");
                Send($"close {alias}");
            }
            openedPath = null;
        }
    }

    private void OpenPoolIfNeeded(string path)
    {
        if (openedPath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
            return;

        foreach (var alias in Aliases)
        {
            Send($"stop {alias}");
            Send($"close {alias}");
        }

        var anyOpened = false;
        foreach (var alias in Aliases)
        {
            var openResult = Send($"open \"{path}\" type waveaudio alias {alias}");
            if (openResult == 0)
            {
                anyOpened = true;
                Send($"seek {alias} to start");
            }
        }

        openedPath = anyOpened ? path : null;
        if (!anyOpened)
            LastSoundStatus = $"MCI open failed for tick sound: {path}";
    }

    private void ApplyVolumeToPool()
    {
        foreach (var alias in Aliases)
            ApplyVolume(alias);
    }

    private void ApplyVolume(string alias)
    {
        var volume = Math.Clamp((int)MathF.Round(Profile.TickVolume * 1000f), 0, 1000);
        Send($"setaudio {alias} volume to {volume}");
    }

    private string EnsureDefaultSoundFile()
    {
        if (!string.IsNullOrWhiteSpace(extractedDefaultSoundPath) && File.Exists(extractedDefaultSoundPath))
            return extractedDefaultSoundPath;

        var assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? AppContext.BaseDirectory;
        var bundledWav = Path.Combine(assemblyDir, "Resources", "tick.wav");
        if (File.Exists(bundledWav))
        {
            extractedDefaultSoundPath = bundledWav;
            return bundledWav;
        }


        var targetDir = Path.Combine(Path.GetTempPath(), "RaffleManager");
        Directory.CreateDirectory(targetDir);
        var mvid = typeof(Plugin).Module.ModuleVersionId.ToString("N");
        var target = Path.Combine(targetDir, $"tick-{mvid}.wav");
        var resourceName = typeof(Plugin).Assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Resources.tick.wav", StringComparison.OrdinalIgnoreCase));
        if (resourceName is not null)
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                using var file = File.Create(target);
                stream.CopyTo(file);
            }
        }

        extractedDefaultSoundPath = target;
        return target;
    }

    private static int Send(string command) => mciSendString(command, null, 0, IntPtr.Zero);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, string? returnValue, int returnLength, IntPtr winHandle);
}
