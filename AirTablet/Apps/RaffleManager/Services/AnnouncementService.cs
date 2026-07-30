using RaffleManager.Models;

namespace RaffleManager.Services;

internal sealed class AnnouncementService
{
    private readonly Configuration config;
    private readonly ChatCommandService chatCommands;

    public AnnouncementService(Configuration config, ChatCommandService chatCommands)
    {
        this.config = config;
        this.chatCommands = chatCommands;
    }

    private VenueProfile Profile => config.Profile;

    public string LastAnnouncementStatus { get; private set; } = "No announcement sent yet.";

    public void AnnounceWinner(WinnerRecord winner)
    {
        if (!Profile.AnnounceWinner)
        {
            LastAnnouncementStatus = "Winner announcement is disabled.";
            return;
        }

        var message = BuildMessage(winner);
        var command = $"/{ChannelCommand(Profile.AnnouncementChannel)} {message}";
        try
        {
            var sent = chatCommands.Send(command);
            LastAnnouncementStatus = sent
                ? $"Sent {Profile.AnnouncementChannel} winner announcement."
                : $"Could not send {Profile.AnnouncementChannel}: {chatCommands.LastError}";

            if (!sent)
                DalamudServices.ChatGui.Print($"[{Profile.AnnouncementChannel}] {message}", "RaffleManager");
        }
        catch (Exception ex)
        {
            LastAnnouncementStatus = $"Announcement failed: {ex.Message}";
            DalamudServices.Log.Warning(ex, "RaffleManager failed to announce winner.");
            DalamudServices.ChatGui.Print($"[{Profile.AnnouncementChannel}] {message}", "RaffleManager");
        }
    }

    public string BuildMessage(WinnerRecord winner)
    {
        return Profile.WinnerMessageTemplate
            .Replace("{winner}", winner.DisplayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", winner.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{world}", winner.World, StringComparison.OrdinalIgnoreCase)
            .Replace("{payout}", $"{winner.Payout:N0} gil", StringComparison.OrdinalIgnoreCase)
            .Replace("{jackpot}", $"{winner.Jackpot:N0} gil", StringComparison.OrdinalIgnoreCase)
            .Replace("{split}", $"{winner.SplitPercent}%", StringComparison.OrdinalIgnoreCase)
            .Replace("{tickets}", $"{winner.Tickets:N0}", StringComparison.OrdinalIgnoreCase)
            .Replace("{totalTickets}", $"{winner.TotalTickets:N0}", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string ChannelCommand(WinnerChatChannel channel) => channel switch
    {
        WinnerChatChannel.Shout => "shout",
        WinnerChatChannel.Yell => "yell",
        _ => "say",
    };
}
