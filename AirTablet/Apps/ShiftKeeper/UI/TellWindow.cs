using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ShiftKeeper.Models;
using ShiftKeeper.Services;
using ShiftKeeper.UI.Components;

namespace ShiftKeeper.UI;

public sealed class TellWindow : Window, IDisposable
{
    private readonly ChatCommandService chat;
    private readonly CancellationTokenSource lifetime = new();
    private StaffMember? recipient;
    private string message = string.Empty;
    private string status = string.Empty;
    private bool sending;

    public TellWindow(ChatCommandService chat)
        : base("Send Staff Tell###ShiftKeeperTellWindow", ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.chat = chat;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(460, 250)),
            MaximumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(760, 520)),
        };
    }

    public void OpenFor(StaffMember member)
    {
        recipient = member;
        message = string.Empty;
        status = string.Empty;
        IsOpen = true;
    }

    public override void PreDraw() => ShiftKeeperTheme.Push();
    public override void PostDraw() => ShiftKeeperTheme.Pop();

    public override void Draw()
    {
        DrawContents(false);
    }

    public void DrawEmbeddedPopup()
    {
        if (!IsOpen)
            return;

        const string popupName =
            "Send Staff Tell##shift-keeper-tell-popup";
        AirTablet.UI.TabletAppTheme.OpenCenteredModal(popupName);
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                popupName,
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        DrawContents(true);
        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }

    private void DrawContents(bool embeddedPopup)
    {
        if (recipient is null)
        {
            ImGui.TextWrapped("No staff member is selected.");
            if (ImGui.Button(
                    "Close",
                    AirTablet.UI.TabletAppTheme.Px(
                        new Vector2(100f, 0f))))
            {
                Close(embeddedPopup);
            }
            return;
        }

        ImGui.TextDisabled("Recipient");
        ImGui.TextWrapped(recipient.TellRecipient);
        ImGui.Spacing();
        ImGui.TextDisabled("Message");
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1));
        ImGui.InputTextMultiline("##shift-keeper-tell-message", ref message, 450, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, 96)));
        var enterPressed = ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Enter);
        ImGui.TextDisabled($"{message.Length}/450 characters. Press Enter or Send to deliver the tell.");
        UiHelpers.Help("ShiftKeeper sends this through the game's native chat command system as /tell Firstname Lastname@World message. Line breaks are converted to spaces.");

        if (!string.IsNullOrWhiteSpace(status))
            UiHelpers.Status(status, status.StartsWith("Sent", StringComparison.Ordinal) ? ShiftKeeperTheme.Green : ShiftKeeperTheme.Amber);
        else
            ImGui.Dummy(
                new Vector2(
                    0f,
                    ImGui.GetTextLineHeight()));

        var canSend = !sending && !string.IsNullOrWhiteSpace(message);
        if (!canSend) ImGui.BeginDisabled();
        var sendRequested =
            ImGui.Button(
                "Send Tell",
                AirTablet.UI.TabletAppTheme.Px(
                    new Vector2(120, 0)))
            || enterPressed && canSend;
        if (!canSend) ImGui.EndDisabled();
        if (sendRequested && canSend)
            SendAndClose(recipient.TellRecipient, embeddedPopup);
        ImGui.SameLine();
        if (ImGui.Button(
                "Cancel",
                AirTablet.UI.TabletAppTheme.Px(
                    new Vector2(100, 0))))
        {
            Close(embeddedPopup);
        }
    }

    private void SendAndClose(string recipientText, bool embeddedPopup)
    {
        var text = message;
        message = string.Empty;
        Close(embeddedPopup);
        _ = SendAsync(recipientText, text);
    }

    private void Close(bool embeddedPopup)
    {
        IsOpen = false;
        if (embeddedPopup)
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
    }

    private async Task SendAsync(string recipientText, string text)
    {
        if (sending) return;
        sending = true;
        status = "Sending…";
        var sent = await chat.SendTellAsync(recipientText, text, lifetime.Token).ConfigureAwait(false);
        status = sent ? $"Sent to {recipientText}." : chat.LastError;
        if (sent) message = string.Empty;
        sending = false;
    }

    public void Dispose() => lifetime.Cancel();
}
