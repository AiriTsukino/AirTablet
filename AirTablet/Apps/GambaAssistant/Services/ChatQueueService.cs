using System.Collections.Concurrent;
using Dalamud.Plugin.Services;

namespace GambaAssistant.Services;

public sealed class ChatQueueService : IDisposable
{
    private readonly Configuration config;
    private readonly LogService log;
    private readonly ConcurrentQueue<ChatQueueItem> queue = new();
    private readonly NativeChatSender nativeChat;
    private DateTimeOffset nextSend = DateTimeOffset.MinValue;
    private DateTimeOffset nextActionCommandDelayLog = DateTimeOffset.MinValue;
    private Func<bool>? shouldDelayActionCommands;
    private long nextSequence;
    public bool Paused { get; private set; }
    public int Count => queue.Count;
    public string Status => Paused ? $"Paused ({Count} queued)" : Count == 0 ? "Idle" : $"Queued: {Count}";
    public bool DemoMode { get; set; }
    public float EffectiveDelaySeconds => Math.Max(0.2f, config.General.ChatQueueDelaySeconds);
    public float DeathRollDelaySeconds => Math.Max(0.2f, config.DeathRoll.ChatBroadcastDelaySeconds);
    public ChatQueueService(Configuration config, LogService log)
    {
        this.config = config;
        this.log = log;
        nativeChat = new NativeChatSender(log);
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    public void EnqueueParty(string message) => Enqueue($"/p {message}", false, EffectiveDelaySeconds, true);
    public void EnqueueBlackjackToChannel(string channel, string message)
    {
        var normalizedChannel = NormalizeChatChannel(channel);
        Enqueue($"{normalizedChannel} {message}", false, EffectiveDelaySeconds, true);
        log.Add(LogCategory.ChatQueue, $"Blackjack broadcast queued ({normalizedChannel}): {message}");
    }

    public void EnqueueDeathRoll(string message)
    {
        if (config.DeathRoll.DisableChatBroadcasts)
        {
            log.Add(LogCategory.ChatQueue, $"DRT broadcast suppressed: {message}");
            return;
        }

        var channel = NormalizeChatChannel(config.DeathRoll.ChatChannel);
        Enqueue($"{channel} {message}", false, DeathRollDelaySeconds, true);
    }
    public void EnqueueCommand(string command, bool diceCommand = false) => EnqueueCommand(command, diceCommand, EffectiveDelaySeconds);
    public void EnqueueCommand(string command, bool diceCommand, float delaySeconds) => Enqueue(command, diceCommand, Math.Max(0.2f, delaySeconds), true);
    public void SetActionCommandDelayPredicate(Func<bool>? predicate) => shouldDelayActionCommands = predicate;
    public void EnqueueDeathRollCommand(string command, bool diceCommand = false)
    {
        if (config.DeathRoll.DisableChatBroadcasts)
        {
            log.Add(LogCategory.ChatQueue, $"DRT command suppressed: {command}");
            return;
        }

        Enqueue(command, diceCommand, DeathRollDelaySeconds, true);
    }

    private static string NormalizeChatChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "/say" or "say" or "/s" or "s" => "/say",
        "/shout" or "shout" or "/sh" or "sh" => "/shout",
        "/yell" or "yell" or "/y" or "y" => "/yell",
        "/party" or "party" or "/p" or "p" => "/party",
        _ => "/party",
    };
    public void Pause() { Paused = true; log.Add(LogCategory.ChatQueue, "Chat/dice queue paused by dealer."); }
    public void Resume() { Paused = false; log.Add(LogCategory.ChatQueue, "Chat/dice queue resumed by dealer."); }
    public void PanicClear() { Paused = true; while(queue.TryDequeue(out _)){} log.Add(LogCategory.Warnings, "Panic stop: chat/dice queue cleared and paused."); }

    public long CreateCheckpoint() => Interlocked.Read(ref nextSequence);

    public void RemoveQueuedAfter(long checkpoint, string reason)
    {
        var retained = new List<ChatQueueItem>();
        var removed = 0;
        while (queue.TryDequeue(out var item))
        {
            if (item.Sequence <= checkpoint)
                retained.Add(item);
            else
                removed++;
        }

        foreach (var item in retained)
            queue.Enqueue(item);

        if (removed > 0)
            log.Add(LogCategory.ChatQueue, $"Removed {removed} queued command(s) after {reason}.");
    }

    private void Enqueue(string command, bool diceCommand, float delaySeconds, bool delayDuringTrade)
    {
        var sequence = Interlocked.Increment(ref nextSequence);
        queue.Enqueue(new ChatQueueItem(command, diceCommand, delaySeconds, delayDuringTrade, sequence));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTimeOffset.Now;
        if (Paused || queue.IsEmpty || now < nextSend) return;
        if (!queue.TryPeek(out var pending)) return;

        if (ShouldDelayActionCommand(pending))
        {
            nextSend = now.AddMilliseconds(500);
            if (now >= nextActionCommandDelayLog)
            {
                nextActionCommandDelayLog = now.AddSeconds(10);
                log.Add(LogCategory.ChatQueue, "Delayed queued chat/command while a verified trade sequence is active.");
            }
            return;
        }

        if (!queue.TryDequeue(out var item)) return;
        if (DemoMode)
        {
            log.Add(LogCategory.Demo, $"Demo would send: {item.Command}");
        }
        else
        {
            var sent = nativeChat.TrySend(item.Command);
            if (sent)
                log.Add(LogCategory.ChatQueue, $"Sent command: {item.Command}");
            else
            {
                log.Add(LogCategory.Warnings, $"Unable to send command automatically: {item.Command}");
            }
        }
        nextSend = DateTimeOffset.Now.AddSeconds(Math.Max(0.2f, item.DelaySeconds));
    }

    private bool ShouldDelayActionCommand(ChatQueueItem item)
    {
        return item.DelayDuringTrade && shouldDelayActionCommands?.Invoke() == true;
    }

    public void Dispose() => DalamudServices.Framework.Update -= OnFrameworkUpdate;
}

public sealed record ChatQueueItem(string Command, bool IsDiceCommand, float DelaySeconds, bool DelayDuringTrade, long Sequence);
