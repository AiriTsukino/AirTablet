using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace GambaAssistant.Services;

/// <summary>
/// Sends normal in-game slash commands through the game's shell module.
/// This is plugin-local and does not depend on the selected chat tab or Chat 2.
/// </summary>
public sealed unsafe class NativeChatSender
{
    private readonly LogService log;

    public NativeChatSender(LogService log)
    {
        this.log = log;
        log.Add(LogCategory.Debug, "Native game shell chat sender initialized.");
    }

    public bool TrySend(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        command = command.Trim();

        // GambaAssistant's own commands are Dalamud commands. Normal game commands like
        // /p and /dice must go through the game's shell command path.
        if (command.StartsWith("/gambaassistant", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (DalamudServices.CommandManager.ProcessCommand(command))
                    return true;
            }
            catch (Exception ex)
            {
                log.Add(LogCategory.Warnings, $"Dalamud command processing failed for {command}: {ex.Message}");
            }
        }

        if (!command.StartsWith('/'))
        {
            log.Add(LogCategory.Warnings, $"Refusing to send non-slash command automatically: {command}");
            return false;
        }

        try
        {
            var shell = RaptureShellModule.Instance();
            var uiModule = UIModule.Instance();

            if (shell is null || uiModule is null)
            {
                log.Add(LogCategory.Warnings, $"Game shell or UI module is unavailable for command: {command}");
                return false;
            }

            using var cmd = new Utf8String(command);
            cmd.SanitizeString(
                AllowedEntities.Unknown9 |
                AllowedEntities.Payloads |
                AllowedEntities.OtherCharacters |
                AllowedEntities.SpecialCharacters |
                AllowedEntities.Numbers |
                AllowedEntities.LowercaseLetters |
                AllowedEntities.UppercaseLetters);

            if (cmd.Length == 0 || cmd.Length > 500)
            {
                log.Add(LogCategory.Warnings, $"Refusing to send command with invalid length: {command}");
                return false;
            }

            shell->ExecuteCommandInner(&cmd, uiModule);
            return true;
        }
        catch (Exception ex)
        {
            log.Add(LogCategory.Warnings, $"Native game shell command failed for {command}: {ex.Message}");
            return false;
        }
    }
}
