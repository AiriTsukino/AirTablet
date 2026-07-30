using GambaAssistant.Games.Blackjack;

namespace GambaAssistant.Services;

public sealed class DiceService : IDisposable
{
    private readonly BlackjackSession session;
    private readonly ChatQueueService chat;
    private readonly LogService log;
    private readonly Queue<PendingDiceRoll> queuedRolls = new();
    private readonly Random demoRandom = new();
    private DateTimeOffset nextDemoRoll = DateTimeOffset.MaxValue;

    public PendingDiceRoll? Pending { get; private set; }
    public int QueuedCount => queuedRolls.Count + (Pending is null ? 0 : 1);
    public int DiceSides => session.Rules.DiceSides;

    public DiceService(BlackjackSession session, ChatQueueService chat, LogService log)
    {
        this.session = session;
        this.chat = chat;
        this.log = log;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    public void RequestRoll(string purpose, Action<BlackjackCard> apply)
    {
        var request = new PendingDiceRoll(Guid.NewGuid(), purpose, DateTimeOffset.Now, apply);

        if (chat.DemoMode)
        {
            if (Pending is null)
            {
                StartDemoRoll(request);
                return;
            }

            queuedRolls.Enqueue(request);
            log.Add(LogCategory.Demo, $"Queued demo dice roll for {purpose}. Pending/queued rolls: {QueuedCount}.");
            return;
        }

        if (Pending is null)
        {
            StartRoll(request);
            return;
        }

        queuedRolls.Enqueue(request);
        log.Add(LogCategory.Dice, $"Queued dice roll for {purpose}. Pending/queued rolls: {QueuedCount}.");
    }

    public bool TryConsumeDealerDice(int result, string rollerName, bool allowUnidentifiedDealer = false)
    {
        if (Pending == null) return false;

        var localName = DalamudServices.PlayerState.IsLoaded ? DalamudServices.PlayerState.CharacterName : string.Empty;
        var knownDealer = !string.IsNullOrWhiteSpace(localName)
            && (string.Equals(rollerName, localName, StringComparison.OrdinalIgnoreCase)
                || rollerName.Contains(localName, StringComparison.OrdinalIgnoreCase)
                || localName.Contains(rollerName, StringComparison.OrdinalIgnoreCase));

        if (!knownDealer && !allowUnidentifiedDealer)
        {
            log.Add(LogCategory.Dice, $"Ignored non-dealer dice roll from {rollerName}: {result}.");
            return false;
        }

        if (!knownDealer && allowUnidentifiedDealer)
            log.Add(LogCategory.Warnings, $"Consumed pending dice result {result} without an exact dealer-name match. Parsed roller: {rollerName}.");

        if (result < 1 || result > session.Rules.DiceSides)
        {
            log.Add(LogCategory.Warnings, $"Ignored dice result {result}; expected 1-{session.Rules.DiceSides}.");
            return false;
        }

        var completed = Pending;
        var card = BlackjackCard.FromDice(result);
        completed.Apply(card);
        log.Add(LogCategory.Dice, $"Consumed dealer dice {result} as {card} for {completed.Purpose}.");
        Pending = null;
        StartNextQueuedRoll();
        return true;
    }

    public void SimulateRoll(int result) => ConsumePendingRoll(result, LogCategory.Demo, "Demo");

    public void ManualConsumeRoll(int result) => ConsumePendingRoll(result, LogCategory.Dice, "Manual live fallback");

    private void ConsumePendingRoll(int result, LogCategory category, string source)
    {
        if (Pending == null) return;
        if (result < 1 || result > session.Rules.DiceSides)
        {
            log.Add(LogCategory.Warnings, $"Ignored {source.ToLowerInvariant()} dice result {result}; expected 1-{session.Rules.DiceSides}.");
            return;
        }

        var completed = Pending;
        var card = BlackjackCard.FromDice(result);
        completed.Apply(card);
        log.Add(category, $"{source} dice {result} => {card} for {completed.Purpose}.");
        Pending = null;
        StartNextQueuedRoll();
    }

    public void ClearPendingAndQueued()
    {
        Pending = null;
        queuedRolls.Clear();
        nextDemoRoll = DateTimeOffset.MaxValue;
        log.Add(LogCategory.Warnings, "Panic stop: pending and queued dice rolls cleared.");
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (!chat.DemoMode || Pending is null || DateTimeOffset.Now < nextDemoRoll)
            return;

        var result = demoRandom.Next(1, session.Rules.DiceSides + 1);
        ConsumePendingRoll(result, LogCategory.Demo, "Demo");
    }

    private void StartDemoRoll(PendingDiceRoll request)
    {
        Pending = request;
        var command = BuildPartyDiceCommand();
        chat.EnqueueCommand(command, true);
        nextDemoRoll = DateTimeOffset.Now.AddSeconds(chat.EffectiveDelaySeconds);
        log.Add(LogCategory.Demo, $"Requested demo {command} for {request.Purpose}; result will resolve after queue delay.");
    }

    private void StartRoll(PendingDiceRoll request)
    {
        Pending = request;
        var command = BuildPartyDiceCommand();
        chat.EnqueueCommand(command, true);
        log.Add(LogCategory.Dice, $"Requested {command} for {request.Purpose}.");
    }

    private string BuildPartyDiceCommand()
    {
        var configured = string.IsNullOrWhiteSpace(session.Rules.DiceCommand)
            ? $"/dice party {session.Rules.DiceSides}"
            : session.Rules.DiceCommand.Trim();

        // FFXIV supports an explicit chat mode in the dice command, for example
        // /dice party 13. Always make the default /dice command party-scoped so it
        // does not depend on the player's active chat tab or Chat 2 mode.
        if (!configured.StartsWith("/dice", StringComparison.OrdinalIgnoreCase))
            return configured;

        var parts = configured.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
            return $"/dice party {session.Rules.DiceSides}";

        var firstArg = parts[1].ToLowerInvariant();
        var explicitChannel = firstArg is "party" or "alliance" or "al" or "freecompany" or "fc" or "pvpteam" or "pt"
            || firstArg.StartsWith("linkshell", StringComparison.OrdinalIgnoreCase)
            || firstArg.StartsWith("l", StringComparison.OrdinalIgnoreCase)
            || firstArg.StartsWith("cwlinkshell", StringComparison.OrdinalIgnoreCase)
            || firstArg.StartsWith("cwl", StringComparison.OrdinalIgnoreCase);

        if (explicitChannel)
            return configured;

        // Normalize shorthand /dice p 13 and older /dice 13 profiles to the
        // long party channel form that FFXIV accepts reliably: /dice party 13.
        if (firstArg == "p")
            return string.Join(" ", new[] { "/dice", "party" }.Concat(parts.Skip(2)));

        return string.Join(" ", new[] { "/dice", "party" }.Concat(parts.Skip(1)));
    }

    private void StartNextQueuedRoll()
    {
        if (Pending is not null || queuedRolls.Count == 0) return;
        var next = queuedRolls.Dequeue();
        if (chat.DemoMode)
            StartDemoRoll(next);
        else
            StartRoll(next);
    }

    public void Dispose() => DalamudServices.Framework.Update -= OnFrameworkUpdate;
}

public sealed record PendingDiceRoll(Guid Id, string Purpose, DateTimeOffset RequestedAt, Action<BlackjackCard> Apply);
