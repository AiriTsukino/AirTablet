using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace AutoGreet.Services;

/// <summary>
/// Sends native FFXIV slash commands through RaptureShellModule.
/// ICommandManager.ProcessCommand only dispatches Dalamud/plugin commands, not game commands like /tell or /target.
/// </summary>
public sealed class ChatCommandService
{
    public string LastError { get; private set; } = string.Empty;
    public string LastSentCommand { get; private set; } = string.Empty;
    public bool IsReady => true;

    public async Task<bool> SendAsync(string command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await DalamudServices.Framework.RunOnFrameworkThread(() => Send(command)).ConfigureAwait(false);
    }

    private unsafe bool Send(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            LastError = "Empty command.";
            return false;
        }

        command = command.Trim();

        if (!command.StartsWith('/'))
        {
            LastError = "AutoGreet only sends slash commands.";
            return false;
        }

        try
        {
            using var cmd = new Utf8String(command);
            if (cmd.Length > 500)
            {
                LastError = "Command was longer than 500 bytes.";
                DalamudServices.ChatGui.PrintError($"AutoGreet could not send command: {LastError}", "AutoGreet");
                return false;
            }

            var shell = RaptureShellModule.Instance();
            var uiModule = UIModule.Instance();
            if (shell is null || uiModule is null)
            {
                LastError = "RaptureShellModule or UIModule was unavailable.";
                DalamudServices.ChatGui.PrintError($"AutoGreet could not send command: {LastError}", "AutoGreet");
                return false;
            }

            shell->ExecuteCommandInner(&cmd, uiModule);
            LastSentCommand = command;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DalamudServices.Log.Error(ex, "AutoGreet failed to send native chat command.");
            DalamudServices.ChatGui.PrintError($"AutoGreet failed to send command: {ex.Message}", "AutoGreet");
            return false;
        }
    }
}
