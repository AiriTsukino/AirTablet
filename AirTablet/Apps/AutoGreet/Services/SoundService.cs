using System.Runtime.InteropServices;
using System.Reflection;

namespace AutoGreet.Services;

public sealed class SoundService : IDisposable
{
    private const string Alias = "AutoGreetDoorbell";
    private readonly Configuration config;
    private readonly object gate = new();
    private string? extractedDefaultSoundPath;

    public SoundService(Configuration config)
    {
        this.config = config;
    }

    public string DefaultSoundPath => EnsureDefaultSoundFile();

    public string EffectiveSoundPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(config.CustomDoorbellSoundPath) && File.Exists(config.CustomDoorbellSoundPath))
                return config.CustomDoorbellSoundPath;

            return DefaultSoundPath;
        }
    }

    public string LastSoundStatus { get; private set; } = "Sound not played yet.";

    public void PlayDoorbell()
    {
        if (!config.DoorbellSoundEnabled) return;

        try
        {
            var path = EffectiveSoundPath;
            if (!File.Exists(path))
            {
                LastSoundStatus = $"Sound file was not found: {path}";
                return;
            }

            lock (gate)
            {
                Send($"stop {Alias}");
                Send($"close {Alias}");

                // Let MCI infer the file type. This is more reliable for user MP3/WAV files than forcing mpegvideo.
                var openResult = Send($"open \"{path}\" alias {Alias}");
                if (openResult != 0)
                {
                    LastSoundStatus = $"MCI open failed ({openResult}): {path}";
                    return;
                }

                var volume = Math.Clamp((int)MathF.Round(config.DoorbellVolume * 1000f), 0, 1000);
                var volumeResult = Send($"setaudio {Alias} volume to {volume}");
                var playResult = Send($"play {Alias} from 0");
                LastSoundStatus = playResult == 0
                    ? $"Playing {Path.GetFileName(path)} at {config.DoorbellVolume:P0}. Volume result: {volumeResult}."
                    : $"MCI play failed ({playResult}): {path}";
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
            Send($"stop {Alias}");
            Send($"close {Alias}");
        }
    }

    private string EnsureDefaultSoundFile()
    {
        if (!string.IsNullOrWhiteSpace(extractedDefaultSoundPath) && File.Exists(extractedDefaultSoundPath))
            return extractedDefaultSoundPath;

        // Prefer a bundled file next to the plugin, if Dalamud.NET.Sdk copied it there.
        var assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? AppContext.BaseDirectory;
        var bundled = Path.Combine(assemblyDir, "Resources", "default-doorbell.mp3");
        if (File.Exists(bundled))
        {
            extractedDefaultSoundPath = bundled;
            return bundled;
        }

        // Fall back to extracting the embedded resource. The extracted filename includes the module MVID so
        // replacing Resources/default-doorbell.mp3 and rebuilding cannot keep reusing an older temp copy.
        var targetDir = Path.Combine(Path.GetTempPath(), "AutoGreet");
        Directory.CreateDirectory(targetDir);

        var assembly = typeof(Plugin).Assembly;
        var mvid = typeof(Plugin).Module.ModuleVersionId.ToString("N");
        var target = Path.Combine(targetDir, $"default-doorbell-{mvid}.mp3");
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Resources.default-doorbell.mp3", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
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
