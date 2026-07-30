using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RaffleManager.Models;
using RaffleManager.Services;
using RaffleManager.UI.Components;

namespace RaffleManager.UI;

internal sealed class SettingsWindow : Window
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly LogoService logo;
    private readonly AnnouncementService announcements;

    private string statusMessage = string.Empty;
    private string newProfileName = string.Empty;
    private bool copyCurrentProfile = true;

    public SettingsWindow(Configuration config, PersistenceService persistence, LogoService logo, AnnouncementService announcements)
        : base("RaffleManager Settings###RaffleManagerSettings")
    {
        Size = AirTablet.UI.TabletAppTheme.Px(new Vector2(720, 690));
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(580, 500)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.config = config;
        this.persistence = persistence;
        this.logo = logo;
        this.announcements = announcements;
    }

    private VenueProfile Profile => config.Profile;

    public override void PreDraw() => RaffleTheme.Push();
    public override void PostDraw() => RaffleTheme.Pop();

    public override void Draw()
    {
        UiHelpers.TextMuted($"Profile: {Profile.Name}");

        if (ImGui.BeginTabBar("##settingsTabs"))
        {
            if (ImGui.BeginTabItem("Profiles")) { DrawProfileSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Raffle")) { DrawRaffleSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Branding")) { DrawBrandingSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Announcement")) { DrawAnnouncementSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Sound")) { DrawSoundSettings(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private void DrawProfileSettings()
    {
        if (UiHelpers.BeginCard("##profileSettings", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Venue Profiles", "Each profile has separate raffle settings, branding, contestants, history, and winner data.");

            var names = config.VenueProfiles.Keys.OrderBy(n => n).ToArray();
            var currentIndex = Array.FindIndex(names, n => n.Equals(config.ActiveVenueProfile, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0) currentIndex = 0;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
            if (ImGui.Combo("Active profile", ref currentIndex, names, names.Length) && currentIndex >= 0 && currentIndex < names.Length)
            {
                config.ActiveVenueProfile = names[currentIndex];
                logo.Refresh();
                persistence.SaveNow();
            }

            ImGui.Spacing();
            ImGui.Text("New profile name");
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
            ImGui.InputText("##newProfile", ref newProfileName, 64);
            ImGui.Checkbox("Copy current settings into new profile", ref copyCurrentProfile);
            if (ImGui.Button("Create Profile"))
            {
                if (persistence.CreateProfile(newProfileName, copyCurrentProfile))
                {
                    statusMessage = $"Created profile {config.ActiveVenueProfile}.";
                    newProfileName = string.Empty;
                    logo.Refresh();
                }
                else
                {
                    statusMessage = "Profile already exists or the name was not valid.";
                }
            }

            ImGui.SameLine();
            var canDelete = !Profile.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            if (!canDelete) ImGui.BeginDisabled();
            if (ImGui.Button("Delete Current Profile")) AirTablet.UI.TabletAppTheme.OpenCenteredModal("Delete current profile?");
            if (!canDelete) ImGui.EndDisabled();

            if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                    "Delete current profile?",
                    ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped($"Delete profile '{Profile.Name}' and its saved raffle data? This cannot be undone.");
                if (ImGui.Button("Delete", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
                {
                    var deleted = Profile.Name;
                    persistence.DeleteProfile(deleted);
                    logo.Refresh();
                    statusMessage = $"Deleted profile {deleted}.";
                    AirTablet.UI.TabletAppTheme.CloseCenteredModal();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0)))) AirTablet.UI.TabletAppTheme.CloseCenteredModal();
                AirTablet.UI.TabletAppTheme.EndCenteredModal();
            }

            ImGui.Spacing();
            HandleProfileFileDialogResults();
            if (ImGui.Button(FileDialogService.DialogOpen ? "File dialog open..." : "Export Current Profile"))
            {
                FileDialogService.PickProfileExportPathAsync(persistence.DataRoot, Profile.Name);
            }
            ImGui.SameLine();
            if (ImGui.Button(FileDialogService.DialogOpen ? "File dialog open...##import" : "Import Profile"))
            {
                FileDialogService.PickProfileToImportAsync(persistence.DataRoot);
            }

            UiHelpers.TextMutedWrapped("Export saves the active venue profile to a JSON file. Import loads a selected JSON profile, saves it into this plugin's profile folder, and makes it active.");
            UiHelpers.TextMutedWrapped($"Profile data file: {Path.Combine(persistence.DataRoot, "venue-profiles.json")}");
            if (!string.IsNullOrWhiteSpace(statusMessage)) UiHelpers.TextMutedWrapped(statusMessage);
        }
        UiHelpers.EndCard();
    }


    private void HandleProfileFileDialogResults()
    {
        var exportPath = FileDialogService.ConsumeProfileExportPath();
        if (!string.IsNullOrWhiteSpace(exportPath))
            persistence.ExportProfile(exportPath, Profile, out statusMessage);

        var importPath = FileDialogService.ConsumeProfileImportPath();
        if (!string.IsNullOrWhiteSpace(importPath) && persistence.ImportProfile(importPath, out statusMessage))
            logo.Refresh();
    }

    private void DrawRaffleSettings()
    {
        if (UiHelpers.BeginCard("##raffleSettings", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Raffle Math", "Ticket price and base jackpot update the jackpot in real time as contestants are added.");
            var baseJackpot = Profile.BaseJackpot;
            if (UiHelpers.InputIntGil("Base jackpot", ref baseJackpot, 100000))
            {
                Profile.BaseJackpot = baseJackpot;
                persistence.SaveNow();
            }

            var ticketPrice = Profile.TicketPrice;
            if (UiHelpers.InputIntGil("Ticket price", ref ticketPrice, 10000))
            {
                Profile.TicketPrice = ticketPrice;
                persistence.SaveNow();
            }

            ImGui.Text("Winner split ratio");
            var split = Profile.WinnerSplitPercent;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260f));
            if (ImGui.SliderInt("##split", ref split, 1, 100, $"Winner {split}% / Venue {100 - split}%"))
            {
                Profile.WinnerSplitPercent = split;
                persistence.SaveNow();
            }

            if (ImGui.Button("50/50")) SetSplit(50);
            ImGui.SameLine();
            if (ImGui.Button("60/40")) SetSplit(60);
            ImGui.SameLine();
            if (ImGui.Button("70/30")) SetSplit(70);
            ImGui.SameLine();
            if (ImGui.Button("80/20")) SetSplit(80);

            ImGui.Spacing();
            var bogoBonusCounts = Profile.BogoBonusTicketsCountTowardJackpot;
            if (ImGui.Checkbox("BOGO bonus tickets add to jackpot", ref bogoBonusCounts))
            {
                Profile.BogoBonusTicketsCountTowardJackpot = bogoBonusCounts;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("When enabled, the free bonus tickets from BOGO entries also increase the jackpot. When disabled, only the paid tickets increase the jackpot.");

            ImGui.Spacing();
            UiHelpers.TextMutedWrapped("Winner payout is jackpot multiplied by the winner percentage. The venue side is the remainder. BOGO can add matching bonus tickets from the main raffle tab.");
        }
        UiHelpers.EndCard();
    }

    private void DrawBrandingSettings()
    {
        if (UiHelpers.BeginCard("##brandingSettings", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Screenshot Branding", "Set a venue name and optional custom logo shown in the raffle window.");
            var venue = Profile.VenueName;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
            if (ImGui.InputText("Venue name", ref venue, 128))
            {
                Profile.VenueName = string.IsNullOrWhiteSpace(venue) ? Profile.Name : venue;
                persistence.SaveNow();
            }

            ImGui.Spacing();
            ImGui.Text("Custom logo path");
            var path = Profile.CustomLogoPath;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
            if (ImGui.InputText("##logoPath", ref path, 512))
            {
                Profile.CustomLogoPath = path;
                persistence.SaveNow();
                logo.Refresh();
            }

            var pickedPath = FileDialogService.ConsumePickedImagePath();
            if (!string.IsNullOrWhiteSpace(pickedPath))
            {
                Profile.CustomLogoPath = pickedPath;
                persistence.SaveNow();
                logo.Refresh();
            }

            if (ImGui.Button(FileDialogService.DialogOpen ? "Choosing logo..." : "Browse logo"))
            {
                var initial = string.IsNullOrWhiteSpace(Profile.CustomLogoPath) ? persistence.DataRoot : Profile.CustomLogoPath;
                FileDialogService.PickImageToOpenAsync(initial, "Choose raffle logo");
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear logo"))
            {
                Profile.CustomLogoPath = string.Empty;
                persistence.SaveNow();
                logo.Refresh();
            }

            UiHelpers.TextMutedWrapped(logo.Status);
            UiHelpers.TextMutedWrapped("This plugin starts venue-neutral. Choose your own image for screenshots and Discord posts.");
        }
        UiHelpers.EndCard();
    }

    private void DrawAnnouncementSettings()
    {
        if (UiHelpers.BeginCard("##announcementSettings", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Winner Announcement", "Optional message sent when a winner is pulled.");
            var announce = Profile.AnnounceWinner;
            if (ImGui.Checkbox("Announce winner when pulled", ref announce))
            {
                Profile.AnnounceWinner = announce;
                persistence.SaveNow();
            }

            var channel = (int)Profile.AnnouncementChannel;
            var names = Enum.GetNames<WinnerChatChannel>();
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(180f));
            if (ImGui.Combo("Chat channel", ref channel, names, names.Length))
            {
                Profile.AnnouncementChannel = (WinnerChatChannel)channel;
                persistence.SaveNow();
            }

            var message = Profile.WinnerMessageTemplate;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
            if (ImGui.InputTextMultiline("Message template", ref message, 512, AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 96f))))
            {
                Profile.WinnerMessageTemplate = message;
                persistence.SaveNow();
            }

            UiHelpers.TextMutedWrapped("Tokens: {winner}, {name}, {world}, {payout}, {jackpot}, {split}, {tickets}, {totalTickets}");

            var preview = Profile.WinnerMessageTemplate
                .Replace("{winner}", "Player Name@World", StringComparison.OrdinalIgnoreCase)
                .Replace("{name}", "Player Name", StringComparison.OrdinalIgnoreCase)
                .Replace("{world}", "World", StringComparison.OrdinalIgnoreCase)
                .Replace("{payout}", "5,000,000 gil", StringComparison.OrdinalIgnoreCase)
                .Replace("{jackpot}", "10,000,000 gil", StringComparison.OrdinalIgnoreCase)
                .Replace("{split}", $"{Profile.WinnerSplitPercent}%", StringComparison.OrdinalIgnoreCase)
                .Replace("{tickets}", "10", StringComparison.OrdinalIgnoreCase)
                .Replace("{totalTickets}", "100", StringComparison.OrdinalIgnoreCase);

            ImGui.Spacing();
            ImGui.TextColored(RaffleTheme.Teal, "Preview");
            ImGui.TextWrapped(preview);
        }
        UiHelpers.EndCard();
    }

    private void DrawSoundSettings()
    {
        if (UiHelpers.BeginCard("##soundSettings", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Tick Sound", "Configurable tick sound used during the winner animation.");
            var enabled = Profile.TickSoundEnabled;
            if (ImGui.Checkbox("Enable tick sound", ref enabled))
            {
                Profile.TickSoundEnabled = enabled;
                persistence.SaveNow();
            }

            var volume = Profile.TickVolume;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(240f));
            if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, $"{volume:P0}"))
            {
                Profile.TickVolume = Math.Clamp(volume, 0f, 1f);
                persistence.SaveNow();
            }
        }
        UiHelpers.EndCard();
    }

    private void SetSplit(int value)
    {
        Profile.WinnerSplitPercent = Math.Clamp(value, 1, 100);
        persistence.SaveNow();
    }
}
