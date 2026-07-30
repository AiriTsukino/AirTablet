using System.Numerics;
using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs.SettingsTabs;

public sealed class ChatTemplateSettingsTab
{
    private readonly ProfileService profiles;
    private readonly BlackjackSession session;
    private int selectedTemplate;
    private string newTemplateName = string.Empty;
    private string renameBuffer = string.Empty;
    private Guid renameTemplateId;
    private bool showVariables;
    private string lastMessage = string.Empty;

    public ChatTemplateSettingsTab(ProfileService profiles, BlackjackSession session)
    {
        this.profiles = profiles;
        this.session = session;
    }

    public void Draw()
    {
        var profile = profiles.ActiveProfile;
        var templates = profiles.GetTemplateSets(profile).ToList();
        selectedTemplate = Math.Clamp(selectedTemplate, 0, Math.Max(templates.Count - 1, 0));
        var selected = templates.Count > 0 ? templates[selectedTemplate] : profiles.GetActiveTemplateSet(profile);

        UiHelpers.InfoBox("Venue Chat Templates", "Each venue has its own chat template library. Assign one template set to the active venue, then edit, clone, create, or remove template sets while the table is idle.");

        UiHelpers.Card("Active Venue Assignment", () =>
        {
            ImGui.TextColored(GambaTheme.Gold, profile.Name);
            ImGui.TextDisabled($"Assigned template: {profiles.GetActiveTemplateSet(profile).Name}");

            if (ImGui.Button("Chat Template Variables"))
                showVariables = true;

            if (templates.Count == 0)
                return;

            var names = templates.Select(t => t.Name).ToArray();
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260f));
            ImGui.Combo("Template set", ref selectedTemplate, names, names.Length);
            selected = templates[Math.Clamp(selectedTemplate, 0, templates.Count - 1)];

            ImGui.SameLine();
            if (session.IsActive) ImGui.BeginDisabled();
            if (ImGui.Button("Assign To Venue"))
            {
                if (profiles.TryAssignTemplate(profile, selected.Id, session, out var reason))
                    lastMessage = $"Assigned {selected.Name} to {profile.Name}.";
                else
                    lastMessage = reason;
            }
            if (session.IsActive) ImGui.EndDisabled();

            if (session.IsActive)
                ImGui.TextDisabled("Template assignment and management are locked during an active session.");
        });

        DrawNaturalBlackjackBroadcastSettings(profile);
        DrawTemplateManagement(profile, selected);
        DrawTemplateEditor(profile, selected);
        DrawVariablesWindow();

        if (!string.IsNullOrWhiteSpace(lastMessage))
            ImGui.TextDisabled(lastMessage);
    }

    private void DrawNaturalBlackjackBroadcastSettings(VenueProfile profile)
    {
        UiHelpers.Card("Natural Blackjack Broadcast", () =>
        {
            ImGui.TextDisabled("Choose where Natural Blackjack announcements are sent. Other Blackjack table messages continue using the normal party broadcast behavior.");

            var channels = new[] { "/party", "/say", "/shout", "/yell" };
            var current = NormalizeChatChannel(profile.NaturalBlackjackChatChannel);
            var selectedChannel = Array.FindIndex(channels, c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));
            if (selectedChannel < 0)
                selectedChannel = 0;

            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.Combo("Natural Blackjack channel", ref selectedChannel, channels, channels.Length))
            {
                profiles.UpdateNaturalBlackjackChatChannel(profile, channels[selectedChannel]);
                lastMessage = $"Natural Blackjack announcements will use {channels[selectedChannel]}.";
            }
            UiHelpers.Tooltip("Sets the chat channel used only for Natural Blackjack announcements.");
        });
    }

    private static string NormalizeChatChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "/say" or "say" or "/s" or "s" => "/say",
        "/shout" or "shout" or "/sh" or "sh" => "/shout",
        "/yell" or "yell" or "/y" or "y" => "/yell",
        "/party" or "party" or "/p" or "p" => "/party",
        _ => "/party",
    };

    private void DrawTemplateManagement(VenueProfile profile, ChatTemplateSet selected)
    {
        UiHelpers.Card("Manage Chat Templates", () =>
        {
            if (session.IsActive) ImGui.BeginDisabled();

            ImGui.SetNextItemWidth(MathF.Min(
                AirTablet.UI.TabletAppTheme.Px(260f),
                MathF.Max(AirTablet.UI.TabletAppTheme.Px(150f), ImGui.GetContentRegionAvail().X)));
            ImGui.InputText("New template name", ref newTemplateName, 80);

            if (ImGui.Button("Create"))
            {
                var created = profiles.CreateTemplate(profile, newTemplateName);
                selectedTemplate = profiles.GetTemplateSets(profile).ToList().FindIndex(t => t.Id == created.Id);
                newTemplateName = string.Empty;
                lastMessage = $"Created chat template {created.Name}.";
            }

            DrawSameLineIfFits("Clone Selected");
            if (ImGui.Button("Clone Selected"))
            {
                var clone = profiles.CloneTemplate(profile, selected.Id);
                selectedTemplate = profiles.GetTemplateSets(profile).ToList().FindIndex(t => t.Id == clone.Id);
                lastMessage = $"Cloned {selected.Name} to {clone.Name}.";
            }

            DrawSameLineIfFits("Remove Selected");
            if (profiles.GetTemplateSets(profile).Count <= 1) ImGui.BeginDisabled();
            if (UiHelpers.ConfirmingButton("Remove Selected", $"Remove Template##{selected.Id}", $"Remove chat template '{selected.Name}'?"))
            {
                if (profiles.TryDeleteTemplate(profile, selected.Id, session, out var reason))
                {
                    selectedTemplate = Math.Clamp(selectedTemplate, 0, profiles.GetTemplateSets(profile).Count - 1);
                    lastMessage = "Chat template removed.";
                }
                else
                {
                    lastMessage = reason;
                }
            }
            if (profiles.GetTemplateSets(profile).Count <= 1) ImGui.EndDisabled();

            ImGui.Separator();
            if (renameTemplateId != selected.Id)
            {
                if (ImGui.Button("Rename Selected"))
                {
                    renameTemplateId = selected.Id;
                    renameBuffer = selected.Name;
                }
            }
            else
            {
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260f));
                ImGui.InputText("##renameTemplate", ref renameBuffer, 80);
                ImGui.SameLine();
                if (ImGui.Button("Apply Rename"))
                {
                    profiles.RenameTemplate(profile, selected.Id, renameBuffer);
                    renameTemplateId = Guid.Empty;
                    lastMessage = "Chat template renamed.";
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel Rename"))
                    renameTemplateId = Guid.Empty;
            }

            if (session.IsActive) ImGui.EndDisabled();
        });
    }

    private static void DrawSameLineIfFits(string nextLabel)
    {
        var style = ImGui.GetStyle();
        var nextWidth = ImGui.CalcTextSize(nextLabel).X + style.FramePadding.X * 2f;
        var rightEdgeIfSameLine = ImGui.GetItemRectMax().X + style.ItemSpacing.X + nextWidth;
        var contentRight = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (rightEdgeIfSameLine <= contentRight)
            ImGui.SameLine();
    }

    private static void DrawDisabledWrapped(string text)
    {
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX()
            + MathF.Max(AirTablet.UI.TabletAppTheme.Px(160f), ImGui.GetContentRegionAvail().X));
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    private void DrawTemplateEditor(VenueProfile profile, ChatTemplateSet selected)
    {
        UiHelpers.Card($"Edit Template: {selected.Name}", () =>
        {
            EnsureRequiredTemplates(selected);
            if (session.IsActive) ImGui.BeginDisabled();

            foreach (var key in ChatTemplateDefaults.RequiredKeys)
            {
                var value = selected.Templates.TryGetValue(key, out var existing) ? existing : string.Empty;
                ImGui.PushID($"{selected.Id}-{key}");
                ImGui.TextColored(GambaTheme.Gold, DisplayNameForKey(key));
                DrawDisabledWrapped(DescriptionForKey(key));
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1f));
                if (ImGui.InputTextMultiline("##template", ref value, 700, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1f, 58f))))
                {
                    profiles.UpdateTemplateValue(profile, selected.Id, key, value);
                    lastMessage = selected.Id == profile.ActiveChatTemplateId
                        ? "Template saved automatically and applied to the active venue."
                        : "Template saved automatically. Use Assign To Venue when ready.";
                }
                ImGui.PopID();
            }

            if (session.IsActive) ImGui.EndDisabled();
        });
    }

    private void DrawVariablesWindow()
    {
        if (!showVariables)
            return;

        ImGui.SetNextWindowSize(AirTablet.UI.TabletAppTheme.Px(new Vector2(560f, 420f)), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Chat Template Variables###GambaChatTemplateVariables", ref showVariables, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        UiHelpers.InfoBox("Variables", "Variables are replaced when GambaAssistant sends or logs Blackjack narration. Unsupported variables remain visible, so venues can spot typos quickly.");
        DrawVariable("{player}", "The player name shown as Name@World when available.");
        DrawVariable("{card}", "The newly drawn card, such as A, 7, K, or Q.");
        DrawVariable("{hand}", "The visible cards in the current hand, such as A + 7.");
        DrawVariable("{handLabel}", "The current hand label, usually Hand, or Hand 1 / Hand 2 during split hands.");
        DrawVariable("{total}", "The current Blackjack hand total, including soft totals when relevant.");
        DrawVariable("{bet}", "The active wager for the hand, formatted as a gil amount in chat.");
        DrawVariable("{bank}", "The player available bank after the current action or settlement.");
        DrawVariable("{dealer}", "The dealer final total or dealer comparison value during settlement.");
        DrawVariable("{outcome}", "The settlement result text, such as Win, Loss, Push, or Natural Blackjack payout.");
        DrawVariable("{payout}", "A payout amount when the message type supplies one.");
        DrawVariable("{options}", "The legal actions for the active hand, such as Hit, Stand, Double Down, Split, or an action with the additional gil needed. Only actions currently legal by the rules are listed.");
        DrawVariable("{amount}", "The formatted gil amount for bet announcements, such as 10,000.");

        ImGui.End();
    }

    private static void DrawVariable(string variable, string description)
    {
        ImGui.TextColored(GambaTheme.Gold, variable);
        ImGui.SameLine(AirTablet.UI.TabletAppTheme.Px(120f));
        ImGui.TextWrapped(description);
    }

    private static void EnsureRequiredTemplates(ChatTemplateSet set)
    {
        var defaults = ChatTemplateDefaults.CreateFormal();
        foreach (var key in ChatTemplateDefaults.RequiredKeys)
        {
            if (!set.Templates.ContainsKey(key))
                set.Templates[key] = defaults[key];
        }
    }

    private static string DisplayNameForKey(string key) => key switch
    {
        "CardReceived" => "Card Received",
        "NaturalBlackjack" => "Natural Blackjack",
        "Bust" => "Bust",
        "Settlement" => "Settlement",
        "DealerDraw" => "Dealer Draw",
        "AllPlayersBust" => "All Players Bust",
        "PlayerTurnOptions" => "Player Turn Options",
        "BetPlaced" => "Bet Placed",
        _ => key,
    };

    private static string DescriptionForKey(string key) => key switch
    {
        "CardReceived" => "Used when a player or dealer receives a card.",
        "NaturalBlackjack" => "Used when an initial two-card natural Blackjack is detected.",
        "Bust" => "Used when a player hand busts and the bet resolves.",
        "Settlement" => "Used during final round settlement for each unresolved hand.",
        "DealerDraw" => "Used when the dealer draws or reveals a card.",
        "AllPlayersBust" => "Used when no active player hands remain and the dealer hand is skipped.",
        "PlayerTurnOptions" => "Used when a specific player needs to choose their next legal action.",
        "BetPlaced" => "Used when the dealer confirms/reserves a player's bet during betting.",
        _ => string.Empty,
    };
}
