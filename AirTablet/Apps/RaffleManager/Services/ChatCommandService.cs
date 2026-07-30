using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace RaffleManager.Services;

internal sealed class ChatCommandService
{
    public string LastError { get; private set; } = string.Empty;
    public string LastSentCommand { get; private set; } = string.Empty;

    public unsafe bool Send(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.TrimStart().StartsWith('/'))
        {
            LastError = "Command must be a slash command.";
            return false;
        }

        command = command.Trim();
        try
        {
            using var cmd = new Utf8String(command);
            cmd.SanitizeString(
                AllowedEntities.Unknown9 |
                AllowedEntities.Payloads |
                AllowedEntities.OtherCharacters |
                AllowedEntities.SpecialCharacters |
                AllowedEntities.Numbers |
                AllowedEntities.LowercaseLetters |
                AllowedEntities.UppercaseLetters);
            var shell = RaptureShellModule.Instance();
            var uiModule = UIModule.Instance();
            if (shell is null || uiModule is null)
            {
                LastError = "RaptureShellModule or UIModule was unavailable.";
                DalamudServices.ChatGui.PrintError($"RaffleManager could not send chat: {LastError}", "RaffleManager");
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
            DalamudServices.Log.Warning(ex, "RaffleManager failed to send chat command.");
            return false;
        }
    }
}
