using AirTablet.Models;

namespace AirTablet.Services;

internal sealed class LaunchService
{
    public bool IsAvailable(AppDescriptor app) =>
        DalamudServices.CommandManager.Commands.ContainsKey(app.Command);

    public bool Open(AppDescriptor app) =>
        DalamudServices.CommandManager.ProcessCommand($"{app.Command} open");

    public bool OpenSettings(AppDescriptor app) =>
        !string.IsNullOrWhiteSpace(app.SettingsCommand) &&
        DalamudServices.CommandManager.ProcessCommand($"{app.SettingsCommand} open");
}
