using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace ShiftKeeper.Services;

public sealed class ChatCommandService
{
    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> SendTellAsync(string recipient, string message, CancellationToken token)
    {
        var safeMessage = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(safeMessage))
        {
            LastError = "A recipient and message are required.";
            return false;
        }
        return await DalamudServices.Framework.RunOnFrameworkThread(() => Send($"/tell {recipient} {safeMessage}")).ConfigureAwait(false);
    }

    private unsafe bool Send(string command)
    {
        try
        {
            using var value = new Utf8String(command);
            if (value.Length > 500)
            {
                LastError = "The tell was longer than the game's 500-byte command limit.";
                return false;
            }
            var shell = RaptureShellModule.Instance();
            var ui = UIModule.Instance();
            if (shell is null || ui is null)
            {
                LastError = "The game chat module is not available right now.";
                return false;
            }
            shell->ExecuteCommandInner(&value, ui);
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DalamudServices.Log.Error(ex, "ShiftKeeper failed to send a tell.");
            DalamudServices.ChatGui.PrintError($"ShiftKeeper could not send the tell: {ex.Message}", "ShiftKeeper");
            return false;
        }
    }
}
