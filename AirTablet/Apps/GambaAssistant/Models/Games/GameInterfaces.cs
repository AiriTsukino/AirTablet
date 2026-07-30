namespace GambaAssistant.Models.Games;

public interface IGameModule
{
    string Name { get; }
    IGameSession CreateSession();
}

public interface IGameSession
{
    string GameName { get; }
    bool IsActive { get; }
}

public interface IGameRuleset { string DisplayName { get; } }
public interface IGameAction { string Name { get; } }
public interface IGameStateMachine
{
    bool CanExecute(IGameAction action, out string reason);
    void Execute(IGameAction action);
}
public interface IGameRenderer { void Draw(); }
