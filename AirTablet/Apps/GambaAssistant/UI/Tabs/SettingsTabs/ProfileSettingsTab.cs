using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Players;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs.SettingsTabs;

public sealed class ProfileSettingsTab
{
    private readonly ProfileService profiles;
    private readonly BlackjackSession session;
    private int selected;
    private string newProfileName = string.Empty;
    private string renameBuffer = string.Empty;
    private Guid renameProfileId;
    private string lastMessage = string.Empty;
    private string vipName = string.Empty;
    private string vipWorld = string.Empty;

    public ProfileSettingsTab(ProfileService profiles, BlackjackSession session)
    {
        this.profiles = profiles;
        this.session = session;
    }

    public void Draw()
    {
        UiHelpers.InfoBox("Profile Locking", "Venue profiles can be created, removed, renamed, or switched only while the table is idle or after a full session reset. Each venue owns its rules and chat templates.");

        DrawActiveProfileCard();
        DrawCreateProfileCard();
        DrawManageProfilesCard();

        if (!string.IsNullOrWhiteSpace(lastMessage))
            ImGui.TextDisabled(lastMessage);
    }

    private void DrawActiveProfileCard()
    {
        UiHelpers.Card("Active Profile", () =>
        {
            var names = profiles.Profiles.Select(p => p.Name).ToArray();
            if (names.Length == 0)
            {
                ImGui.TextDisabled("No profiles available.");
                return;
            }

            selected = profiles.Profiles.FindIndex(profile => profile.Id == profiles.ActiveProfile.Id);
            selected = Math.Clamp(selected, 0, names.Length - 1);
            if (session.IsActive) ImGui.BeginDisabled();
            if (ImGui.Combo("Active profile", ref selected, names, names.Length))
            {
                if (profiles.TrySwitchProfile(profiles.Profiles[selected].Id, session, out var reason))
                {
                    lastMessage = $"Switched to {profiles.ActiveProfile.Name}.";
                }
                else
                {
                    lastMessage = reason;
                }
            }
            if (session.IsActive) ImGui.EndDisabled();

            if (session.IsActive)
                ImGui.TextDisabled("Profile switching is locked while a session is active.");
        });
    }

