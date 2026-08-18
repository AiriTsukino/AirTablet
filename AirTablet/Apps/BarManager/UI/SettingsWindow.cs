using System.Numerics;
using BarManager.Models;
using BarManager.Services;
using BarManager.UI.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BarManager.UI;

internal sealed class SettingsWindow : Window
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private int selectedVenue;
    private string statusMessage = string.Empty;
    private int pendingDrinkDeleteIndex = -1;
    private int pendingVenueDeleteIndex = -1;
    private Guid? pendingRangeMultiplierDeleteId;

    public SettingsWindow(Configuration config, PersistenceService persistence)
        : base("BarManager Settings###BarManagerSettings")
    {
        Size = AirTablet.UI.TabletAppTheme.Px(new Vector2(940, 720));
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(860, 560)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.config = config;
        this.persistence = persistence;
    }

    public override void PreDraw() => BarManagerTheme.Push();
    public override void PostDraw() => BarManagerTheme.Pop();

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##SettingsTabs"))
        {
            if (ImGui.BeginTabItem("Venues")) { DrawVenues(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Drink Menu")) { DrawDrinks(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Gamba Settings")) { DrawGambaSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Buyouts & Jackpot")) { DrawMoneySettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Files")) { DrawFileSettings(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private void DrawVenues()
    {
        config.EnsureDefaults();
        selectedVenue = Math.Max(0, config.Venues.FindIndex(v => v.Id == config.ActiveVenueId));
        selectedVenue = Math.Clamp(selectedVenue, 0, config.Venues.Count - 1);

        if (UiHelpers.BeginCard("##VenueCard", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Venue Profiles", "Each venue has its own menu, jackpot, buyouts, and gamba settings.");
            var names = config.Venues.Select(v => v.Name).ToArray();
            if (ImGui.Combo("Active venue", ref selectedVenue, names, names.Length))
            {
                config.ActiveVenueId = config.Venues[selectedVenue].Id;
                persistence.SaveNow();
            }

            var venue = config.ActiveVenue;
            var name = venue.Name;
            if (ImGui.InputText("Venue name", ref name, 128))
            {
                venue.Name = string.IsNullOrWhiteSpace(name) ? "Venue" : name;
                persistence.SaveNow();
            }

            if (ImGui.Button("Add venue profile"))
            {
                var clone = new VenueProfile { Name = $"Venue {config.Venues.Count + 1}" };
                config.Venues.Add(clone);
                config.ActiveVenueId = clone.Id;
                selectedVenue = config.Venues.Count - 1;
                config.CurrentAudit = new BarAuditState { JackpotCurrent = clone.JackpotBase };
                persistence.SaveNow();
            }
            ImGui.SameLine();
            if (config.Venues.Count > 1 && ImGui.Button("Delete active venue"))
            {
                pendingVenueDeleteIndex = selectedVenue;
                AirTablet.UI.TabletAppTheme.OpenCenteredModal("Confirm delete venue");
            }

            ImGui.Spacing();
            if (ImGui.Button("Export venue profile"))
            {
                var defaultPath = persistence.GetDefaultVenueExportPath();
                var path = FileDialogService.PickJsonToSave(Path.GetDirectoryName(defaultPath) ?? persistence.DataRoot, Path.GetFileName(defaultPath), "Export venue profile");
                if (!string.IsNullOrWhiteSpace(path))
                    statusMessage = $"Exported venue profile to {persistence.ExportVenueProfile(path)}";
            }
            UiHelpers.TooltipOnHover("Export the active venue profile to a JSON file. The export includes this venue's menu, prices, buyout and jackpot values, and all gamba drink settings so bartenders can share the complete venue setup.");
            ImGui.SameLine();
            if (ImGui.Button("Import venue profile"))
            {
                var path = FileDialogService.PickJsonToOpen(persistence.DataRoot, "Import venue profile");
                if (!string.IsNullOrWhiteSpace(path))
                    persistence.ImportVenueProfile(path, out statusMessage);
            }
            UiHelpers.TooltipOnHover("Import a venue profile JSON file from another bartender or backup. This adds the imported venue as a profile and switches BarManager to it without changing unrelated profiles.");

            if (!string.IsNullOrWhiteSpace(statusMessage))
                UiHelpers.TextWrappedMuted(statusMessage);

            DrawDeleteVenueConfirmation();
        }
        UiHelpers.EndCard();
    }

    private void DrawDrinks()
    {
        var venue = config.ActiveVenue;
        if (UiHelpers.BeginCard("##DrinkMenuCard", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Drink Menu", "Menus start empty so each venue can add only the drinks and prices they use.");
            if (ImGui.Button("Add drink"))
            {
                venue.Drinks.Add(new DrinkDefinition { Name = "New Drink", Price = 0 });
                persistence.SaveNow();
            }

            ImGui.Spacing();
            if (venue.Drinks.Count == 0)
            {
                UiHelpers.TextMuted("No drinks configured yet. Add a drink to begin tracking sales.");
            }
            else if (ImGui.BeginTable("##drinkTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Enabled");
                ImGui.TableSetupColumn("Gamba");
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Price");
                ImGui.TableSetupColumn("Actions");
                ImGui.TableHeadersRow();
                var openDeleteDrinkPopup = false;
                for (var i = 0; i < venue.Drinks.Count; i++)
                {
                    var drink = venue.Drinks[i];
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var enabled = drink.Enabled;
                    if (ImGui.Checkbox("##enabled", ref enabled)) { drink.Enabled = enabled; persistence.SaveNow(); }
                    ImGui.TableNextColumn();
                    var gamba = drink.IsGambaDrink;
                    if (ImGui.Checkbox("##gamba", ref gamba))
                    {
                        foreach (var d in venue.Drinks) d.IsGambaDrink = false;
                        drink.IsGambaDrink = gamba;
                        if (gamba) venue.Gamba.DrinkName = drink.Name;
                        persistence.SaveNow();
                    }
                    ImGui.TableNextColumn();
                    var name = drink.Name;
                    ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
                    if (ImGui.InputText("##name", ref name, 128))
                    {
                        drink.Name = name;
                        if (drink.IsGambaDrink) venue.Gamba.DrinkName = name;
                        persistence.SaveNow();
                    }
                    ImGui.TableNextColumn();
                    var price = drink.Price;
                    ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
                    if (UiHelpers.InputIntGil("##price", ref price, 1000)) { drink.Price = price; persistence.SaveNow(); }
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton("Delete"))
                    {
                        pendingDrinkDeleteIndex = i;
                        openDeleteDrinkPopup = true;
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();

                if (openDeleteDrinkPopup)
                    AirTablet.UI.TabletAppTheme.OpenCenteredModal("Confirm delete drink");
            }

            DrawDeleteDrinkConfirmation(venue);
        }
        UiHelpers.EndCard();
    }

    private void DrawMoneySettings()
    {
        var venue = config.ActiveVenue;
        if (UiHelpers.BeginCard("##MoneyCard", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Buyouts & Jackpot", "Hourly buyout steps by 1 hour, while manual text entry still accepts any decimal value.");
            var jackpot = venue.JackpotBase;
            if (UiHelpers.InputIntGil("Default jackpot base", ref jackpot, 100000)) { venue.JackpotBase = jackpot; persistence.SaveNow(); }
            var current = config.CurrentAudit.JackpotCurrent;
            if (UiHelpers.InputIntGil("Current jackpot", ref current, 100000)) { config.CurrentAudit.JackpotCurrent = current; persistence.SaveNow(); }
            var full = venue.FullBuyoutPrice;
            if (UiHelpers.InputIntGil("Full buyout price", ref full, 100000)) { venue.FullBuyoutPrice = full; persistence.SaveNow(); }
            var hourly = venue.HourlyBuyoutPrice;
            if (UiHelpers.InputIntGil("Hourly buyout price", ref hourly, 100000)) { venue.HourlyBuyoutPrice = hourly; persistence.SaveNow(); }
        }
        UiHelpers.EndCard();
    }

    private void DrawGambaSettings()
    {
        var venue = config.ActiveVenue;
        var gamba = venue.Gamba;

        if (ImGui.BeginChild("##GambaSettingsScroll", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0)), false))
        {
            UiHelpers.Header("Gamba Drink Settings", "Configure the venue-specific gamba drink, party-chat roll tracking, imports/exports, and payout rules.");

            UiHelpers.SectionTitle("Gamba Drink Basics");
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(8f));
            var drinkName = gamba.DrinkName;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260f));
            if (ImGui.InputText("Display name", ref drinkName, 128)) { gamba.DrinkName = string.IsNullOrWhiteSpace(drinkName) ? "Gamba Drink" : drinkName; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("The name shown on the main tab for this venue's gamba drink. This does not have to match another venue's drink name.");
            var rolls = gamba.RollsPerDrink;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Rolls per gamba drink", ref rolls)) { gamba.RollsPerDrink = Math.Max(1, rolls); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover($"How many /dice {gamba.MaxRoll} or plain /dice rolls a customer receives for each purchased gamba drink. Live tracking stops after this many paid rolls unless the session grants free rolls.");
            var min = gamba.MinRoll;
            var max = gamba.MaxRoll;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Minimum roll", ref min)) { gamba.MinRoll = Math.Max(0, min); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Lowest valid roll value accepted for this game and the required lower value for ranged party-chat dice messages. For normal FFXIV dice tracking this should usually stay at 1.");
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Maximum roll", ref max)) { gamba.MaxRoll = Math.Max(gamba.MinRoll + 1, max); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Highest valid roll value accepted for this game and the required dice maximum for ranged party-chat messages. With the default 999, BarManager accepts /dice and /dice 999 results, but ignores ranged rolls like (1-100) because that would not match the venue's required dice size.");
            var autoEnd = gamba.AutoEndWhenRollsUsed;
            if (ImGui.Checkbox("Auto-save session when rolls are used", ref autoEnd)) { gamba.AutoEndWhenRollsUsed = autoEnd; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Automatically ends and saves the active gamba session once the selected customer has used all paid rolls.");
            var paste = gamba.AllowPasteImport;
            if (ImGui.Checkbox("Allow manual paste import", ref paste)) { gamba.AllowPasteImport = paste; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Allows roll results to be pasted or entered manually. Turn this off if your venue only wants live party-chat roll tracking.");
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(8f));

            UiHelpers.SectionTitle("Party Chat Tracking");
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(8f));
            UiHelpers.TextWrappedMuted($"Tracks only the active session customer and only while a session is live. Ranged Random! messages must match /dice {gamba.MaxRoll}; plain /dice messages without a range are accepted.");
            var announce = gamba.AnnounceRollsLeft;
            if (ImGui.Checkbox("Announce rolls remaining in party chat", ref announce)) { gamba.AnnounceRollsLeft = announce; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("When enabled, BarManager sends a party-chat message telling the player how many rolls they have left at the configured interval.");
            var every = gamba.AnnounceEveryRolls;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Announce every N rolls", ref every)) { gamba.AnnounceEveryRolls = Math.Clamp(every, 1, 50); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Controls how often the remaining-roll announcement is sent. A value of 5 announces at 5, 10, 15 and so on, and always announces at 0.");
            UiHelpers.TextWrappedMuted("Default is 5. Valid range is 1-50. It also announces when the player reaches 0 rolls.");
            var contribute = gamba.AddRollPricePercentToJackpot;
            if (ImGui.Checkbox("Add percentage of each roll price to jackpot", ref contribute)) { gamba.AddRollPricePercentToJackpot = contribute; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Adds part of each played roll's drink price back into the current jackpot in real time. This is useful for venues where every gamba roll grows the jackpot.");
            var percent = gamba.JackpotContributionPercent;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputFloat("Jackpot contribution %", ref percent, 1f, 5f)) { gamba.JackpotContributionPercent = Math.Clamp(percent, 0f, 100f); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Percentage of the roll price added to the jackpot when jackpot contribution is enabled. Example: 10 means a 100,000 gil roll adds 10,000 gil.");
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(8f));

            UiHelpers.SectionTitle("Jackpot Win Actions");
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(8f));
            var autoEndJackpot = gamba.AutoEndOnJackpotWin;
            if (ImGui.Checkbox("End session automatically on jackpot win", ref autoEndJackpot)) { gamba.AutoEndOnJackpotWin = autoEndJackpot; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Off by default. When enabled, a jackpot win immediately tells the customer to stop rolling, sets remaining rolls to 0, saves the session, and prevents unused remaining rolls from being played.");

            var showJackpotPurchaseGuidance = gamba.ShowRollPurchaseGuidanceAfterJackpotWin;
            if (ImGui.Checkbox("Show roll purchase guidance after jackpot wins", ref showJackpotPurchaseGuidance)) { gamba.ShowRollPurchaseGuidanceAfterJackpotWin = showJackpotPurchaseGuidance; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Off by default. When off, a jackpot session ends with only a congratulatory jackpot payout message. Enable this to also show how many rolls the winnings could purchase and any remaining cashout amount. Non-jackpot session messages are not affected.");

            var jackpotShoutout = gamba.JackpotShoutoutEnabled;
            if (ImGui.Checkbox("Announce jackpot winner", ref jackpotShoutout)) { gamba.JackpotShoutoutEnabled = jackpotShoutout; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("When enabled, BarManager sends a venue-wide jackpot shoutout using the selected chat channel when the customer wins the jackpot.");

            var shoutChannel = NormalizeJackpotChannel(gamba.JackpotShoutoutChannel);
            var channelIndex = shoutChannel switch { "say" => 0, "shout" => 1, _ => 2 };
            var channelLabels = new[] { "/say", "/shout", "/yell" };
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.Combo("Jackpot announcement channel", ref channelIndex, channelLabels, channelLabels.Length))
            {
                gamba.JackpotShoutoutChannel = channelIndex switch { 0 => "say", 1 => "shout", _ => "yell" };
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Chat channel used for the jackpot shoutout. /yell is the default.");

            var shoutMessage = gamba.JackpotShoutoutMessage;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1f));
            if (ImGui.InputTextMultiline("Jackpot shoutout message", ref shoutMessage, 512, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1f, 58f))))
            {
                gamba.JackpotShoutoutMessage = string.IsNullOrWhiteSpace(shoutMessage) ? "Congratulations {player}! They just won the jackpot for {payout} gil!" : shoutMessage;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Message sent when a jackpot is won. Available placeholders: {player}, {payout}, {jackpot}, and {venue}.");
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(8f));

            UiHelpers.SectionTitle("Global Bonus Multipliers");
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(8f));
            UiHelpers.TextWrappedMuted("These bonuses apply to the gamba game as a whole instead of individual payout rules. Roll ranges apply whenever a winning roll lands in their range; activation bonuses affect the next win while active.");

            DrawRollRangeMultipliers(gamba);

            var lossEnabled = gamba.LossStreakBonusEnabled;
            if (ImGui.Checkbox("Enable loss streak bonus", ref lossEnabled)) { gamba.LossStreakBonusEnabled = lossEnabled; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("When enabled, a configurable number of consecutive losing rolls activates a named bonus. The bonus remains active until the player's next win, then multiplies that winning payout and turns itself off.");
            var lossName = gamba.LossStreakBonusName;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260f));
            if (ImGui.InputText("Loss streak bonus name", ref lossName, 128)) { gamba.LossStreakBonusName = string.IsNullOrWhiteSpace(lossName) ? "Loss Streak Bonus" : lossName; persistence.SaveNow(); }
            var lossAnnouncement = gamba.LossStreakBonusAnnouncement;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1f));
            if (ImGui.InputTextMultiline("Loss streak announcement", ref lossAnnouncement, 512, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1f, 58f)))) { gamba.LossStreakBonusAnnouncement = string.IsNullOrWhiteSpace(lossAnnouncement) ? "{player}, {bonus} is active! Your next win is multiplied by x{multiplier} {duration}." : lossAnnouncement; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Party-chat text sent when the loss streak bonus activates. Available placeholders: {player}, {bonus}, {multiplier}, and {duration}.");
            var lossThreshold = gamba.LossStreakThreshold;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Loss streak length", ref lossThreshold)) { gamba.LossStreakThreshold = Math.Clamp(lossThreshold, 1, 999); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("How many losing rolls in a row are required before the loss streak bonus activates. The default is 5 when this feature is enabled.");
            var lossMultiplier = gamba.LossStreakBonusMultiplier;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputFloat("Loss streak multiplier", ref lossMultiplier, 0.25f, 1f)) { gamba.LossStreakBonusMultiplier = Math.Clamp(lossMultiplier, 1f, 100f); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Multiplier applied to the next win after the loss streak bonus activates. Example: 2 means the next win is doubled.");
            var lossDuration = FormatNullableTurns(gamba.LossStreakBonusDurationTurns);
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputText("Loss streak duration turns", ref lossDuration, 16)) { gamba.LossStreakBonusDurationTurns = ParseNullableTurns(lossDuration, null); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("How many customer rolls this bonus can stay active after it turns on. Leave empty for unlimited turns. The bonus still turns off early when the player gets a win.");
            var lossAppliesToJackpot = gamba.LossStreakBonusAppliesToJackpot;
            if (ImGui.Checkbox("Loss streak can multiply jackpot wins", ref lossAppliesToJackpot)) { gamba.LossStreakBonusAppliesToJackpot = lossAppliesToJackpot; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Off by default. When off, this bonus can still activate and be consumed by a jackpot win, but it will not multiply the jackpot payout. Turn it on only if your venue allows jackpot wins to be multiplied by this bonus.");

            ImGui.Spacing();
            var bartenderEnabled = gamba.BartenderRollBonusEnabled;
            if (ImGui.Checkbox("Enable bartender roll bonus", ref bartenderEnabled)) { gamba.BartenderRollBonusEnabled = bartenderEnabled; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("When enabled during a live session, BarManager automatically sends /dice party using the configured dice maximum after each customer roll, only when the bonus is eligible. It does not roll immediately when a session starts. If the bartender rolls 1, this named bonus activates for the configured duration or until the next win. It cannot activate while the loss streak bonus is active.");
            var bartenderName = gamba.BartenderRollBonusName;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260f));
            if (ImGui.InputText("Bartender bonus name", ref bartenderName, 128)) { gamba.BartenderRollBonusName = string.IsNullOrWhiteSpace(bartenderName) ? "Bartender Bonus" : bartenderName; persistence.SaveNow(); }
            var bartenderAnnouncement = gamba.BartenderRollBonusAnnouncement;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1f));
            if (ImGui.InputTextMultiline("Bartender bonus announcement", ref bartenderAnnouncement, 512, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1f, 58f)))) { gamba.BartenderRollBonusAnnouncement = string.IsNullOrWhiteSpace(bartenderAnnouncement) ? "{player}, {bonus} is active! Your next win is multiplied by x{multiplier} {duration}." : bartenderAnnouncement; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Party-chat text sent when the bartender roll bonus activates. Available placeholders: {player}, {bonus}, {multiplier}, and {duration}.");
            var bartenderMax = gamba.BartenderRollMax;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Bartender bonus dice max", ref bartenderMax)) { gamba.BartenderRollMax = Math.Clamp(bartenderMax, 2, 999999); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("The dice maximum BarManager automatically rolls with /dice party after a customer's roll. The bonus only activates if the bartender's result is exactly 1.");
            var bartenderMultiplier = gamba.BartenderRollBonusMultiplier;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputFloat("Bartender bonus multiplier", ref bartenderMultiplier, 0.25f, 1f)) { gamba.BartenderRollBonusMultiplier = Math.Clamp(bartenderMultiplier, 1f, 100f); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Multiplier applied to the next win if the bartender bonus activates. Default duration is 3 customer rolls or until a win, whichever happens first.");
            var bartenderDuration = FormatNullableTurns(gamba.BartenderRollBonusDurationTurns);
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputText("Bartender bonus duration turns", ref bartenderDuration, 16)) { gamba.BartenderRollBonusDurationTurns = ParseNullableTurns(bartenderDuration, 3); persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("How many customer rolls this bonus can stay active after it turns on. Leave empty for unlimited turns. Default is 3 turns. The bonus still turns off early when the player gets a win.");
            var bartenderAppliesToJackpot = gamba.BartenderRollBonusAppliesToJackpot;
            if (ImGui.Checkbox("Bartender bonus can multiply jackpot wins", ref bartenderAppliesToJackpot)) { gamba.BartenderRollBonusAppliesToJackpot = bartenderAppliesToJackpot; persistence.SaveNow(); }
            UiHelpers.TooltipOnHover("Off by default. When off, this bonus can still activate and be consumed by a jackpot win, but it will not multiply the jackpot payout. Turn it on only if your venue allows jackpot wins to be multiplied by this bonus.");
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(8f));

            UiHelpers.SectionTitle("Import / Export");
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(8f));
            UiHelpers.TextWrappedMuted("Use this to transfer gamba drink settings between bartenders at the same venue. These buttons open a file picker instead of requiring you to type a path.");
            if (ImGui.Button("Export gamba settings"))
            {
                var defaultPath = persistence.GetDefaultGambaExportPath();
                var path = FileDialogService.PickJsonToSave(Path.GetDirectoryName(defaultPath) ?? persistence.GambaSettingsRoot, Path.GetFileName(defaultPath), "Export gamba settings");
                if (!string.IsNullOrWhiteSpace(path))
                    statusMessage = $"Exported gamba settings to {persistence.ExportGambaSettings(path)}";
            }
            ImGui.SameLine();
            if (ImGui.Button("Import gamba settings"))
            {
                var path = FileDialogService.PickJsonToOpen(persistence.GambaSettingsRoot, "Import gamba settings");
                if (!string.IsNullOrWhiteSpace(path))
                    persistence.ImportGambaSettings(path, out statusMessage);
            }
            ImGui.SameLine();
            if (ImGui.Button("Open gamba folder"))
                OpenFolder(persistence.GambaSettingsRoot);
            if (!string.IsNullOrWhiteSpace(statusMessage)) UiHelpers.TextWrappedMuted(statusMessage);
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(8f));

            UiHelpers.SectionTitle("Payout Rules");
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(8f));
            UiHelpers.TextWrappedMuted("Rules are evaluated top-to-bottom. The first enabled matching rule wins.");
            if (ImGui.Button("Add rule"))
            {
                gamba.Rules.Add(new GambaRule { Name = "New Rule", Tier = "CUSTOM" });
                persistence.SaveNow();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear all rules"))
                AirTablet.UI.TabletAppTheme.OpenCenteredModal("Confirm clear gamba rules");
            DrawClearRulesConfirmation(gamba);

            if (gamba.Rules.Count == 0)
            {
                UiHelpers.TextWrappedMuted("No gamba rules configured. Rolls will resolve as NONE until you add payout rules or import a gamba settings file.");
            }
            else
            {
                for (var i = 0; i < gamba.Rules.Count; i++)
                {
                    DrawRuleEditor(gamba.Rules, i);
                }
            }
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(8f));
            ImGui.Spacing();
            ImGui.Dummy(AirTablet.UI.TabletAppTheme.Px(new Vector2(1f, 12f)));
        }
        ImGui.EndChild();
    }

    private void DrawRuleEditor(List<GambaRule> rules, int i)
    {
        var r = rules[i];
        ImGui.PushID(i);
        var open = ImGui.CollapsingHeader($"{i + 1}. {r.Name}  [{r.Tier}]###rule{i}", ImGuiTreeNodeFlags.DefaultOpen);
        if (open)
        {
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(10f));
            EnsureRuleRollExpression(r);
            var available = ImGui.GetContentRegionAvail().X;
            var optionsWidth = MathF.Min(
                AirTablet.UI.TabletAppTheme.Px(245f),
                MathF.Max(AirTablet.UI.TabletAppTheme.Px(210f), available * 0.34f));
            var detailsWidth = MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(280f),
                available - optionsWidth - AirTablet.UI.TabletAppTheme.Px(20f));

            if (ImGui.BeginTable("##RuleLayout", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Rule details", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Match options", ImGuiTableColumnFlags.WidthFixed, optionsWidth);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                DrawRuleDetails(r, detailsWidth);

                ImGui.TableNextColumn();
                DrawRuleMatchOptions(r);

                ImGui.EndTable();
            }

            ImGui.Spacing();
            if (i > 0 && ImGui.Button("Move up")) { (rules[i - 1], rules[i]) = (rules[i], rules[i - 1]); persistence.SaveNow(); }
            if (i > 0) ImGui.SameLine();
            if (i < rules.Count - 1 && ImGui.Button("Move down")) { (rules[i + 1], rules[i]) = (rules[i], rules[i + 1]); persistence.SaveNow(); }
            if (i < rules.Count - 1) ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                rules.RemoveAt(i);
                persistence.SaveNow();
                ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(10f));
                ImGui.PopID();
                return;
            }
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(10f));
        }
        ImGui.Separator();
        ImGui.PopID();
    }

    private void DrawRuleDetails(GambaRule r, float detailsWidth)
    {
        var inputWidth = MathF.Min(
            AirTablet.UI.TabletAppTheme.Px(460f),
            MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(190f),
                detailsWidth - AirTablet.UI.TabletAppTheme.Px(16f)));

        var enabled = r.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { r.Enabled = enabled; persistence.SaveNow(); }
        UiHelpers.TooltipOnHover("Disabled rules stay saved but are skipped when resolving a roll.");
        ImGui.SameLine();
        var paysJackpot = r.PaysJackpot;
        if (ImGui.Checkbox("Pays current jackpot", ref paysJackpot)) { r.PaysJackpot = paysJackpot; persistence.SaveNow(); }
        UiHelpers.TooltipOnHover("When this rule wins, it pays the venue's current jackpot amount instead of the fixed payout value.");
        ImGui.SameLine();
        var free = r.GrantsFreeRoll;
        if (ImGui.Checkbox("Grants free roll", ref free)) { r.GrantsFreeRoll = free; persistence.SaveNow(); }
        UiHelpers.TooltipOnHover("When this rule wins, the session receives one extra roll without requiring another drink purchase.");

        ImGui.Spacing();
        var name = r.Name;
        ImGui.TextUnformatted("Name");
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputText("##ruleName", ref name, 64)) { r.Name = name; persistence.SaveNow(); }
        UiHelpers.TooltipOnHover("Friendly name for this payout rule, such as Jackpot, Free Roll, or Second Prize.");

        var tier = r.Tier;
        ImGui.TextUnformatted("Tier label");
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputText("##ruleTier", ref tier, 32)) { r.Tier = string.IsNullOrWhiteSpace(tier) ? "CUSTOM" : tier.ToUpperInvariant(); persistence.SaveNow(); }
        UiHelpers.TooltipOnHover("Short label saved in the session history and report when this rule wins.");

        var payout = r.Payout;
        ImGui.TextUnformatted("Payout");
        ImGui.SetNextItemWidth(inputWidth);
        if (UiHelpers.InputIntGil("##rulePayout", ref payout, 5000)) { r.Payout = payout; persistence.SaveNow(); }
        UiHelpers.TooltipOnHover("Fixed gil payout for this rule. This is ignored when 'Pays current jackpot' is enabled.");

        var rollMatch = r.WinningRollExpression;
        ImGui.TextUnformatted("Winning roll(s)");
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputText("##ruleWinningRolls", ref rollMatch, 128))
        {
            ApplyRuleRollExpression(r, rollMatch);
            UpdateRuleTooltipCache(r, force: true);
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover(GetWinningRollsTooltip(r));
    }

    private void DrawRuleMatchOptions(GambaRule r)
    {
        UiHelpers.TextMuted("Match options");
        var triples = r.Triples;
        if (UiHelpers.CheckboxWithHelp("Any triple", ref triples, "Matches any three identical digits, such as 111, 555, or 999. This works even when Winning roll(s) is empty, so you do not need to list every triple manually."))
        {
            r.Triples = triples;
            persistence.SaveNow();
        }

        var adjacent = r.AdjacentDoubles;
        if (UiHelpers.CheckboxWithHelp("Adjacent doubles", ref adjacent, "Matches rolls with the same digit next to itself, such as 22, 100, 550, 770, or 899. This works even when Winning roll(s) is empty, so any adjacent double can win."))
        {
            r.AdjacentDoubles = adjacent;
            persistence.SaveNow();
        }

        var runs = r.Runs;
        if (UiHelpers.CheckboxWithHelp("Runs", ref runs, "Matches a three-digit sequence in the selected direction. Count up matches 123, 234, 345, 456, 567, 678, and 789. Count down matches 321, 432, 543, 654, 765, 876, and 987."))
        {
            r.Runs = runs;
            persistence.SaveNow();
        }
        if (r.Runs)
        {
            ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(12f));
            var countUp = r.RunsCountUp ?? r.RunDirection == GambaRunDirection.CountUp;
            if (UiHelpers.CheckboxWithHelp("Count up", ref countUp, "Matches ascending runs: 123, 234, 345, 456, 567, 678, and 789."))
            {
                r.RunsCountUp = countUp;
                persistence.SaveNow();
            }
            var countDown = r.RunsCountDown ?? r.RunDirection == GambaRunDirection.CountDown;
            if (UiHelpers.CheckboxWithHelp("Count down", ref countDown, "Matches descending runs: 321, 432, 543, 654, 765, 876, and 987."))
            {
                r.RunsCountDown = countDown;
                persistence.SaveNow();
            }
            if (!countUp && !countDown)
                UiHelpers.TextWrappedMuted("Enable Count up, Count down, or both for this Runs condition to match a roll.");
            ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(12f));
        }

        if (ShouldShowExactOnly(r))
        {
            var exactOnly = r.ExactOnly;
            var exactTooltip = GetExactOnlyTooltip(r);
            if (UiHelpers.CheckboxWithHelp("Exact only", ref exactOnly, exactTooltip))
            {
                r.ExactOnly = exactOnly;
                UpdateRuleTooltipCache(r, force: true);
                persistence.SaveNow();
            }
        }
        else
        {
            r.ExactOnly = true;
            UiHelpers.TextWrappedMuted("Exact only is hidden because every entered winning roll is already 3 digits. /dice 999 cannot roll more than 3 digits, so those values are always exact.");
        }
    }

    private void DrawRollRangeMultipliers(GambaSettings gamba)
    {
        ImGui.Spacing();
        ImGui.TextColored(BarManagerTheme.Gold, "Roll range multipliers");
        UiHelpers.TextWrappedMuted("A winning payout inside the first enabled matching range is multiplied by that range amount. Range bonuses can stack with an active loss-streak or bartender bonus.");
        if (ImGui.Button("+ Add multiplier range"))
        {
            var lower = Math.Max(0, gamba.MinRoll);
            var upper = Math.Max(lower, gamba.MaxRoll);
            var midpoint = Math.Max(lower, lower + Math.Max(0, upper - lower + 1) / 2 - 1);
            gamba.RollRangeMultipliers.Add(new GambaRollRangeMultiplier
            {
                MinimumRoll = lower,
                MaximumRoll = midpoint,
                Multiplier = 2f,
            });
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Adds a profile-specific roll range. For example, 1-499 at x2 doubles any winning payout whose roll falls in that range.");

        if (gamba.RollRangeMultipliers.Count == 0)
        {
            UiHelpers.TextWrappedMuted("No roll range multipliers configured. Add one to multiply winning payouts based on the rolled number.");
            ImGui.Spacing();
            return;
        }

        if (ImGui.BeginTable("##RollRangeMultipliers", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(34f));
            ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(112f));
            ImGui.TableSetupColumn("Through", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(112f));
            ImGui.TableSetupColumn("Multiplier", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Jackpot", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(62f));
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(116f));
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(58f));
            ImGui.TableHeadersRow();
            for (var i = 0; i < gamba.RollRangeMultipliers.Count; i++)
            {
                var range = gamba.RollRangeMultipliers[i];
                ImGui.PushID(range.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var enabled = range.Enabled;
                if (ImGui.Checkbox("##enabled", ref enabled)) { range.Enabled = enabled; persistence.SaveNow(); }

                ImGui.TableNextColumn();
                var minimum = range.MinimumRoll;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputInt("##minimum", ref minimum))
                {
                    range.MinimumRoll = Math.Clamp(minimum, Math.Max(0, gamba.MinRoll), Math.Max(gamba.MinRoll, gamba.MaxRoll));
                    range.MaximumRoll = Math.Clamp(range.MaximumRoll, range.MinimumRoll, Math.Max(range.MinimumRoll, gamba.MaxRoll));
                    persistence.SaveNow();
                }

                ImGui.TableNextColumn();
                var maximum = range.MaximumRoll;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputInt("##maximum", ref maximum))
                {
                    range.MaximumRoll = Math.Clamp(maximum, range.MinimumRoll, Math.Max(range.MinimumRoll, gamba.MaxRoll));
                    persistence.SaveNow();
                }

                ImGui.TableNextColumn();
                var multiplier = range.Multiplier;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputFloat("##multiplier", ref multiplier, 0.25f, 1f))
                {
                    range.Multiplier = Math.Clamp(multiplier, 1f, 100f);
                    persistence.SaveNow();
                }

                ImGui.TableNextColumn();
                var jackpot = range.AppliesToJackpot;
                if (ImGui.Checkbox("##jackpot", ref jackpot)) { range.AppliesToJackpot = jackpot; persistence.SaveNow(); }
                UiHelpers.TooltipOnHover("When enabled, this range may also multiply a jackpot payout. It is off by default.");

                ImGui.TableNextColumn();
                if (i > 0 && ImGui.SmallButton("Up"))
                {
                    (gamba.RollRangeMultipliers[i - 1], gamba.RollRangeMultipliers[i]) = (gamba.RollRangeMultipliers[i], gamba.RollRangeMultipliers[i - 1]);
                    persistence.SaveNow();
                }
                if (i > 0 && i < gamba.RollRangeMultipliers.Count - 1) ImGui.SameLine();
                if (i < gamba.RollRangeMultipliers.Count - 1 && ImGui.SmallButton("Down"))
                {
                    (gamba.RollRangeMultipliers[i + 1], gamba.RollRangeMultipliers[i]) = (gamba.RollRangeMultipliers[i], gamba.RollRangeMultipliers[i + 1]);
                    persistence.SaveNow();
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Delete"))
                {
                    pendingRangeMultiplierDeleteId = range.Id;
                    AirTablet.UI.TabletAppTheme.OpenCenteredModal("Confirm delete multiplier range");
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        UiHelpers.TextWrappedMuted("Ranges are checked from top to bottom; only the first enabled range containing the roll applies. Jackpot payouts are multiplied only when Jackpot is checked for that range.");
        DrawDeleteRangeMultiplierConfirmation(gamba);
        ImGui.Spacing();
    }

    private void DrawFileSettings()
    {
        if (UiHelpers.BeginCard("##FilesCard", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Files & Folders", "Main Dalamud config stays small; menus, gamba settings, current audit, and reports are saved separately.");
            UiHelpers.TextWrappedMuted($"Dalamud config folder: {persistence.ConfigRoot}");
            ImGui.Separator();

            var dataDirectory = config.DataDirectory;
            UiHelpers.TextWrappedMuted($"Current data folder: {persistence.DataRoot}");
            if (ImGui.InputText("Data folder override", ref dataDirectory, 512))
            {
                config.DataDirectory = dataDirectory;
                persistence.EnsureFolders();
                persistence.SaveNow();
            }

            var auditDirectory = config.AuditReportDirectory;
            UiHelpers.TextWrappedMuted($"Current audit report folder: {persistence.AuditReportRoot}");
            if (ImGui.InputText("Audit report folder override", ref auditDirectory, 512))
            {
                config.AuditReportDirectory = auditDirectory;
                persistence.EnsureFolders();
                persistence.SaveConfig();
            }

            var gambaDirectory = config.GambaSettingsDirectory;
            UiHelpers.TextWrappedMuted($"Current gamba import/export folder: {persistence.GambaSettingsRoot}");
            if (ImGui.InputText("Gamba settings folder override", ref gambaDirectory, 512))
            {
                config.GambaSettingsDirectory = gambaDirectory;
                persistence.EnsureFolders();
                persistence.SaveConfig();
            }

            ImGui.Spacing();
            if (ImGui.Button("Open data folder")) OpenFolder(persistence.DataRoot);
            ImGui.SameLine();
            if (ImGui.Button("Open audit folder")) OpenFolder(persistence.AuditReportRoot);
            ImGui.SameLine();
            if (ImGui.Button("Open gamba folder")) OpenFolder(persistence.GambaSettingsRoot);
        }
        UiHelpers.EndCard();
    }


    private static void EnsureRuleRollExpression(GambaRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.WinningRollExpression))
        {
            UpdateRuleTooltipCache(rule);
            return;
        }

        rule.WinningRollExpression = GetRuleRollExpression(rule);
        UpdateRuleTooltipCache(rule, force: true);
    }

    private static string GetWinningRollsTooltip(GambaRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.WinningRollsTooltip))
            UpdateRuleTooltipCache(rule, force: true);

        return rule.WinningRollsTooltip;
    }

    private static string GetExactOnlyTooltip(GambaRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ExactOnlyTooltip))
            UpdateRuleTooltipCache(rule, force: true);

        return rule.ExactOnlyTooltip;
    }

    private static void UpdateRuleTooltipCache(GambaRule rule, bool force = false)
    {
        var normalized = NormalizeTooltipExpression(rule.WinningRollExpression);
        var cacheKey = $"{normalized}|exact:{rule.ExactOnly}";
        if (!force && rule.LastTooltipRollExpression == cacheKey &&
            !string.IsNullOrWhiteSpace(rule.WinningRollsTooltip) &&
            !string.IsNullOrWhiteSpace(rule.ExactOnlyTooltip))
        {
            return;
        }

        var tokens = GetTooltipRollTokens(normalized);
        rule.WinningRollsTooltip = BuildWinningRollsTooltip(tokens);
        rule.ExactOnlyTooltip = BuildExactOnlyTooltip(tokens);
        rule.LastTooltipRollExpression = cacheKey;
    }


    private static bool ShouldShowExactOnly(GambaRule rule)
    {
        var tokens = GetTooltipRollTokens(rule.WinningRollExpression);
        return tokens.Count == 0 || tokens.Any(token => token.Length < 3);
    }

    private static bool HasOnlyThreeDigitWinningRolls(List<string> tokens)
    {
        return tokens.Count > 0 && tokens.All(token => token.Length >= 3);
    }

    private static string BuildWinningRollsTooltip(List<string> tokens)
    {
        if (tokens.Count == 0)
            return "Enter one winning roll, such as 777, or multiple roll values separated by commas, such as 111,222,333. If Exact only is visible and on, those values must match the full /dice result. If Exact only is off, shorter values can appear anywhere inside the roll.";

        var values = string.Join(", ", tokens);
        if (HasOnlyThreeDigitWinningRolls(tokens))
            return $"Current winning roll value{(tokens.Count == 1 ? string.Empty : "s")}: {values}. Exact only is hidden because every entered value is already 3 digits, and /dice 999 cannot roll more than 3 digits.";

        return $"Current winning roll value{(tokens.Count == 1 ? string.Empty : "s")}: {values}. Exact only controls whether {(tokens.Count == 1 ? "this value" : "these values")} must match the full /dice result or can appear anywhere inside the roll.";
    }

    private static string BuildExactOnlyTooltip(List<string> tokens)
    {
        if (tokens.Count == 0)
            return "Exact only changes how Winning roll(s) are matched. No winning roll is currently entered, so exact matching is not used. Checked pattern types, such as Any triple, Adjacent doubles, or Runs, can still win on any roll that fits those patterns.";

        if (HasOnlyThreeDigitWinningRolls(tokens))
            return "Exact only is hidden for this rule because every entered winning roll is already 3 digits. With /dice 999, a 3-digit value can only match itself.";

        var first = tokens[0];
        var exactValues = string.Join(", ", tokens);
        var containsExamples = BuildContainsExamples(first);

        return $"Based on your current Winning roll{(tokens.Count == 1 ? string.Empty : "s")}: {exactValues}. With Exact only ON, {(tokens.Count == 1 ? "the roll must be exactly " + first : "the roll must exactly match one of those values")}. With Exact only OFF, {(tokens.Count == 1 ? first : "each listed value")} can appear anywhere inside the /dice result. For example, {first} would win on {containsExamples}.";
    }

    private static List<string> GetTooltipRollTokens(string text)
    {
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsDigit).ToArray()))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct()
            .OrderBy(token => int.TryParse(token, out var n) ? n : int.MaxValue)
            .ThenBy(token => token)
            .ToList();
    }

    private static string NormalizeTooltipExpression(string text)
    {
        var tokens = GetTooltipRollTokens(text);
        return tokens.Count == 0 ? string.Empty : string.Join(",", tokens);
    }

    private static List<string> GetRuleRollTokens(GambaRule rule)
    {
        var fromExpression = GetTooltipRollTokens(rule.WinningRollExpression);
        if (fromExpression.Count > 0)
            return fromExpression;

        var values = new List<int>();
        if (rule.EqualTo.HasValue)
            values.Add(rule.EqualTo.Value);
        values.AddRange(rule.InValues);

        return values
            .Distinct()
            .OrderBy(v => v)
            .Select(v => v.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    private static string BuildContainsExamples(string token)
    {
        token = token.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return "72, 172, 272, or 720";

        if (token.Length >= 3)
            return $"{token}";

        if (token.Length == 2)
        {
            var prefix = $"1{token}";
            if (prefix.Length > 3) prefix = token;
            var middle = $"2{token}";
            if (middle.Length > 3) middle = token;
            var suffix = $"{token}0";
            if (suffix.Length > 3) suffix = token;
            return $"{token}, {prefix}, {middle}, or {suffix}";
        }

        return $"{token}, 1{token}, {token}2, or {token}{token}{token}";
    }

    private static string GetRuleRollExpression(GambaRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.WinningRollExpression))
            return rule.WinningRollExpression;
        if (rule.InValues.Count > 0)
            return string.Join(",", rule.InValues);
        return rule.EqualTo?.ToString() ?? string.Empty;
    }

    private static void ApplyRuleRollExpression(GambaRule rule, string text)
    {
        rule.WinningRollExpression = text;
        var values = ParseInts(text).Distinct().OrderBy(v => v).ToList();
        rule.EqualTo = values.Count == 1 ? values[0] : null;
        rule.InValues = values.Count > 1 ? values : new List<int>();
        rule.ContainsTokens.Clear();
        rule.ContainsAnyDigits.Clear();
    }

    private void DrawDeleteRangeMultiplierConfirmation(GambaSettings gamba)
    {
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Confirm delete multiplier range",
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
            return;

        var range = pendingRangeMultiplierDeleteId.HasValue
            ? gamba.RollRangeMultipliers.FirstOrDefault(candidate => candidate.Id == pendingRangeMultiplierDeleteId.Value)
            : null;
        var description = range is null
            ? "this multiplier range"
            : $"the {range.MinimumRoll:N0}-{range.MaximumRoll:N0} x{range.Multiplier:0.##} multiplier range";
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(380f));
        ImGui.TextUnformatted($"Delete {description} from this venue's gamba profile?");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Delete range", AirTablet.UI.TabletAppTheme.Px(new Vector2(125f, 0f))))
        {
            if (range is not null)
            {
                gamba.RollRangeMultipliers.Remove(range);
                persistence.SaveNow();
            }
            pendingRangeMultiplierDeleteId = null;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(100f, 0f))))
        {
            pendingRangeMultiplierDeleteId = null;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }

        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }


    private void DrawDeleteVenueConfirmation()
    {
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Confirm delete venue",
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
            return;

        var venueName = pendingVenueDeleteIndex >= 0 && pendingVenueDeleteIndex < config.Venues.Count
            ? config.Venues[pendingVenueDeleteIndex].Name
            : "this venue";
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(380f));
        ImGui.TextUnformatted($"Delete venue profile '{venueName}'? This removes that venue's menu, buyout settings, jackpot settings, and gamba rules from BarManager.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Delete venue", AirTablet.UI.TabletAppTheme.Px(new Vector2(130f, 0f))))
        {
            if (config.Venues.Count > 1 && pendingVenueDeleteIndex >= 0 && pendingVenueDeleteIndex < config.Venues.Count)
            {
                config.Venues.RemoveAt(pendingVenueDeleteIndex);
                config.ActiveVenueId = config.Venues[0].Id;
                selectedVenue = 0;
                config.CurrentAudit = new BarAuditState { JackpotCurrent = config.ActiveVenue.JackpotBase };
                persistence.SaveNow();
            }
            pendingVenueDeleteIndex = -1;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(100f, 0f))))
        {
            pendingVenueDeleteIndex = -1;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }

        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }

    private void DrawDeleteDrinkConfirmation(VenueProfile venue)
    {
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Confirm delete drink",
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
            return;

        var drinkName = pendingDrinkDeleteIndex >= 0 && pendingDrinkDeleteIndex < venue.Drinks.Count
            ? venue.Drinks[pendingDrinkDeleteIndex].Name
            : "this drink";
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(360f));
        ImGui.TextUnformatted($"Delete '{drinkName}' from this venue's drink menu?");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Delete drink", AirTablet.UI.TabletAppTheme.Px(new Vector2(125f, 0f))))
        {
            if (pendingDrinkDeleteIndex >= 0 && pendingDrinkDeleteIndex < venue.Drinks.Count)
            {
                venue.Drinks.RemoveAt(pendingDrinkDeleteIndex);
                persistence.SaveNow();
            }
            pendingDrinkDeleteIndex = -1;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(100f, 0f))))
        {
            pendingDrinkDeleteIndex = -1;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }

        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }

    private void DrawClearRulesConfirmation(GambaSettings gamba)
    {
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Confirm clear gamba rules",
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(380f));
        ImGui.TextUnformatted("Clear all gamba payout rules for this venue? This cannot be undone unless you previously exported the gamba settings or venue profile.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Clear rules", AirTablet.UI.TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            gamba.Rules.Clear();
            persistence.SaveNow();
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(100f, 0f))))
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();

        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "BarManager failed to open folder.");
        }
    }


    private static string FormatNullableTurns(int? turns) => turns.HasValue && turns.Value > 0 ? turns.Value.ToString() : string.Empty;

    private static int? ParseNullableTurns(string value, int? fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value.Trim(), out var parsed) ? Math.Clamp(parsed, 1, 999) : fallback;
    }

    private static string NormalizeJackpotChannel(string value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();
        return normalized switch
        {
            "s" or "say" => "say",
            "sh" or "shout" => "shout",
            "y" or "yell" => "yell",
            _ => "yell",
        };
    }

    private static List<int> ParseInts(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var n) ? n : (int?)null).Where(x => x.HasValue).Select(x => x!.Value).ToList();
    private static List<string> ParseStrings(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
}
