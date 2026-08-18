using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace MacroDeck;

internal sealed class ChatCommandService
{
    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> SendAsync(string command)
    {
        return await AirTablet.DalamudServices.Framework.RunOnFrameworkThread(() => Send(command)).ConfigureAwait(false);
    }

    private unsafe bool Send(string command)
    {
        command = command.Trim();
        if (string.IsNullOrWhiteSpace(command) || !command.StartsWith('/'))
        {
            LastError = "MacroDeck only sends slash commands.";
            return false;
        }
        try
        {
            using var native = new Utf8String(command);
            if (native.Length > 500)
            {
                LastError = "The command is longer than 500 bytes.";
                return false;
            }
            var shell = RaptureShellModule.Instance();
            var ui = UIModule.Instance();
            if (shell is null || ui is null)
            {
                LastError = "The game chat module is unavailable.";
                return false;
            }
            shell->ExecuteCommandInner(&native, ui);
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AirTablet.DalamudServices.Log.Error(ex, "MacroDeck could not execute a macro command.");
            return false;
        }
    }
}
