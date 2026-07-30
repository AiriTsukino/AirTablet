using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class PendingEmoteQueueService : IDisposable
{
    private static readonly TimeSpan TargetedEmoteTargetHold = TimeSpan.FromSeconds(1.75);

    private sealed record PendingEmote(VisitorKey Target, string Command, string MacroName, DateTimeOffset QueuedUtc);

    private readonly Configuration config;
    private readonly ChatCommandService chatCommands;
    private readonly TargetingService targeting;
    private readonly EmoteResumeService emoteResume;
    private readonly DiagnosticLogService logs;
    private readonly object sync = new();
    private readonly List<PendingEmote> pending = [];
    private CancellationTokenSource cts = new();
    private Task? worker;

    public PendingEmoteQueueService(Configuration config, ChatCommandService chatCommands, TargetingService targeting, EmoteResumeService emoteResume, DiagnosticLogService logs)
    {
        this.config = config;
        this.chatCommands = chatCommands;
        this.targeting = targeting;
        this.emoteResume = emoteResume;
        this.logs = logs;
    }

    public int PendingCount
    {
        get { lock (sync) return pending.Count; }
    }

    public void Enqueue(VisitorKey target, string command, string macroName)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        lock (sync)
        {
            pending.Add(new PendingEmote(target, command.Trim(), macroName, DateTimeOffset.UtcNow));
            EnsureWorkerLocked();
        }

        logs.Info("Emote queued", $"Queued emote command for {target.Display} until they are targetable: {command}");
    }

    private void EnsureWorkerLocked()
    {
        if (worker is { IsCompleted: false }) return;
        cts.Dispose();
        cts = new CancellationTokenSource();
        worker = Task.Run(() => ProcessAsync(cts.Token));
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            PendingEmote[] snapshot;
            lock (sync) snapshot = pending.ToArray();

            if (snapshot.Length == 0)
                return;

            foreach (var item in snapshot)
            {
                token.ThrowIfCancellationRequested();

                bool targeted;
                try
                {
                    targeted = await targeting.TargetAndVerifyAsync(item.Target, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logs.Warning("Emote target check failed", $"Could not check target for queued emote on {item.Target.Display}: {ex.Message}");
                    targeted = false;
                }

                if (!targeted)
                    continue;

                await Task.Delay(200, token).ConfigureAwait(false);
                var sent = await chatCommands.SendAsync(item.Command, token).ConfigureAwait(false);
                if (sent)
                {
                    logs.Info("Queued emote sent", $"Sent queued emote for {item.Target.Display}: {item.Command}. Holding target briefly so the game can apply the emote to the selected player.");
                    await Task.Delay(TargetedEmoteTargetHold, token).ConfigureAwait(false);
                    Remove(item);

                    if (config.UntargetAfterGreeting)
                        await targeting.ClearTargetAsync(CancellationToken.None).ConfigureAwait(false);

                    await emoteResume.RequestResumeAfterQueueAsync(item.MacroName, item.Target, "queued targeted emote sent", CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    logs.Warning("Queued emote failed", $"Could not send queued emote for {item.Target.Display}: {chatCommands.LastError}");
                    Remove(item);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }

    private void Remove(PendingEmote item)
    {
        lock (sync)
            pending.RemoveAll(x => x.Target.ToString().Equals(item.Target.ToString(), StringComparison.OrdinalIgnoreCase)
                                   && x.Command.Equals(item.Command, StringComparison.OrdinalIgnoreCase)
                                   && x.QueuedUtc == item.QueuedUtc);
    }

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }
}
