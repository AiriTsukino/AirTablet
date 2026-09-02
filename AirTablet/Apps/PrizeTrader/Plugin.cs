using System.Globalization;
using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace PrizeTrader;

internal sealed class Plugin : IDisposable
{
    private const string PrizeTraderVersion = "1.0.22.0";
    private const string EarlyAccessWarningModal = "PrizeTrader early access##PrizeTrader";
    private readonly Configuration config;
    private readonly TradeSequenceService trades;
    private long amount;
    private bool confirmStart;
    private bool settingsVisible;
    private nint observedTargetAddress;
    private bool targetObservationInitialized;
    private bool earlyAccessWarningOpened;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        amount = Math.Max(1, config.LastAmount);
        settingsVisible = config.SettingsVisible;
        trades = new TradeSequenceService(() => config.AutoAcceptIncomingTrades);
    }

    public void Tick()
    {
        ObserveTargetChange();
        trades.Tick();
    }
    public string? ConsumeNotification() => trades.ConsumeNotification();

    public void Draw()
    {
        if (settingsVisible)
        {
            DrawSettings();
            DrawEarlyAccessWarning();
            return;
        }

        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTable("##prizetrader-layout", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Setup", ImGuiTableColumnFlags.WidthStretch, 0.54f);
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthStretch, 0.46f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawSetup();
            ImGui.TableNextColumn();
            DrawProgress();
            ImGui.EndTable();
        }

        DrawConfirmation();
        DrawEarlyAccessWarning();
    }

    private void DrawSetup()
    {
        DrawCard("Payout setup", () =>
        {
                TextColoredWrapped(TabletAppTheme.MutedText, "Locked recipient");
            ImGui.TextWrapped(trades.LockedDisplay ?? "No target locked");
            if (!trades.IsBusy)
            {
                if (ImGui.Button("Lock Current Target", new Vector2(-1f, 0f)))
                    trades.LockCurrentTarget();

                TextColoredWrapped(TabletAppTheme.MutedText, "Total gil to trade");
                ImGui.SetNextItemWidth(-1f);
                long step = 1;
                long fastStep = 1_000_000;
                var amountChanged = ImGui.InputScalar("##prizetrader-total", ImGuiDataType.S64, ref amount, in step, in fastStep, "%d");
                amount = Math.Max(1, amount);
                if (amountChanged)
                    trades.ClearDisplayedProgress("The payout amount changed. Previous trade progress was cleared.");
                var chunks = (amount + TradeSequenceService.MaximumChunk - 1) / TradeSequenceService.MaximumChunk;
                ImGui.TextWrapped($"Total Trades: {chunks.ToString("N0", CultureInfo.InvariantCulture)}  |  Gil Amount Entered: {amount.ToString("N0", CultureInfo.InvariantCulture)}");

                var disabled = !trades.HasLockedTarget || amount <= 0;
                if (disabled) ImGui.BeginDisabled();
                if (ImGui.Button("Start Payout", new Vector2(-1f, 0f)))
                {
                    confirmStart = true;
                    TabletAppTheme.OpenCenteredModal("Confirm PrizeTrader payout");
                }
                if (disabled) ImGui.EndDisabled();
            }
            else if (trades.IsRunning)
            {
                if (trades.NeedsRetry && ImGui.Button("Retry Current Trade", new Vector2(-1f, 0f))) trades.RetryCurrentTrade();
                if (ImGui.Button("Cancel Payout", new Vector2(-1f, 0f))) trades.Cancel("Cancelled by the operator.");
            }
            else
            {
                TextColoredWrapped(TabletAppTheme.MutedText,
                    "PrizeTrader is handling an incoming trade. Outgoing payout controls will return after the trusted Trade complete message.");
            }
        });
    }

    public bool CanNavigateBack() => settingsVisible;

    public bool NavigateBack()
    {
        if (!settingsVisible) return false;
        settingsVisible = false;
        config.SettingsVisible = false;
        SaveConfig();
        return true;
    }

    private void DrawHeader()
    {
        if (!ImGui.BeginTable("##prizetrader-header", 2,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings)) return;
        ImGui.TableSetupColumn("Spacer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(90f));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Dummy(Vector2.Zero);
        ImGui.TableNextColumn();
        if (ImGui.Button("Settings", new Vector2(-1f, 0f)))
        {
            settingsVisible = true;
            config.SettingsVisible = true;
            SaveConfig();
        }
        ImGui.EndTable();
    }

    private void DrawSettings()
    {
        ImGui.TextColored(TabletAppTheme.AccentHover, "PrizeTrader Settings");
        ImGui.Separator();
        if (!ImGui.BeginTabBar("##prizetrader-settings-tabs"))
            return;
        if (ImGui.BeginTabItem("General"))
        {
            DrawGeneralSettings();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Debug Log"))
        {
            DrawDebugLog();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawGeneralSettings()
    {
        var autoAccept = config.AutoAcceptIncomingTrades;
        if (ImGui.Checkbox("Automatically accept incoming trades", ref autoAccept))
        {
            config.AutoAcceptIncomingTrades = autoAccept;
            trades.OnIncomingAutoAcceptSettingChanged();
            SaveConfig();
        }
        TextColoredWrapped(TabletAppTheme.MutedText,
            "Off by default. When enabled, PrizeTrader accepts incoming trade requests, readies your empty side, confirms Yes once, and waits for the trusted Trade complete system message. It never adds gil or items to incoming trades.");
        TextColoredWrapped(new Vector4(1f, 0.72f, 0.30f, 1f),
            "This accepts trades from any player who sends you a request while the setting is enabled.");
    }

    private void DrawDebugLog()
    {
        ImGui.TextColored(TabletAppTheme.AccentHover, "PrizeTrader diagnostics");
        TextColoredWrapped(TabletAppTheme.MutedText,
            "Use this session log when reporting trade problems. Click inside the log to select specific text and press Ctrl+C, or copy the entire log with the button below.");
        ImGui.Spacing();

        var logText = trades.DebugLogText;
        if (ImGui.Button("Copy All", TabletAppTheme.Px(new Vector2(105f, 30f))))
            ImGui.SetClipboardText(logText);
        ImGui.SameLine();
        if (ImGui.Button("Clear", TabletAppTheme.Px(new Vector2(90f, 30f))))
            trades.ClearDebugLog();
        ImGui.SameLine();
        ImGui.TextDisabled($"{trades.DebugLogLineCount:N0} line(s)");

        ImGui.Spacing();
        logText = trades.DebugLogText;
        var height = MathF.Max(TabletAppTheme.Px(220f), ImGui.GetContentRegionAvail().Y);
        ImGui.InputTextMultiline(
            "##prizetrader-copyable-debug-log",
            ref logText,
            Math.Max(logText.Length + 1024, 4096),
            new Vector2(-1f, height),
            ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
    }

    private void DrawEarlyAccessWarning()
    {
        if (config.LastAcknowledgedEarlyAccessVersion.Equals(PrizeTraderVersion, StringComparison.OrdinalIgnoreCase))
        {
            earlyAccessWarningOpened = false;
            return;
        }
        if (!earlyAccessWarningOpened)
        {
            earlyAccessWarningOpened = true;
            TabletAppTheme.OpenCenteredModal(EarlyAccessWarningModal);
        }
        if (!TabletAppTheme.BeginCenteredModal(EarlyAccessWarningModal,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
            return;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(420f));
        ImGui.TextWrapped("PrizeTrader is in early access and may not be fully functional yet. Carefully verify every recipient, gil amount, and completed trade while testing, and report any problems with the Debug Log from Settings.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("Acknowledge", TabletAppTheme.Px(new Vector2(150f, 0f))))
        {
            config.LastAcknowledgedEarlyAccessVersion = PrizeTraderVersion;
            SaveConfig();
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private void ObserveTargetChange()
    {
        var address = DalamudServices.TargetManager.Target?.Address ?? nint.Zero;
        if (!targetObservationInitialized)
        {
            observedTargetAddress = address;
            targetObservationInitialized = true;
            return;
        }
        if (address == observedTargetAddress) return;
        observedTargetAddress = address;
        trades.ClearDisplayedProgress("The current target changed or was lost. Previous trade progress was cleared.");
    }

    private void SaveConfig() => DalamudServices.PluginInterface.SavePluginConfig(config);

    private void DrawProgress()
    {
        DrawCard("Trade progress", () =>
        {
            ImGui.TextWrapped(trades.Status);
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 5f)));
            ImGui.TextWrapped($"Confirmed: {trades.ConfirmedAmount:N0} gil");
            ImGui.TextWrapped($"Remaining: {trades.RemainingAmount:N0} gil");
            ImGui.TextWrapped($"Current trade: {trades.CurrentChunk:N0} gil");
            var fraction = trades.TotalAmount <= 0 ? 0f : (float)((double)trades.ConfirmedAmount / trades.TotalAmount);
            ImGui.ProgressBar(Math.Clamp(fraction, 0f, 1f), new Vector2(-1f, 0f), $"{fraction:P0}");
            ImGui.Spacing();
            TextColoredWrapped(TabletAppTheme.MutedText, "The recipient may take as long as needed. PrizeTrader does not treat a closed or unanswered trade as payment.");
        });
    }

    private void DrawConfirmation()
    {
        if (!confirmStart) return;
        if (!TabletAppTheme.BeginCenteredModal("Confirm PrizeTrader payout"))
            return;
        ImGui.TextWrapped($"Trade {amount.ToString("N0", CultureInfo.InvariantCulture)} gil to {trades.LockedDisplay}? The payout will use normal trades capped at 1,000,000 gil each.");
        ImGui.Spacing();
        if (ImGui.Button("Begin", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            config.LastAmount = amount;
            DalamudServices.PluginInterface.SavePluginConfig(config);
            trades.Start(amount);
            confirmStart = false;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(110f, 0f))))
        {
            confirmStart = false;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private static void DrawCard(string title, Action content)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TabletAppTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.48f));
        ImGui.BeginChild($"##prizetrader-card-{title}", new Vector2(-1f, 0f), true, ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.TextColored(TabletAppTheme.AccentHover, title);
        ImGui.Separator();
        content();
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private static void TextColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    public void Dispose() => trades.Dispose();
}
