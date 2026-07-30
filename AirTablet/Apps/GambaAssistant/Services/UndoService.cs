namespace GambaAssistant.Services;

public sealed class UndoService
{
    private readonly Configuration config;
    private readonly LogService log;
    private readonly LinkedList<UndoAction> actions = new();
    public IReadOnlyCollection<UndoAction> Actions => actions.ToList();
    public UndoService(Configuration config, LogService log) { this.config = config; this.log = log; }
    public void Push(string label, Action undo)
    {
        actions.AddFirst(new UndoAction(label, undo, DateTimeOffset.Now));
        while (actions.Count > Math.Clamp(config.General.UndoLimit, 5, 25)) actions.RemoveLast();
        log.Add(LogCategory.Undo, $"Undo available: {label}");
    }
    public bool TryUndoLast()
    {
        if (actions.First == null) return false;
        var action = actions.First.Value;
        actions.RemoveFirst();
        action.Undo();
        log.Add(LogCategory.Undo, $"Undid: {action.Label}");
        return true;
    }
    public void Clear() => actions.Clear();
}
public sealed record UndoAction(string Label, Action Undo, DateTimeOffset Timestamp);
