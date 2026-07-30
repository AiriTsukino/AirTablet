using System.Text.Json;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Games.DeathRoll;

namespace GambaAssistant.Services;

public sealed class PersistenceService : IDisposable
{
    private const string BlackjackSessionPath = "Blackjack/session.json";
    private const string DeathRollSessionPath = "DeathRoll/session.json";
    private readonly Configuration config;
    private readonly string root;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private readonly object fileWriteLock = new();
    public string ConfigRoot => root;

    public PersistenceService(Configuration config)
    {
        this.config = config;
        root = DalamudServices.PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Blackjack"));
        Directory.CreateDirectory(Path.Combine(root, "DeathRoll"));
        Directory.CreateDirectory(Path.Combine(root, "Profiles"));
        Directory.CreateDirectory(Path.Combine(root, "Templates"));
        Directory.CreateDirectory(Path.Combine(root, "Exports"));
    }

    public void SaveNow() => DalamudServices.PluginInterface.SavePluginConfig(config);

    public void SaveBlackjackSession(BlackjackSession session)
    {
        session.NormalizeForSave();
        SaveJson(BlackjackSessionPath, session);
    }

    public BlackjackSession? LoadBlackjackSession()
    {
        var session = LoadJson<BlackjackSession>(BlackjackSessionPath);
        session?.NormalizeAfterLoad();
        return session;
    }

    public void SaveDeathRollTournament(DeathRollTournament tournament)
    {
        tournament.NormalizeForSave();
        SaveJson(DeathRollSessionPath, tournament);
    }

    public DeathRollTournament? LoadDeathRollTournament()
    {
        var tournament = LoadJson<DeathRollTournament>(DeathRollSessionPath);
        tournament?.NormalizeAfterLoad();
        return tournament;
    }

    public void SaveJson<T>(string relativePath, T value)
    {
        var path = Path.Combine(root, relativePath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(value, jsonOptions);
        lock (fileWriteLock)
        {
            SaveJsonAtomic(path, directory, json);
        }
    }

    private static void SaveJsonAtomic(string path, string directory, string json)
    {
        IOException? lastIoException = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                lastIoException = ex;
                TryDeleteFile(tempPath);
                Thread.Sleep(35 + attempt * 35);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        throw lastIoException ?? new IOException($"Could not save {path}.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only. A future save uses a unique temp path.
        }
    }

    public T? LoadJson<T>(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        if (!File.Exists(path)) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), jsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public void Dispose() => SaveNow();
}
