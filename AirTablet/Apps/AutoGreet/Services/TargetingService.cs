using AutoGreet.Models;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoGreet.Services;

public sealed class TargetingService
{
    public string LastTargetStatus { get; private set; } = "No targeting attempted yet.";

    public async Task<bool> TargetAsync(VisitorKey target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await DalamudServices.Framework.RunOnFrameworkThread(() => TargetOnFrameworkThread(target, logMissing: true)).ConfigureAwait(false);
    }

    public async Task<bool> TargetAndVerifyAsync(VisitorKey target, CancellationToken token)
    {
        const int maxAttempts = 8;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            var targeted = await DalamudServices.Framework.RunOnFrameworkThread(() => TargetOnFrameworkThread(target, logMissing: attempt == 1)).ConfigureAwait(false);
            if (targeted)
            {
                await Task.Delay(250, token).ConfigureAwait(false);

                var verified = await IsTargetingAsync(target, token).ConfigureAwait(false);
                if (verified)
                {
                    LastTargetStatus = $"Target verified for {target.Display}.";
                    return true;
                }
            }

            LastTargetStatus = $"Waiting for target lock on {target.Display} before emote... ({attempt}/{maxAttempts})";
            await Task.Delay(250, token).ConfigureAwait(false);
        }

        LastTargetStatus = $"Could not verify target lock for {target.Display} before emote.";
        DalamudServices.Log.Warning("AutoGreet could not verify target lock for {Visitor} before sending a targeted emote.", target.Display);
        return false;
    }

    public async Task<bool> IsTargetingAsync(VisitorKey target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await DalamudServices.Framework.RunOnFrameworkThread(() => IsCurrentTarget(target)).ConfigureAwait(false);
    }

    public async Task ClearTargetAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await DalamudServices.Framework.RunOnFrameworkThread(() =>
        {
            DalamudServices.TargetManager.Target = null;
            LastTargetStatus = "Target cleared.";
            return true;
        }).ConfigureAwait(false);
    }

    public async Task<bool> WaitForTargetAsync(VisitorKey target, Func<bool> stillPresent, float timeoutSeconds, CancellationToken token)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0.5f, 30f));
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;

        while (DateTime.UtcNow <= deadline)
        {
            token.ThrowIfCancellationRequested();

            if (!stillPresent())
            {
                LastTargetStatus = $"Stopped waiting for {target.Display}; they left before the emote target was visible.";
                return false;
            }

            attempt++;
            var targeted = await DalamudServices.Framework.RunOnFrameworkThread(() => TargetOnFrameworkThread(target, logMissing: false)).ConfigureAwait(false);
            if (targeted)
                return true;

            LastTargetStatus = $"Waiting for {target.Display} to load before emote target... ({attempt})";
            await Task.Delay(250, token).ConfigureAwait(false);
        }

        LastTargetStatus = $"Timed out waiting for {target.Display} to load before emote target.";
        DalamudServices.Log.Warning("AutoGreet timed out waiting for {Visitor} to become targetable for an emote.", target.Display);
        return false;
    }

    private bool TargetOnFrameworkThread(VisitorKey target, bool logMissing)
    {
        var obj = FindPlayerObject(target);
        if (obj is null)
        {
            LastTargetStatus = $"Could not find visible player actor for {target.Display}.";
            if (logMissing)
                DalamudServices.Log.Warning("AutoGreet could not target {Visitor}: no matching player object found.", target.Display);
            return false;
        }

        if (IsCurrentTarget(target))
        {
            LastTargetStatus = $"Already targeting {target.Display}.";
            return true;
        }

        DalamudServices.TargetManager.Target = obj;
        LastTargetStatus = $"Targeted {target.Display}.";
        return true;
    }

    private static bool IsCurrentTarget(VisitorKey target)
    {
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter pc) return false;

        var name = pc.Name.ToString();
        if (!name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) return false;

        string world;
        try { world = pc.HomeWorld.Value.Name.ToString(); }
        catch { world = string.Empty; }

        return world.Equals(target.World, StringComparison.OrdinalIgnoreCase);
    }

    public static IGameObject? FindPlayerObject(VisitorKey target)
    {
        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            var name = pc.Name.ToString();
            if (!name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) continue;

            string world;
            try { world = pc.HomeWorld.Value.Name.ToString(); }
            catch { world = string.Empty; }

            if (!world.Equals(target.World, StringComparison.OrdinalIgnoreCase)) continue;
            return pc;
        }

        return null;
    }
}
