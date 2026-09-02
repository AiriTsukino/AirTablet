using Newtonsoft.Json.Linq;

namespace AirTablet.Services;

internal sealed class UpdateCheckService : IDisposable
{
    private const string ManifestUrl =
        "https://raw.githubusercontent.com/AiriTsukino/AirTablet/main/pluginmaster.json";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly object stateLock = new();
    private readonly Version installedVersion =
        typeof(Plugin).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    private readonly CancellationTokenSource cancellation = new();
    private DateTime nextCheckUtc = DateTime.MinValue;
    private Version? latestVersion;
    private string lastError = string.Empty;
    private bool checking;

    public UpdateCheckService()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AirTablet-UpdateChecker/1.0");
        http.DefaultRequestHeaders.CacheControl =
            new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
    }

    public Version InstalledVersion => installedVersion;

    public Version? LatestVersion
    {
        get { lock (stateLock) return latestVersion; }
    }

    public bool IsUpdateAvailable
    {
        get
        {
            lock (stateLock)
                return latestVersion is not null && latestVersion > installedVersion;
        }
    }

    public bool IsChecking
    {
        get { lock (stateLock) return checking; }
    }

    public string LastError
    {
        get { lock (stateLock) return lastError; }
    }

    public void Tick()
    {
        lock (stateLock)
        {
            if (checking || DateTime.UtcNow < nextCheckUtc)
                return;
            checking = true;
            // Schedule the next normal check immediately so repeated failures do
            // not create a request loop every rendered frame.
            nextCheckUtc = DateTime.UtcNow + CheckInterval;
        }

        _ = CheckAsync(cancellation.Token);
    }

    public void CheckNow()
    {
        lock (stateLock)
            nextCheckUtc = DateTime.MinValue;
        Tick();
    }

    private async Task CheckAsync(CancellationToken token)
    {
        try
        {
            // The query value prevents GitHub's raw-content edge cache from
            // returning an older manifest after a release has just been pushed.
            var requestUrl = $"{ManifestUrl}?airtablet-check={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var json = await http.GetStringAsync(requestUrl, token).ConfigureAwait(false);
            var manifest = JArray.Parse(json)
                .OfType<JObject>()
                .FirstOrDefault(item => string.Equals(
                    item.Value<string>("InternalName"),
                    "AirTablet",
                    StringComparison.OrdinalIgnoreCase));
            var versionText = manifest?.Value<string>("AssemblyVersion");
            if (!Version.TryParse(versionText, out var remoteVersion))
                throw new InvalidDataException("AirTablet's remote manifest did not contain a valid AssemblyVersion.");

            lock (stateLock)
            {
                latestVersion = remoteVersion;
                lastError = string.Empty;
            }
            DalamudServices.Log.Debug(
                "AirTablet update check completed. Installed={Installed}; remote={Remote}; update={Update}.",
                installedVersion,
                remoteVersion,
                remoteVersion > installedVersion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (stateLock)
                lastError = ex.Message;
            DalamudServices.Log.Warning(ex, "AirTablet could not check its remote AssemblyVersion.");
        }
        finally
        {
            lock (stateLock)
                checking = false;
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        http.Dispose();
    }
}