    private void DrawCreateProfileCard()
    {
        UiHelpers.Card("Create Venue Profile", () =>
        {
            if (session.IsActive) ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260f));
            ImGui.InputText("Venue name", ref newProfileName, 80);
            ImGui.SameLine();
            if (ImGui.Button("Create Venue"))
            {
                if (profiles.TryCreateProfile(newProfileName, session, out var created, out var reason))
                {
                    newProfileName = string.Empty;
                    selected = profiles.Profiles.FindIndex(p => p.Id == created!.Id);
                    session.Rules = profiles.ActiveProfile.BlackjackRules;
                    lastMessage = $"Created and selected {created!.Name}.";
                }
                else
                {
                    lastMessage = reason;
                }
            }
            if (session.IsActive) ImGui.EndDisabled();
        });
    }

    private void DrawManageProfilesCard()
    {
        UiHelpers.Card("Manage Venues", () =>
        {
            foreach (var profile in profiles.Profiles.ToList())
            {
                ImGui.PushID(profile.Id.ToString());
                var active = profile.Id == profiles.ActiveProfile.Id;
                ImGui.TextColored(active ? GambaTheme.Gold : GambaTheme.Text, active ? $"{profile.Name} (active)" : profile.Name);
                ImGui.SameLine();

                if (renameProfileId != profile.Id)
                {
                    if (ImGui.Button("Rename"))
                    {
                        renameProfileId = profile.Id;
                        renameBuffer = profile.Name;
                    }
                }
                else
                {
                    ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220f));
                    ImGui.InputText("##rename", ref renameBuffer, 80);
                    ImGui.SameLine();
                    if (ImGui.Button("Apply"))
                    {
                        profiles.RenameProfile(profile, renameBuffer);
                        renameProfileId = Guid.Empty;
                        lastMessage = "Venue profile renamed.";
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                        renameProfileId = Guid.Empty;
                }

                ImGui.SameLine();
                if (session.IsActive || profiles.Profiles.Count <= 1) ImGui.BeginDisabled();
                if (UiHelpers.ConfirmingButton("Remove", $"Remove Venue##{profile.Id}", $"Remove venue profile '{profile.Name}'? This deletes its saved profile JSON."))
                {
                    if (profiles.TryDeleteProfile(profile.Id, session, out var reason))
                    {
                        selected = Math.Clamp(selected, 0, profiles.Profiles.Count - 1);
                        session.Rules = profiles.ActiveProfile.BlackjackRules;
                        lastMessage = "Venue profile removed.";
                    }
                    else
                    {
                        lastMessage = reason;
                    }
                }
                if (session.IsActive || profiles.Profiles.Count <= 1) ImGui.EndDisabled();
                ImGui.PopID();
            }
        });
    }

    public void DrawRules()
    {
        profiles.BindActiveProfileRules(session);
        var rules = profiles.ActiveProfile.BlackjackRules;
        var locked = session.IsActive;

        UiHelpers.InfoBox("Rules Lock", locked
            ? "Critical Blackjack rules are locked during an active session. Reset the night or finish the session before changing them."
            : "These rules apply to newly dealt hands and are saved with the active venue profile.");

        UiHelpers.Card("Limits", () =>
        {
            if (locked) ImGui.BeginDisabled();

            var min = rules.MinimumBet;
            if (UiHelpers.InputGil("Minimum bet", ref min))
            {
                rules.MinimumBet = min;
                SaveRules(rules);
            }

            var max = rules.MaximumBet;
            if (UiHelpers.InputGil("Maximum bet", ref max))
            {
                rules.MaximumBet = max;
                SaveRules(rules);
            }

            var vipMax = profiles.ActiveProfile.VipMaximumBet;
            if (UiHelpers.InputGil("VIP maximum bet", ref vipMax))
            {
                profiles.ActiveProfile.VipMaximumBet = Math.Max(rules.MaximumBet, vipMax);
                profiles.SaveProfile(profiles.ActiveProfile);
            }
            UiHelpers.Tooltip("Players on this venue profile's Blackjack VIP list may bet up to this amount. Everyone else remains limited by Maximum bet.");

            if (locked) ImGui.EndDisabled();
        });

        DrawBlackjackVipCard();

        UiHelpers.Card("Initial Deal", () =>
        {
            if (locked) ImGui.BeginDisabled();

            var dealMode = rules.InitialDealMode == BlackjackInitialDealMode.PlayerFullHandsThenDealer ? 1 : 0;
            var dealModeLabels = new[]
            {
                "Round-robin: player card 1s, dealer visible card, player card 2s",
                "Full player hands first: each player gets 2 cards, dealer visible card last"
            };

            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(420f));
            if (ImGui.Combo("Initial dealing mode", ref dealMode, dealModeLabels, dealModeLabels.Length))
            {
                rules.InitialDealMode = dealMode == 1
                    ? BlackjackInitialDealMode.PlayerFullHandsThenDealer
                    : BlackjackInitialDealMode.RoundRobin;
                SaveRules(rules);
            }
            UiHelpers.Tooltip("Full player hands first announces each player's starting hand only after both starting cards are rolled.");

            if (locked) ImGui.EndDisabled();
        });

        UiHelpers.Card("Actions / Dealer", () =>
        {
            if (locked) ImGui.BeginDisabled();

            var soft17 = rules.DealerStandsOnSoft17;
            if (ImGui.Checkbox("Dealer stands on all 17s including soft 17", ref soft17))
            {
                rules.DealerStandsOnSoft17 = soft17;
                SaveRules(rules);
            }

            var pushOnTie = rules.PushOnTie;
            if (ImGui.Checkbox("Push player bet on tie", ref pushOnTie))
            {
                rules.PushOnTie = pushOnTie;
                SaveRules(rules);
            }

            ImGui.Separator();

            var split = rules.SplittingEnabled;
            if (ImGui.Checkbox("Splitting enabled", ref split))
            {
                rules.SplittingEnabled = split;
                SaveRules(rules);
            }

            var resplit = rules.ResplitPairs;
            if (ImGui.Checkbox("Allow split hands to be split again", ref resplit))
            {
                rules.ResplitPairs = resplit;
                SaveRules(rules);
            }
            UiHelpers.Tooltip("Off by default: a single hand can only be split once, so split hands cannot be split again.");

            var maxSplitHands = rules.MaxSplitHands;
            if (ImGui.SliderInt("Maximum hands after splits", ref maxSplitHands, 2, 4))
            {
                rules.MaxSplitHands = Math.Clamp(maxSplitHands, 2, 4);
                SaveRules(rules);
            }

            var doubleAfterSplit = rules.DoubleAfterSplit;
            if (ImGui.Checkbox("Allow Double Down on split hands", ref doubleAfterSplit))
            {
                rules.DoubleAfterSplit = doubleAfterSplit;
                SaveRules(rules);
            }

            var splitNatural = rules.SplitTwentyOneCountsAsNatural;
            if (ImGui.Checkbox("21 on split hand counts as Natural Blackjack", ref splitNatural))
            {
                rules.SplitTwentyOneCountsAsNatural = splitNatural;
                SaveRules(rules);
            }
            UiHelpers.Tooltip("Off by default: a 21 after a split is treated as normal 21, not Natural Blackjack.");

            if (locked) ImGui.EndDisabled();
        });

        UiHelpers.Card("Winnings / Payouts", () =>
        {
            if (locked) ImGui.BeginDisabled();

            var changed = false;

            var standardWinMultiplier = rules.StandardWinTotalMultiplier;
            if (DrawMultiplier("Standard win total return", ref standardWinMultiplier, "Default 2.00x: 10,000 bet returns 20,000 total."))
            {
                rules.StandardWinTotalMultiplier = standardWinMultiplier;
                changed = true;
            }

            var doubleDownMultiplier = rules.DoubleDownWinTotalMultiplier;
            if (DrawMultiplier("Double Down win total return", ref doubleDownMultiplier, "Default 3.00x original bet: 10,000 original bet returns 30,000 total after doubling."))
            {
                rules.DoubleDownWinTotalMultiplier = doubleDownMultiplier;
                changed = true;
            }

            var naturalMultiplier = rules.NaturalBlackjackTotalMultiplier;
            if (DrawMultiplier("Natural Blackjack total return", ref naturalMultiplier, "Default 3.50x: 10,000 bet returns 35,000 total."))
            {
                rules.NaturalBlackjackTotalMultiplier = naturalMultiplier;
                changed = true;
            }

            if (changed)
                SaveRules(rules);

            ImGui.TextDisabled($"Examples at 10,000 gil: standard {rules.StandardWinReturn(10_000):N0}, double down {rules.DoubleDownWinReturn(10_000):N0}, natural {rules.NaturalBlackjackReturn(10_000):N0}.");

            if (locked) ImGui.EndDisabled();
        });
    }

    private void DrawBlackjackVipCard()
    {
        var profile = profiles.ActiveProfile;
        UiHelpers.Card("Blackjack VIP Limits", () =>
        {
            ImGui.TextWrapped($"VIPs may bet up to {Math.Max(profile.BlackjackRules.MaximumBet, profile.VipMaximumBet):N0} gil on the {profile.Name} venue profile. The higher limit is applied only when the matching player is in the current party.");
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(240f));
            ImGui.InputTextWithHint("##blackjack-vip-name", "Character name", ref vipName, 64);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(180f));
            ImGui.InputTextWithHint("##blackjack-vip-world", "Home world", ref vipWorld, 64);
            ImGui.SameLine();
            if (ImGui.Button("Add VIP"))
                AddVip(new PlayerIdentity(vipName, vipWorld));
            ImGui.SameLine();
            if (ImGui.Button("Add Target"))
            {
                if (DalamudServices.TargetManager.Target is IPlayerCharacter player)
                {
                    var world = string.Empty;
                    try { world = player.HomeWorld.Value.Name.ExtractText(); }
                    catch { world = string.Empty; }
                    AddVip(new PlayerIdentity(player.Name.TextValue, world));
                }
                else
                {
                    lastMessage = "Target a player before adding a Blackjack VIP.";
                }
            }

            ImGui.Separator();
            if (profile.BlackjackVips.Count == 0)
            {
                ImGui.TextDisabled("No Blackjack VIPs saved for this venue.");
                return;
            }

            foreach (var vip in profile.BlackjackVips.ToList())
            {
                ImGui.PushID($"blackjack-vip-{vip.Display}");
                ImGui.TextUnformatted(vip.Display);
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    profiles.RemoveBlackjackVip(profile, vip);
                    lastMessage = $"Removed {vip.Display} from the Blackjack VIP list.";
                }
                ImGui.PopID();
            }
        });
    }

    private void AddVip(PlayerIdentity identity)
    {
        if (profiles.AddBlackjackVip(profiles.ActiveProfile, identity))
        {
            vipName = string.Empty;
            vipWorld = string.Empty;
            lastMessage = $"Added {identity.Display} to the Blackjack VIP list.";
        }
        else
        {
            lastMessage = string.IsNullOrWhiteSpace(identity.Name)
                ? "Enter a character name first."
                : $"{identity.Display} is already on this venue's Blackjack VIP list.";
        }
    }


    private void SaveRules(BlackjackRules rules)
    {
        profiles.ActiveProfile.BlackjackRules = rules;
        profiles.SaveProfile(profiles.ActiveProfile);
    }

    private static bool DrawMultiplier(string label, ref decimal value, string tooltip)
    {
        var asFloat = (float)value;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(130f));
        var changed = ImGui.InputFloat(label, ref asFloat, 0.1f, 0.5f, "%.2fx");
        if (changed)
            value = Math.Clamp((decimal)asFloat, 0m, 20m);
        UiHelpers.Tooltip(tooltip);
        return changed;
    }

}
