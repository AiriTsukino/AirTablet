using System.Numerics;
using AirTablet.Models;
using AirTablet.Services;
using Dalamud.Bindings.ImGui;

namespace AirTablet.UI;

internal sealed class TabletWindow
{
    private const string ReleaseVersion = "1.0.30.1";
    private const double ScreenTransitionSeconds = 0.20;
    private const double StartupAnimationSeconds = 4.0;
    private const string DiscordInviteUrl = "https://discord.com/invite/HqyDz3SRbG";
    private const string KofiUrl = "https://ko-fi.com/airitsukino";
    private const float StatusBarHeight = 28f;
    private const float HomeGestureAreaHeight = 16f;
    private const float SmallTabletScale = 0.96f;
    private const float LargeTabletScale = 1.44f;
    private const float AppContentScale = 1.00f;

    private enum Screen
    {
        Home,
        Welcome,
        Settings,
        Wiki,
        Module,
        Feedback,
    }

    private enum SettingsPage
    {
        Menu,
        General,
        Appearance,
        Apps,
        WhatsNew,
        StatusBar,
        Migration,
        About,
    }

    private enum TransitionPhase
    {
        None,
        Opening,
        Closing,
    }

    private enum TutorialStep
    {
        None,
        Home,
        ControlCenterOpen,
        ControlCenterClose,
        LockPosition,
        Minimize,
        Restore,
    }

    private static readonly Vector2 DisplaySize = new(960, 540);
    private static readonly Vector2 BezelPadding = new(22, 18);
    private static readonly Vector2 FullOuterSize = new(1034, 590);
    private static readonly Vector2 MiniOuterSize = new(186, 128);
    private readonly Configuration config;
    private readonly CatalogService catalog;
    private readonly ChangelogService changelog;
    private readonly WikiService wiki;
    private readonly TextureCache textures;
    private readonly FileDialogService dialogs;
    private readonly AppHostService appHost;
    private readonly Action save;
    private readonly Action saveImmediate;
    private readonly string[] supporters;
    private readonly HashSet<string> setupSelectedApps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> appTileScales =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> controlHoverAmounts =
        new(StringComparer.OrdinalIgnoreCase);
    private Screen screen;
    private SettingsPage settingsPage;
    private string activeModuleId = string.Empty;
    private string draggedAppId = string.Empty;
    private string selectedWikiArticleId = "airtabos";
    private string wikiSearch = string.Empty;
    private string previousWikiSearch = string.Empty;
    private int selectedWikiMatch;
    private bool wikiJumpPending;
    private bool wikiArticleScrollResetPending;
    private bool wikiArticleJumpRepeatPending;
    private int welcomePage;
    private string notice = string.Empty;
    private DateTime noticeUntil;
    private double noticeStartedAt;
    private string migrationStatus = string.Empty;
    private float uiScale = 1f;
    private TransitionPhase transitionPhase;
    private double transitionStartedAt;
    private TutorialStep tutorialStep;
    private bool startupAnimationPending;
    private double startupAnimationStartedAt = -1d;
    private bool recoveryRequested;
    private bool migrationConfirmationPending;
    private bool welcomeSetupConfirmationPending;
    private bool lastRenderedMinimized;
    private bool preserveRememberedMiniOnNextForegroundCheck;
    private bool controlCenterOpen;
    private bool controlCenterPickerOpen;
    private int controlCenterPickerPage;
    private float controlCenterProgress;
    private string controlCenterPickerHoveredWidgetId = string.Empty;
    private double controlCenterPickerHoverStartedAt;
    private string controlCenterMacroPickerWidgetId = string.Empty;
    private int controlCenterMacroPickerSlot = -1;
    private int controlCenterMacroPickerPage;
    private string controlCenterMacroHoveredKeyId = string.Empty;
    private double controlCenterMacroHoverStartedAt;
    private int controlCenterMacroHoverFrame = -1;
    // ImGui retains the shell window position across plugin reloads. Force the
    // first rendered frame to use the position saved for the selected size so
    // a full-size startup cannot inherit the last mini position (and vice versa).
    private bool forceNextWindowPosition = true;

    private bool HasUnreadChangelog =>
        config.SetupCompleted &&
        !string.Equals(
            config.LastReadChangelogVersion,
            ReleaseVersion,
            StringComparison.OrdinalIgnoreCase);

    private bool ControlCenterVisible =>
        controlCenterOpen || controlCenterProgress > 0.01f;

    public TabletWindow(
        Configuration config,
        CatalogService catalog,
        ChangelogService changelog,
        TextureCache textures,
        FileDialogService dialogs,
        AppHostService appHost,
        Action save,
        Action saveImmediate)
    {
        this.config = config;
        this.catalog = catalog;
        this.changelog = changelog;
        wiki = new WikiService();
        this.textures = textures;
        this.dialogs = dialogs;
        this.appHost = appHost;
        this.save = save;
        this.saveImmediate = saveImmediate;
        TabletAppTheme.RememberTheme(ThemePalette.Resolve(config.Theme));
        supporters = LoadSupporters();
        migrationStatus = GetMigrationHistoryStatus();
        NormalizeMiniSettings();
        if (config.SetupCompleted && config.TutorialCompleted)
        {
            config.Minimized = config.StartupTabletMode switch
            {
                "Full" => false,
                "Mini" => true,
                _ => config.Minimized,
            };
        }
        lastRenderedMinimized = config.Minimized;
        preserveRememberedMiniOnNextForegroundCheck =
            config.Minimized &&
            config.StartupTabletMode.Equals("RememberLast", StringComparison.OrdinalIgnoreCase);
        startupAnimationPending = config.ShowStartupAnimation && !config.Minimized;
        if (config.SetupCompleted && !config.TutorialCompleted)
        {
            tutorialStep = TutorialStep.Home;
            screen = Screen.Settings;
            settingsPage = SettingsPage.General;
            config.Minimized = false;
        }
        foreach (var id in AppHostService.BundledAppIds.Where(appHost.IsEnabled))
            setupSelectedApps.Add(id);
    }

    public void OpenHome()
    {
        controlCenterOpen = false;
        controlCenterPickerOpen = false;
        controlCenterProgress = 0f;
        screen = config.SetupCompleted ? Screen.Home : Screen.Welcome;
        if (!config.SetupCompleted)
            welcomePage = 0;
        settingsPage = SettingsPage.General;
        activeModuleId = string.Empty;
        config.Minimized = config.SetupCompleted && config.TutorialCompleted
            ? config.StartupTabletMode switch
            {
                "Full" => false,
                "Mini" => true,
                _ => config.Minimized,
            }
            : false;
        config.WindowVisible = true;
        save();
    }

    public void OpenSettings()
    {
        settingsPage = SettingsPage.General;
        if (!config.SetupCompleted)
            welcomePage = 0;
        activeModuleId = string.Empty;
        config.Minimized = false;
        config.WindowVisible = true;
        BeginOpening(config.SetupCompleted ? Screen.Settings : Screen.Welcome);
        save();
    }

    public void RequestRecovery()
    {
        recoveryRequested = true;
        config.WindowVisible = true;
    }

    public void Draw(bool allowDuringTravel = false)
    {
        ProcessAppForegroundRequest();
        if (recoveryRequested &&
            DalamudServices.ClientState.IsLoggedIn &&
            DalamudServices.ObjectTable.LocalPlayer is not null)
        {
            RecoverToActiveGameScreen();
        }
        if (!config.WindowVisible ||
            (!allowDuringTravel &&
             (!DalamudServices.ClientState.IsLoggedIn ||
              DalamudServices.ObjectTable.LocalPlayer is null)))
            return;

        var drawingMinimized = config.Minimized;
        var palette = ThemePalette.Resolve(config.Theme);
        TabletAppTheme.RememberTheme(palette);
        uiScale = drawingMinimized
            ? SmallTabletScale
            : LargeTabletScale;
        var fontScale = drawingMinimized ? SmallTabletScale : AppContentScale;
        PushShellStyle();
        try
        {
            if (lastRenderedMinimized != drawingMinimized)
            {
                lastRenderedMinimized = drawingMinimized;
                forceNextWindowPosition = true;
            }

            var outerSize = (drawingMinimized ? MiniOuterSize : FullOuterSize) * uiScale;
            ImGui.SetNextWindowSize(outerSize, ImGuiCond.Always);
            var positionLocked = !drawingMinimized && config.PositionLocked;
            var windowPosition = drawingMinimized
                ? config.MiniPosition
                : config.Position;
            ImGui.SetNextWindowPos(
                windowPosition,
                positionLocked || forceNextWindowPosition
                    ? ImGuiCond.Always
                    : ImGuiCond.FirstUseEver);

            var flags =
                ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoBackground;
            if (positionLocked || TabletAppTheme.HasOpenModal)
                flags |= ImGuiWindowFlags.NoMove;

            // The fresh ID intentionally discards the platform-window class
            // retained by ImGui from the reverted v1.0.29.0 shell.
            if (ImGui.Begin("AirTablet###AirTabletShellStable", flags))
            {
                ImGui.SetWindowFontScale(fontScale);
                if (drawingMinimized)
                    DrawMini(palette);
                else
                    DrawFullTablet(palette);

                if (!positionLocked && !TabletAppTheme.HasOpenModal)
                {
                    var position = ImGui.GetWindowPos();
                    var savedPosition = drawingMinimized
                        ? config.MiniPosition
                        : config.Position;
                    if (Vector2.DistanceSquared(position, savedPosition) > 1f)
                    {
                        if (drawingMinimized)
                        {
                            config.MiniPosition = position;
                            config.MiniPositionInitialized = true;
                        }
                        else
                        {
                            config.Position = position;
                        }
                        save();
                    }
                }
            }
            ImGui.End();
            forceNextWindowPosition = false;
        }
        finally
        {
            PopShellStyle();
        }

        dialogs.Draw();
    }

    private void ProcessAppForegroundRequest()
    {
        var preserveRememberedMini = preserveRememberedMiniOnNextForegroundCheck;
        preserveRememberedMiniOnNextForegroundCheck = false;

        if (tutorialStep != TutorialStep.None)
        {
            appHost.ConsumeHomeRequest();
            appHost.ConsumeForegroundRequest();
            return;
        }

        if (appHost.ConsumeHomeRequest())
        {
            activeModuleId = string.Empty;
            BeginOpening(Screen.Home);
            saveImmediate();
            return;
        }

        var requestedAppId = appHost.ConsumeForegroundRequest();
        if (string.IsNullOrWhiteSpace(requestedAppId) ||
            !config.SetupCompleted ||
            !appHost.IsEnabled(requestedAppId) ||
            !appHost.IsRunning(requestedAppId))
        {
            return;
        }

        config.WindowVisible = true;
        if (!preserveRememberedMini)
            config.Minimized = false;
        activeModuleId = requestedAppId;
        BeginOpening(Screen.Module);
        saveImmediate();
    }

    private void DrawFullTablet(ThemePalette palette)
    {
        var windowPos = ImGui.GetWindowPos();
        var bodyMin = windowPos + S(new Vector2(7, 7));
        var screenMin = bodyMin + BezelPadding * uiScale;
        var screenMax = screenMin + DisplaySize * uiScale;
        var bodyMax = screenMax + BezelPadding * uiScale;
        var draw = ImGui.GetWindowDrawList();
        TabletAppTheme.SetTabletScreenBounds(screenMin, screenMax);

        draw.AddRectFilled(
            bodyMin + S(new Vector2(3, 4)),
            bodyMax + S(new Vector2(3, 4)),
            ImGui.GetColorU32(new Vector4(0, 0, 0, 0.50f)),
            S(25f));
        DrawBlackChassis(draw, bodyMin, bodyMax, S(25f));
        DrawScreenSurface(draw, screenMin, screenMax, palette, S(17f));
        DrawWallpaper(screenMin, screenMax, palette);
        if (screen != Screen.Home)
        {
            draw.AddRectFilled(
                screenMin,
                screenMax,
                ImGui.GetColorU32(new Vector4(
                    palette.Surface.X,
                    palette.Surface.Y,
                    palette.Surface.Z,
                    1f)),
                S(17f));
        }

        var startupAnimationActive = UpdateStartupAnimation();
        DrawPhysicalLockButton(bodyMax);
        DrawPhysicalMiniButton(bodyMax);

        if (!startupAnimationActive)
        {
            ImGui.SetCursorScreenPos(screenMin);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
                if (ImGui.BeginChild("##tablet-screen", screenMax - screenMin, false, ImGuiWindowFlags.NoScrollbar))
                {
                    ImGui.SetWindowFontScale(AppContentScale);
                    DrawStatusBar();
                    ImGui.SetCursorPosY(S(StatusBarHeight));
                    if (ImGui.BeginChild(
                            "##screen-content",
                            new Vector2(0, -S(HomeGestureAreaHeight)),
                            false,
                            ImGuiWindowFlags.NoScrollbar))
                    {
                        ImGui.SetWindowFontScale(AppContentScale);
                        UpdateScreenTransition();
                        var contentDisabled = tutorialStep != TutorialStep.None ||
                                              ControlCenterVisible;
                        if (contentDisabled)
                            ImGui.BeginDisabled();
                        switch (screen)
                        {
                        case Screen.Home:
                            DrawHome(palette);
                            break;
                        case Screen.Welcome:
                            DrawWelcome(palette);
                            break;
                        case Screen.Settings:
                            DrawSettingsApp(palette);
                            break;
                        case Screen.Wiki:
                            DrawWikiApp(palette);
                            break;
                        case Screen.Module:
                            DrawModuleScreen(palette);
                            break;
                        case Screen.Feedback:
                            DrawFeedbackApp(palette);
                            break;
                        }
                        if (contentDisabled)
                            ImGui.EndDisabled();
                    }
                    ImGui.EndChild();
                    DrawControlCenter(palette);
                    if (ControlCenterVisible || tutorialStep is TutorialStep.ControlCenterOpen or TutorialStep.ControlCenterClose)
                        DrawStatusBarOverlay();
                    if (config.SetupCompleted)
                        DrawGestureBar(palette);
                }
                ImGui.EndChild();
            ImGui.PopStyleColor();
            if (!ControlCenterVisible)
                DrawTabletNotification(screenMin, screenMax, palette);
            DrawTutorialOverlay(screenMin, screenMax, bodyMax, palette);
        }
        else
        {
            DrawStartupAnimation(screenMin, screenMax, palette);
        }
    }

    private void DrawMini(ThemePalette palette)
    {
        var windowPos = ImGui.GetWindowPos();
        var bodyMin = windowPos + S(new Vector2(5, 5));
        var bodyMax = windowPos + MiniOuterSize * uiScale - S(new Vector2(12, 5));
        var screenMin = bodyMin + S(new Vector2(10, 9));
        var screenMax = bodyMax - S(new Vector2(10, 9));
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(
            bodyMin + S(new Vector2(2, 3)),
            bodyMax + S(new Vector2(2, 3)),
            ImGui.GetColorU32(new Vector4(0, 0, 0, 0.52f)),
            S(16f));
        DrawBlackChassis(draw, bodyMin, bodyMax, S(16f));
        DrawScreenSurface(draw, screenMin, screenMax, palette, S(10f));
        DrawWallpaper(screenMin, screenMax, palette);

        var closeCenter = new Vector2(screenMax.X - S(13f), screenMin.Y + S(13f));
        var closeHitMin = closeCenter - S(new Vector2(10, 10));
        var closeHovered = ImGui.IsMouseHoveringRect(closeHitMin, closeHitMin + S(new Vector2(20, 20)));
        var closeHover = AnimateControlHover("mini-close", closeHovered);
        var closeScale = 1f + closeHover * 0.12f;
        var closeRadius = S(9f) * closeScale;
        var closeColor = Vector4.Lerp(
            new Vector4(0.18f, 0.18f, 0.22f, 0.96f),
            new Vector4(0.48f, 0.49f, 0.56f, 1f),
            closeHover);
        var closeGlyph = S(3f) * closeScale;
        draw.AddCircleFilled(closeCenter, closeRadius, ImGui.GetColorU32(closeColor));
        draw.AddLine(closeCenter - new Vector2(closeGlyph), closeCenter + new Vector2(closeGlyph), ImGui.GetColorU32(Vector4.One), S(1.5f) * closeScale);
        draw.AddLine(closeCenter + new Vector2(-closeGlyph, closeGlyph), closeCenter + new Vector2(closeGlyph, -closeGlyph), ImGui.GetColorU32(Vector4.One), S(1.5f) * closeScale);
        ImGui.SetCursorScreenPos(closeHitMin);
        if (ImGui.InvisibleButton("##mini-close", S(new Vector2(20, 20))) &&
            tutorialStep == TutorialStep.None)
        {
            config.WindowVisible = false;
            save();
        }
        DrawTooltip("Close AirTablet. Reopen it with /airtablet.");

        var expandMin = screenMin + S(new Vector2(51, 33));
        var expandMax = expandMin + S(new Vector2(47, 43));
        var expandHovered = ImGui.IsMouseHoveringRect(expandMin, expandMax);
        var expandHover = AnimateControlHover("mini-expand", expandHovered);
        var expandScale = 1f + expandHover * 0.10f;
        var expandCenter = (expandMin + expandMax) * 0.5f;
        var animatedExpandSize = (expandMax - expandMin) * expandScale;
        var animatedExpandMin = expandCenter - animatedExpandSize * 0.5f;
        var animatedExpandMax = expandCenter + animatedExpandSize * 0.5f;
        var expandColor = Vector4.Lerp(palette.Accent, palette.AccentHover, expandHover);
        draw.AddRectFilled(animatedExpandMin, animatedExpandMax, ImGui.GetColorU32(expandColor), S(14f) * expandScale);
        DrawExpandGlyph(draw, expandCenter, expandScale);
        ImGui.SetCursorScreenPos(expandMin);
        if (ImGui.InvisibleButton("##mini-expand", expandMax - expandMin))
        {
            if (tutorialStep is TutorialStep.None or TutorialStep.Restore)
            {
                RestoreFullTablet();
                if (tutorialStep == TutorialStep.Restore)
                    CompleteControlTutorial();
                else
                    save();
            }
        }
        DrawTooltip("Restore the full tablet.");

        var clock = ClockText();
        draw.AddText(screenMin + S(new Vector2(9, 8)), ImGui.GetColorU32(new Vector4(0.92f, 0.93f, 0.98f, 1f)), clock);
        DrawMiniTutorial(bodyMin, bodyMax, expandMin, expandMax, palette);
    }

    private void NormalizeMiniSettings()
    {
        if (config.MiniCollapseCorner is not
            ("TopLeft" or "TopRight" or "BottomLeft" or "BottomRight"))
        {
            config.MiniCollapseCorner = "TopLeft";
        }

        if (!config.MiniPositionInitialized)
        {
            config.MiniPosition = CalculateCollapsedMiniPosition();
            config.MiniPositionInitialized = true;
        }
    }

    private void MinimizeTablet()
    {
        controlCenterOpen = false;
        controlCenterPickerOpen = false;
        controlCenterProgress = 0f;
        if (config.AnchorMiniToCollapseCorner ||
            !config.MiniPositionInitialized)
        {
            config.MiniPosition = CalculateCollapsedMiniPosition();
            config.MiniPositionInitialized = true;
        }
        config.Minimized = true;
    }

    private void RestoreFullTablet()
    {
        config.Minimized = false;
    }

    private Vector2 CalculateCollapsedMiniPosition()
    {
        var fullSize = FullOuterSize * LargeTabletScale;
        var miniSize = MiniOuterSize * SmallTabletScale;
        var remaining = Vector2.Max(Vector2.Zero, fullSize - miniSize);
        var offset = config.MiniCollapseCorner switch
        {
            "TopRight" => new Vector2(remaining.X, 0f),
            "BottomLeft" => new Vector2(0f, remaining.Y),
            "BottomRight" => remaining,
            _ => Vector2.Zero,
        };
        return config.Position + offset;
    }

    private void RecoverToActiveGameScreen()
    {
        var viewport = ImGui.GetMainViewport();
        var minimized = config.Minimized;
        var tabletSize = minimized
            ? MiniOuterSize * SmallTabletScale
            : FullOuterSize * LargeTabletScale;
        var centeredPosition =
            viewport.Pos +
            Vector2.Max(Vector2.Zero, (viewport.Size - tabletSize) * 0.5f);
        if (minimized)
        {
            config.MiniPosition = centeredPosition;
            config.MiniPositionInitialized = true;
        }
        else
        {
            config.Position = centeredPosition;
        }
        config.WindowVisible = true;
        recoveryRequested = false;
        forceNextWindowPosition = true;
        ShowNotice("AirTablet was recovered to the center of the game screen.");
        saveImmediate();
    }

    private void DrawTabletNotification(
        Vector2 screenMin,
        Vector2 screenMax,
        ThemePalette palette)
    {
        if (string.IsNullOrWhiteSpace(notice) || DateTime.UtcNow >= noticeUntil)
            return;

        const double enterSeconds = 0.22;
        const double exitSeconds = 0.26;
        var now = ImGui.GetTime();
        var remaining = (noticeUntil - DateTime.UtcNow).TotalSeconds;
        var enter = (float)Math.Clamp(
            (now - noticeStartedAt) / enterSeconds,
            0d,
            1d);
        var exit = (float)Math.Clamp(remaining / exitSeconds, 0d, 1d);
        var progress = MathF.Min(
            1f - MathF.Pow(1f - enter, 3f),
            1f - MathF.Pow(1f - exit, 3f));

        var bubbleWidth = MathF.Min(
            screenMax.X - screenMin.X - S(36f),
            S(430f));
        var bubbleHeight = S(62f);
        var bubbleX = screenMin.X +
            (screenMax.X - screenMin.X - bubbleWidth) * 0.5f;
        var bubbleY = screenMin.Y + S(9f) -
            (1f - progress) * (bubbleHeight + S(18f));

        var bubbleMin = new Vector2(bubbleX, bubbleY);
        var bubbleMax = bubbleMin + new Vector2(bubbleWidth, bubbleHeight);
        // Foreground rendering places the notification above every screen child.
        // Its explicit screen clip still keeps it beneath and inside the bezel.
        var draw = ImGui.GetForegroundDrawList();
        draw.PushClipRect(screenMin, screenMax, true);
        draw.AddRectFilled(
            bubbleMin,
            bubbleMax,
            ImGui.GetColorU32(new Vector4(
                palette.SurfaceRaised.X,
                palette.SurfaceRaised.Y,
                palette.SurfaceRaised.Z,
                0.94f * progress)),
            S(16f));
        draw.AddRect(
            bubbleMin,
            bubbleMax,
            ImGui.GetColorU32(new Vector4(
                palette.Accent.X,
                palette.Accent.Y,
                palette.Accent.Z,
                0.64f * progress)),
            S(16f),
            ImDrawFlags.None,
            S(1f));
        draw.AddText(
            bubbleMin + S(new Vector2(15f, 8f)),
            ImGui.GetColorU32(new Vector4(
                palette.AccentHover.X,
                palette.AccentHover.Y,
                palette.AccentHover.Z,
                progress)),
            "AirTablet");
        var messageWidth = bubbleWidth - S(30f);
        draw.AddText(
            bubbleMin + S(new Vector2(15f, 31f)),
            ImGui.GetColorU32(new Vector4(0.96f, 0.97f, 1f, progress)),
            FitTextToWidth(notice, messageWidth));
        draw.PopClipRect();
    }

    private bool UpdateStartupAnimation()
    {
        if (!startupAnimationPending)
            return false;

        if (startupAnimationStartedAt < 0d)
            startupAnimationStartedAt = ImGui.GetTime();

        if (ImGui.GetTime() - startupAnimationStartedAt < StartupAnimationSeconds)
            return true;

        startupAnimationPending = false;
        return false;
    }

    private void DrawStartupAnimation(
        Vector2 screenMin,
        Vector2 screenMax,
        ThemePalette palette)
    {
        var elapsed = Math.Max(0d, ImGui.GetTime() - startupAnimationStartedAt);
        var progress = (float)Math.Clamp(
            elapsed / StartupAnimationSeconds,
            0d,
            1d);
        var easedProgress = 1f - MathF.Pow(1f - progress, 2.2f);
        var draw = ImGui.GetForegroundDrawList();
        draw.PushClipRect(screenMin, screenMax, true);
        draw.AddRectFilled(
            screenMin,
            screenMax,
            ImGui.GetColorU32(new Vector4(
                palette.Background.X * 0.54f,
                palette.Background.Y * 0.54f,
                palette.Background.Z * 0.62f,
                1f)));

        var center = (screenMin + screenMax) * 0.5f;
        var pulse = 0.5f + 0.5f * MathF.Sin((float)elapsed * 3.2f);
        var markCenter = center - S(new Vector2(0f, 68f));
        draw.AddCircleFilled(
            markCenter,
            S(34f + pulse * 3f),
            ImGui.GetColorU32(new Vector4(
                palette.Accent.X,
                palette.Accent.Y,
                palette.Accent.Z,
                0.18f + pulse * 0.08f)),
            48);
        draw.AddCircle(
            markCenter,
            S(27f),
            ImGui.GetColorU32(palette.AccentHover),
            48,
            S(2.5f));
        for (var index = 0; index < 8; index++)
        {
            var angle = (float)elapsed * 2.4f +
                index * MathF.PI * 0.25f;
            var dot = markCenter + new Vector2(
                MathF.Cos(angle),
                MathF.Sin(angle)) * S(27f);
            var alpha = 0.22f + 0.78f * ((index + 1f) / 8f);
            draw.AddCircleFilled(
                dot,
                S(index == 7 ? 3.8f : 2.5f),
                ImGui.GetColorU32(new Vector4(
                    palette.AccentHover.X,
                    palette.AccentHover.Y,
                    palette.AccentHover.Z,
                    alpha)),
                16);
        }

        DrawCenteredText(
            draw,
            new Vector2(center.X, center.Y - S(13f)),
            "AirTablet",
            new Vector4(0.97f, 0.97f, 1f, 1f));
        DrawCenteredText(
            draw,
            new Vector2(center.X, center.Y + S(14f)),
            $"AirTabOS {ReleaseVersion}",
            new Vector4(0.67f, 0.68f, 0.76f, 1f));

        var barWidth = S(360f);
        var barHeight = S(7f);
        var barMin = new Vector2(
            center.X - barWidth * 0.5f,
            center.Y + S(58f));
        var barMax = barMin + new Vector2(barWidth, barHeight);
        draw.AddRectFilled(
            barMin,
            barMax,
            ImGui.GetColorU32(new Vector4(0.20f, 0.20f, 0.25f, 0.94f)),
            barHeight * 0.5f);
        draw.AddRectFilled(
            barMin,
            new Vector2(
                barMin.X + MathF.Max(barHeight, barWidth * easedProgress),
                barMax.Y),
            ImGui.GetColorU32(palette.Accent),
            barHeight * 0.5f);
        DrawCenteredText(
            draw,
            new Vector2(center.X, center.Y + S(83f)),
            progress < 0.78f ? "Preparing your tablet..." : "Ready",
            new Vector4(0.74f, 0.75f, 0.82f, 1f));
        draw.PopClipRect();
    }

    private void DrawTutorialOverlay(
        Vector2 screenMin,
        Vector2 screenMax,
        Vector2 bodyMax,
        ThemePalette palette)
    {
        if (tutorialStep is TutorialStep.None or TutorialStep.Restore)
            return;

        var draw = ImGui.GetForegroundDrawList();
        draw.PushClipRect(screenMin, screenMax, true);
        draw.AddRectFilled(
            screenMin,
            screenMax,
            ImGui.GetColorU32(new Vector4(0.015f, 0.016f, 0.023f, 0.78f)));

        var cardWidth = S(520f);
        var cardHeight = S(146f);
        Vector2 target;
        Vector2 cardMin;
        string title;
        string firstLine;
        string secondLine;
        string action;
        var stepNumber = 1;

        switch (tutorialStep)
        {
            case TutorialStep.Home:
                target = new Vector2(
                    (screenMin.X + screenMax.X) * 0.5f,
                    screenMax.Y - S(8f));
                cardMin = new Vector2(
                    target.X - cardWidth * 0.5f,
                    screenMin.Y + S(176f));
                title = "Your Home control";
                firstLine = "Return to the Home screen from any app or tablet page.";
                secondLine = "Enabled apps stay loaded and continue working in the background.";
                action = "Click the highlighted Home bar to continue.";
                break;
            case TutorialStep.ControlCenterOpen:
                target = new Vector2(screenMax.X - S(100f), screenMin.Y + S(14f));
                cardMin = new Vector2(
                    screenMax.X - cardWidth - S(34f),
                    screenMin.Y + S(72f));
                title = "Open Control Center";
                firstLine = "Your status indicators open quick controls and live app widgets.";
                secondLine = "Control Center stays local and can be customized with only the widgets you want.";
                action = "Click the highlighted status area to open it.";
                stepNumber = 2;
                break;
            case TutorialStep.ControlCenterClose:
                target = new Vector2(screenMax.X - S(100f), screenMin.Y + S(14f));
                cardMin = new Vector2(
                    screenMax.X - cardWidth - S(34f),
                    screenMin.Y + S(72f));
                title = "Close Control Center";
                firstLine = "Control Center appears above the current app without unloading it.";
                secondLine = "You can close it from this same status area or with the Home bar.";
                action = "Click the highlighted status area again to close it.";
                stepNumber = 3;
                break;
            case TutorialStep.LockPosition:
                target = new Vector2(
                    screenMax.X - S(3f),
                    bodyMax.Y - S(481f));
                cardMin = new Vector2(
                    screenMax.X - cardWidth - S(88f),
                    MathF.Max(screenMin.Y + S(62f), target.Y - S(28f)));
                title = "Lock the tablet position";
                firstLine = "The upper side button locks AirTablet at its current position.";
                secondLine = "This prevents accidental dragging while you use the tablet.";
                action = "Click the upper side button to continue.";
                stepNumber = 4;
                break;
            default:
                target = new Vector2(
                    screenMax.X - S(3f),
                    bodyMax.Y - S(377f));
                cardMin = new Vector2(
                    screenMax.X - cardWidth - S(88f),
                    MathF.Min(screenMax.Y - cardHeight - S(34f), target.Y - S(48f)));
                title = "Minimize without stopping";
                firstLine = "The lower side button changes AirTablet to its compact view.";
                secondLine = "Your enabled apps remain loaded and continue working.";
                action = "Click the lower side button to continue.";
                stepNumber = 5;
                break;
        }

        var cardMax = cardMin + new Vector2(cardWidth, cardHeight);
        DrawTutorialCard(
            draw,
            cardMin,
            cardMax,
            palette,
            title,
            firstLine,
            secondLine,
            action,
            stepNumber);

        if (tutorialStep == TutorialStep.Home)
        {
            var highlightMin = target - S(new Vector2(75f, 10f));
            var highlightMax = target + S(new Vector2(75f, 8f));
            draw.AddRectFilled(
                highlightMin,
                highlightMax,
                ImGui.GetColorU32(new Vector4(
                    palette.Accent.X,
                    palette.Accent.Y,
                    palette.Accent.Z,
                    0.28f)),
                S(9f));
            draw.AddRect(
                highlightMin,
                highlightMax,
                ImGui.GetColorU32(palette.AccentHover),
                S(9f),
                ImDrawFlags.None,
                S(2f));
            draw.AddRectFilled(
                target - S(new Vector2(59f, 2f)),
                target + S(new Vector2(59f, 2f)),
                ImGui.GetColorU32(Vector4.One),
                S(3f));
            DrawDownArrow(
                draw,
                new Vector2(target.X, cardMax.Y + S(15f)),
                target - S(new Vector2(0f, 14f)),
                palette.AccentHover);
        }
        else if (tutorialStep is TutorialStep.ControlCenterOpen or TutorialStep.ControlCenterClose)
        {
            var highlightMin = target - S(new Vector2(96f, 14f));
            var highlightMax = target + S(new Vector2(84f, 14f));
            draw.AddRectFilled(
                highlightMin,
                highlightMax,
                ImGui.GetColorU32(new Vector4(palette.Accent.X, palette.Accent.Y, palette.Accent.Z, 0.28f)),
                S(12f));
            draw.AddRect(
                highlightMin,
                highlightMax,
                ImGui.GetColorU32(palette.AccentHover),
                S(12f),
                ImDrawFlags.None,
                S(2f));
            DrawUpArrow(
                draw,
                new Vector2(target.X, cardMin.Y - S(12f)),
                target + S(new Vector2(0f, 18f)),
                palette.AccentHover);
        }
        else
        {
            DrawRightArrow(
                draw,
                new Vector2(cardMax.X + S(12f), target.Y),
                target - S(new Vector2(8f, 0f)),
                palette.AccentHover);
        }
        draw.PopClipRect();

        if (tutorialStep == TutorialStep.Home)
        {
            var tutorialHitMin = target - S(new Vector2(75f, 10f));
            var tutorialHitSize = S(new Vector2(150f, 18f));
            ImGui.SetCursorScreenPos(tutorialHitMin);
            if (ImGui.InvisibleButton("##tutorial-home-control", tutorialHitSize))
            {
                ReturnHome();
                AdvanceControlTutorial(TutorialStep.ControlCenterOpen);
            }
        }
    }

    private void DrawMiniTutorial(
        Vector2 bodyMin,
        Vector2 bodyMax,
        Vector2 expandMin,
        Vector2 expandMax,
        ThemePalette palette)
    {
        if (tutorialStep != TutorialStep.Restore)
            return;

        var draw = ImGui.GetForegroundDrawList();
        var cardSize = S(new Vector2(342f, 154f));
        var displayWidth = ImGui.GetIO().DisplaySize.X;
        var placeRight = bodyMax.X + S(22f) + cardSize.X < displayWidth;
        var cardMin = placeRight
            ? new Vector2(bodyMax.X + S(22f), bodyMin.Y)
            : new Vector2(bodyMin.X - cardSize.X - S(22f), bodyMin.Y);
        var cardMax = cardMin + cardSize;
        draw.AddRectFilled(
            cardMin + S(new Vector2(4f, 5f)),
            cardMax + S(new Vector2(4f, 5f)),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.42f)),
            S(15f));
        DrawTutorialCard(
            draw,
            cardMin,
            cardMax,
            palette,
            "Restore the full tablet",
            "This button restores the full tablet.",
            "Your apps stayed loaded while minimized.",
            "Click Restore to finish the tutorial.",
            6);

        var target = (expandMin + expandMax) * 0.5f;
        draw.AddRect(
            expandMin - S(new Vector2(4f)),
            expandMax + S(new Vector2(4f)),
            ImGui.GetColorU32(Vector4.One),
            S(17f),
            ImDrawFlags.None,
            S(2f));
        if (placeRight)
        {
            DrawLeftArrow(
                draw,
                new Vector2(cardMin.X - S(10f), target.Y),
                target + S(new Vector2(10f, 0f)),
                palette.AccentHover);
        }
        else
        {
            DrawRightArrow(
                draw,
                new Vector2(cardMax.X + S(10f), target.Y),
                target - S(new Vector2(10f, 0f)),
                palette.AccentHover);
        }
    }

    private void DrawTutorialCard(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 max,
        ThemePalette palette,
        string title,
        string firstLine,
        string secondLine,
        string action,
        int stepNumber)
    {
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(new Vector4(
                palette.SurfaceRaised.X,
                palette.SurfaceRaised.Y,
                palette.SurfaceRaised.Z,
                0.98f)),
            S(16f));
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(new Vector4(
                palette.Accent.X,
                palette.Accent.Y,
                palette.Accent.Z,
                0.82f)),
            S(16f),
            ImDrawFlags.None,
            S(1.5f));
        draw.AddText(
            min + S(new Vector2(18f, 14f)),
            ImGui.GetColorU32(palette.AccentHover),
            title);
        var progress = $"STEP {stepNumber} OF 6";
        var progressSize = ImGui.CalcTextSize(progress);
        draw.AddText(
            new Vector2(max.X - progressSize.X - S(18f), min.Y + S(14f)),
            ImGui.GetColorU32(new Vector4(0.60f, 0.61f, 0.69f, 1f)),
            progress);
        draw.AddText(
            min + S(new Vector2(18f, 48f)),
            ImGui.GetColorU32(new Vector4(0.95f, 0.96f, 0.99f, 1f)),
            firstLine);
        draw.AddText(
            min + S(new Vector2(18f, 72f)),
            ImGui.GetColorU32(new Vector4(0.74f, 0.75f, 0.82f, 1f)),
            secondLine);
        draw.AddText(
            min + S(new Vector2(18f, 108f)),
            ImGui.GetColorU32(palette.AccentHover),
            action);
    }

    private static void DrawCenteredText(
        ImDrawListPtr draw,
        Vector2 center,
        string text,
        Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        draw.AddText(
            center - size * 0.5f,
            ImGui.GetColorU32(color),
            text);
    }

    private void DrawRightArrow(
        ImDrawListPtr draw,
        Vector2 start,
        Vector2 end,
        Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        draw.AddLine(start, end - S(new Vector2(11f, 0f)), packed, S(3f));
        draw.AddTriangleFilled(
            end,
            end - S(new Vector2(13f, 8f)),
            end - S(new Vector2(13f, -8f)),
            packed);
    }

    private void DrawLeftArrow(
        ImDrawListPtr draw,
        Vector2 start,
        Vector2 end,
        Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        draw.AddLine(start, end + S(new Vector2(11f, 0f)), packed, S(3f));
        draw.AddTriangleFilled(
            end,
            end + S(new Vector2(13f, 8f)),
            end + S(new Vector2(13f, -8f)),
            packed);
    }

    private void DrawDownArrow(
        ImDrawListPtr draw,
        Vector2 start,
        Vector2 end,
        Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        draw.AddLine(start, end - S(new Vector2(0f, 11f)), packed, S(3f));
        draw.AddTriangleFilled(
            end,
            end - S(new Vector2(8f, 13f)),
            end - S(new Vector2(-8f, 13f)),
            packed);
    }

    private void DrawUpArrow(
        ImDrawListPtr draw,
        Vector2 start,
        Vector2 end,
        Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        draw.AddLine(start, end + S(new Vector2(0f, 11f)), packed, S(3f));
        draw.AddTriangleFilled(
            end,
            end + S(new Vector2(-8f, 13f)),
            end + S(new Vector2(8f, 13f)),
            packed);
    }

    private static string FitTextToWidth(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;

        const string ellipsis = "…";
        var length = text.Length;
        while (length > 1 &&
               ImGui.CalcTextSize(text[..length] + ellipsis).X > width)
        {
            length--;
        }
        return text[..Math.Max(1, length)].TrimEnd() + ellipsis;
    }

    private void DrawPhysicalLockButton(Vector2 bodyMax)
    {
        var min = new Vector2(bodyMax.X - S(1f), bodyMax.Y - S(514f));
        DrawPhysicalSideButton(
            "##physical-lock",
            min,
            S(new Vector2(10, 66)),
            config.PositionLocked,
            config.PositionLocked
                ? "Unlock the tablet so it can be moved."
                : "Lock the tablet at its current screen position.",
            () =>
            {
                if (startupAnimationPending ||
                    TabletAppTheme.HasOpenModal ||
                    tutorialStep != TutorialStep.None &&
                    tutorialStep != TutorialStep.LockPosition)
                {
                    return;
                }
                config.PositionLocked = !config.PositionLocked;
                if (tutorialStep == TutorialStep.LockPosition)
                    AdvanceControlTutorial(TutorialStep.Minimize);
                else
                    save();
            });
    }

    private void DrawPhysicalMiniButton(Vector2 bodyMax)
    {
        var min = new Vector2(bodyMax.X - S(1f), bodyMax.Y - S(418f));
        DrawPhysicalSideButton(
            "##physical-mini",
            min,
            S(new Vector2(10, 82)),
            false,
            "Minimize AirTablet to its compact screen.",
            () =>
            {
                if (startupAnimationPending ||
                    TabletAppTheme.HasOpenModal ||
                    tutorialStep != TutorialStep.None &&
                    tutorialStep != TutorialStep.Minimize)
                {
                    return;
                }
                MinimizeTablet();
                if (tutorialStep == TutorialStep.Minimize)
                    AdvanceControlTutorial(TutorialStep.Restore);
                else
                    save();
            });
    }

    private void DrawPhysicalSideButton(
        string id,
        Vector2 min,
        Vector2 size,
        bool pressed,
        string tooltip,
        Action clicked)
    {
        var max = min + size;
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        var hoverAmount = AnimateControlHover(id, hovered);
        var palette = ThemePalette.Resolve(config.Theme);
        var idleColor = pressed
            ? Vector4.Lerp(
                new Vector4(0.075f, 0.078f, 0.09f, 1f),
                new Vector4(palette.Accent.X, palette.Accent.Y, palette.Accent.Z, 1f),
                0.34f)
            : new Vector4(0.12f, 0.125f, 0.14f, 1f);
        var brightColor = pressed
            ? Vector4.Lerp(
                new Vector4(0.20f, 0.21f, 0.24f, 1f),
                new Vector4(palette.AccentHover.X, palette.AccentHover.Y, palette.AccentHover.Z, 1f),
                0.52f)
            : new Vector4(0.31f, 0.32f, 0.36f, 1f);
        draw.AddRectFilled(
            min + S(new Vector2(2, 3)),
            max + S(new Vector2(2, 3)),
            ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f)),
            S(4f));
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(Vector4.Lerp(idleColor, brightColor, hoverAmount)),
            S(4f));
        draw.AddLine(
            min + S(new Vector2(2, 2)),
            new Vector2(max.X - S(2f), min.Y + S(2f)),
            ImGui.GetColorU32(pressed
                ? new Vector4(palette.AccentHover.X, palette.AccentHover.Y, palette.AccentHover.Z, 0.82f)
                : new Vector4(0.43f, 0.44f, 0.49f, 0.78f)),
            S(1f));
        draw.AddLine(
            new Vector2(max.X - S(2f), min.Y + S(4f)),
            new Vector2(max.X - S(2f), max.Y - S(4f)),
            ImGui.GetColorU32(new Vector4(0.30f, 0.31f, 0.35f, 0.78f)),
            S(1f));
        for (var y = min.Y + S(6f); y < max.Y - S(4f); y += S(4f))
        {
            draw.AddLine(
                new Vector2(min.X + S(2f), y),
                new Vector2(max.X - S(3f), y),
                ImGui.GetColorU32(new Vector4(0.72f, 0.73f, 0.76f, 0.10f)),
                S(1f));
        }
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton(id, size))
            clicked();
        DrawTooltip(tooltip);
    }

    private void DrawStatusBar()
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var width = ImGui.GetWindowSize().X;
        DrawStatusBarVisual(draw, origin, width);

        ImGui.Dummy(new Vector2(width, S(StatusBarHeight)));

        if (config.SetupCompleted && tutorialStep == TutorialStep.None)
        {
            var hitWidth = config.ShowBattery ? S(178f) : S(76f);
            var hitMin = new Vector2(origin.X + width - hitWidth - S(16f), origin.Y);
            ImGui.SetCursorScreenPos(hitMin);
            if (ImGui.InvisibleButton("##control-center-status", new Vector2(hitWidth, S(StatusBarHeight))))
            {
                controlCenterOpen = !controlCenterOpen;
                if (!controlCenterOpen)
                    controlCenterPickerOpen = false;
            }
            DrawTooltip(controlCenterOpen ? "Close Control Center." : "Open Control Center.");
        }
    }

    private void DrawStatusBarVisual(ImDrawListPtr draw, Vector2 origin, float width)
    {
        var textColor = new Vector4(0.93f, 0.94f, 0.98f, 1f);
        var textTop = origin.Y + S(5f);
        var glyphHeight = ImGui.GetTextLineHeight();
        draw.AddText(new Vector2(origin.X + S(28f), textTop), ImGui.GetColorU32(textColor), ClockText());

        var title = GetStatusBarTitle();
        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleSize = ImGui.CalcTextSize(title);
            draw.AddText(
                new Vector2(origin.X + (width - titleSize.X) * 0.5f, origin.Y + S(5f)),
                ImGui.GetColorU32(textColor),
                title);
        }

        var right = origin.X + width - S(30f);
        if (config.ShowBattery)
        {
            var batteryMin = new Vector2(
                right - BatteryGlyphWidth(glyphHeight),
                textTop);
            DrawBatteryGlyph(draw, batteryMin, glyphHeight);
            var percent = "100%";
            var percentSize = ImGui.CalcTextSize(percent);
            var percentPosition = new Vector2(
                batteryMin.X - percentSize.X - S(8f),
                textTop);
            draw.AddText(percentPosition, ImGui.GetColorU32(textColor), percent);
            DrawSignalGlyph(
                draw,
                new Vector2(
                    percentPosition.X - SignalGlyphWidth(glyphHeight) - S(9f),
                    textTop + glyphHeight),
                glyphHeight);
        }
        else
        {
            DrawSignalGlyph(
                draw,
                new Vector2(
                    right - SignalGlyphWidth(glyphHeight),
                    textTop + glyphHeight),
                glyphHeight);
        }

    }

    private void DrawStatusBarOverlay()
    {
        var parentMin = ImGui.GetWindowPos();
        var parentWidth = ImGui.GetWindowSize().X;
        var tutorialControlCenterStep = tutorialStep is TutorialStep.ControlCenterOpen or TutorialStep.ControlCenterClose;
        var flags =
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoSavedSettings;
        if (!tutorialControlCenterStep)
            flags |= ImGuiWindowFlags.NoInputs;
        ImGui.SetCursorScreenPos(parentMin);
        if (ImGui.BeginChild(
                "##control-center-status-visual-layer",
                new Vector2(parentWidth, S(StatusBarHeight)),
                false,
                flags))
        {
            DrawStatusBarVisual(
                ImGui.GetWindowDrawList(),
                ImGui.GetWindowPos(),
                ImGui.GetWindowSize().X);
            if (tutorialControlCenterStep)
            {
                var hitWidth = S(180f);
                var hitMin = new Vector2(parentMin.X + parentWidth - hitWidth - S(16f), parentMin.Y);
                ImGui.SetCursorScreenPos(hitMin);
                if (ImGui.InvisibleButton("##tutorial-control-center-status-hit", new Vector2(hitWidth, S(StatusBarHeight))))
                {
                    if (tutorialStep == TutorialStep.ControlCenterOpen)
                    {
                        controlCenterOpen = true;
                        controlCenterPickerOpen = false;
                        AdvanceControlTutorial(TutorialStep.ControlCenterClose);
                    }
                    else
                    {
                        controlCenterOpen = false;
                        controlCenterPickerOpen = false;
                        AdvanceControlTutorial(TutorialStep.LockPosition);
                    }
                }
            }
        }
        ImGui.EndChild();
    }

    private void DrawControlCenter(ThemePalette palette)
    {
        var target = controlCenterOpen ? 1f : 0f;
        var response = 1f - MathF.Exp(-13f * MathF.Max(0.001f, ImGui.GetIO().DeltaTime));
        controlCenterProgress += (target - controlCenterProgress) * response;
        if (MathF.Abs(controlCenterProgress - target) < 0.002f)
            controlCenterProgress = target;
        if (controlCenterProgress <= 0.002f)
            return;

        var screenMin = ImGui.GetWindowPos();
        var screenSize = ImGui.GetWindowSize();
        var screenMax = screenMin + screenSize;
        var contentMin = screenMin + new Vector2(0, S(StatusBarHeight));
        var contentSize = screenSize - new Vector2(0, S(StatusBarHeight + HomeGestureAreaHeight));
        ImGui.SetCursorScreenPos(contentMin);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild(
                "##control-center-interaction-layer",
                contentSize,
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoNav))
        {
            var draw = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            var eased = 1f - MathF.Pow(1f - controlCenterProgress, 3f);
            draw.PushClipRect(screenMin, screenMax, false);
            draw.AddRectFilled(
                screenMin,
                screenMax,
                ImGui.GetColorU32(new Vector4(0.018f, 0.020f, 0.035f, 0.83f * eased)));
            draw.AddCircleFilled(
                screenMin + S(new Vector2(865, 8)),
                S(245f),
                ImGui.GetColorU32(new Vector4(palette.Accent.X, palette.Accent.Y, palette.Accent.Z, 0.10f * eased)));
            draw.AddCircleFilled(
                screenMin + S(new Vector2(70, 500)),
                S(220f),
                ImGui.GetColorU32(new Vector4(palette.AccentHover.X, palette.AccentHover.Y, palette.AccentHover.Z, 0.055f * eased)));
            draw.PopClipRect();

            if (controlCenterProgress >= 0.72f)
            {
                var slide = new Vector2(0, -S(34f) * (1f - eased));
                if (tutorialStep != TutorialStep.None)
                    ImGui.BeginDisabled();
                if (controlCenterPickerOpen)
                    DrawControlCenterPicker(draw, min + slide, max + slide, palette);
                else
                    DrawControlCenterWidgets(draw, min + slide, max + slide, palette);
                if (tutorialStep != TutorialStep.None)
                    ImGui.EndDisabled();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawControlCenterWidgets(
        ImDrawListPtr draw,
        Vector2 panelMin,
        Vector2 panelMax,
        ThemePalette palette)
    {
        var titlePos = panelMin + S(new Vector2(34, 24));
        draw.AddText(titlePos, ImGui.GetColorU32(Vector4.One), "Control Center");
        draw.AddText(
            titlePos + new Vector2(0, S(24f)),
            ImGui.GetColorU32(new Vector4(0.70f, 0.72f, 0.80f, 1f)),
            "Your venue at a glance");

        var available = appHost.GetControlCenterWidgets();
        var byId = available.ToDictionary(widget => widget.Id, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(controlCenterMacroPickerWidgetId) &&
            byId.TryGetValue(controlCenterMacroPickerWidgetId, out var macroWidget))
        {
            DrawControlCenterMacroPicker(draw, panelMin, panelMax, palette, macroWidget);
            return;
        }
        config.ControlCenterWidgets ??= [];
        var selected = config.ControlCenterWidgets
            .Select(id => byId.GetValueOrDefault(id))
            .Where(widget => widget is not null)
            .Cast<ControlCenterWidget>()
            .OrderByDescending(widget => widget.Size == ControlCenterWidgetSize.Wide)
            .ToList();

        const int columns = 6;
        const int maximumSlots = 18;
        var gap = S(8f);
        var margin = S(26f);
        var compactWidth = (panelMax.X - panelMin.X - margin * 2f - gap * (columns - 1)) / columns;
        var cardHeight = S(92f);
        var gridTop = panelMin.Y + S(65f);
        var slot = 0;
        foreach (var widget in selected)
        {
            var units = widget.Size == ControlCenterWidgetSize.Wide ? 2 : 1;
            if (slot + units > maximumSlots)
                break;
            var row = slot / columns;
            var column = slot % columns;
            var min = new Vector2(
                panelMin.X + margin + column * (compactWidth + gap),
                gridTop + row * (cardHeight + gap));
            var width = units == 2 ? compactWidth * 2f + gap : compactWidth;
            DrawControlCenterWidgetCard(draw, min, new Vector2(width, cardHeight), widget, palette);
            slot += units;
        }

        var selectedIds = new HashSet<string>(config.ControlCenterWidgets, StringComparer.OrdinalIgnoreCase);
        var hasWidgetToAdd = available.Any(widget =>
            !selectedIds.Contains(widget.Id) &&
            (widget.Size == ControlCenterWidgetSize.Wide ? 2 : 1) <= maximumSlots - slot);
        if (slot < maximumSlots && hasWidgetToAdd)
        {
            var row = slot / columns;
            var column = slot % columns;
            var min = new Vector2(
                panelMin.X + margin + column * (compactWidth + gap),
                gridTop + row * (cardHeight + gap));
            DrawControlCenterAddCard(draw, min, new Vector2(compactWidth, cardHeight), palette);
        }

        var hint = "Click the status indicators or Home bar to close";
        var hintSize = ImGui.CalcTextSize(hint);
        draw.AddText(
            new Vector2((panelMin.X + panelMax.X - hintSize.X) * 0.5f, panelMax.Y - S(27f)),
            ImGui.GetColorU32(new Vector4(0.57f, 0.59f, 0.67f, 0.92f)),
            hint);
    }

    private void DrawControlCenterWidgetCard(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 size,
        ControlCenterWidget widget,
        ThemePalette palette)
    {
        ControlCenterWidgetSnapshot snapshot;
        try
        {
            snapshot = widget.Read();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "Control Center widget {Widget} could not refresh.", widget.Id);
            snapshot = new("Unavailable", "Could not read app state", null, false);
        }

        var max = min + size;
        var active = snapshot.IsActive == true;
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        draw.AddRectFilled(min + S(new Vector2(2, 3)), max + S(new Vector2(2, 3)), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.26f)), S(18f));
        var background = active
            ? new Vector4(palette.Accent.X * 0.58f, palette.Accent.Y * 0.58f, palette.Accent.Z * 0.58f, 0.96f)
            : new Vector4(0.16f, 0.17f, 0.23f, hovered ? 0.98f : 0.92f);
        if (!snapshot.IsAvailable)
            background = new Vector4(0.14f, 0.14f, 0.17f, 0.90f);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(background), S(18f));
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, hovered ? 0.16f : 0.09f)), S(18f), ImDrawFlags.None, S(1f));
        DrawMarqueeText(
            draw,
            widget.AppId,
            min + S(new Vector2(10, 8)),
            size.X - S(42f),
            ImGui.GetColorU32(new Vector4(0.62f, 0.65f, 0.75f, 1f)));

        if (widget.Kind == ControlCenterWidgetKind.MacroPad && widget.ReadMacroPad is not null)
        {
            DrawControlCenterMacroPad(draw, min, size, widget, palette);
        }
        else if (widget.Kind == ControlCenterWidgetKind.Toggle && widget.SetToggle is not null)
        {
            var controlCenter = min + S(new Vector2(28f, 59f));
            draw.AddCircleFilled(
                controlCenter,
                S(17f),
                ImGui.GetColorU32(active ? palette.AccentHover : new Vector4(0.30f, 0.32f, 0.38f, 1f)));
            draw.AddCircle(
                controlCenter + S(new Vector2(0, 1.5f)),
                S(8f),
                ImGui.GetColorU32(Vector4.One),
                24,
                S(1.7f));
            draw.AddLine(
                controlCenter - S(new Vector2(0, 10f)),
                controlCenter - S(new Vector2(0, 1f)),
                ImGui.GetColorU32(Vector4.One),
                S(2f));
            DrawMarqueeText(
                draw,
                widget.Title,
                min + S(new Vector2(52, 36)),
                size.X - S(62f),
                ImGui.GetColorU32(Vector4.One));
            DrawMarqueeText(
                draw,
                !string.IsNullOrWhiteSpace(snapshot.Value) ? snapshot.Value : "Unavailable",
                min + S(new Vector2(52, 60)),
                size.X - S(62f),
                ImGui.GetColorU32(new Vector4(0.72f, 0.74f, 0.82f, 1f)));
            var toggleHitMin = min + S(new Vector2(7, 29));
            var toggleHitSize = new Vector2(size.X - S(14f), size.Y - S(36f));
            ImGui.SetCursorScreenPos(toggleHitMin);
            if (ImGui.InvisibleButton($"##toggle-{widget.Id}", toggleHitSize) && snapshot.IsAvailable)
                widget.SetToggle(!active);
            DrawTooltip(widget.Description);
        }
        else
        {
            DrawMarqueeText(draw, widget.Title, min + S(new Vector2(10, 29)), size.X - S(20f), ImGui.GetColorU32(new Vector4(0.76f, 0.78f, 0.86f, 1f)));
            DrawMarqueeText(draw, snapshot.Value, min + S(new Vector2(10, 50)), size.X - S(20f), ImGui.GetColorU32(Vector4.One));
            DrawMarqueeText(draw, snapshot.Detail, min + S(new Vector2(10, 70)), size.X - S(20f), ImGui.GetColorU32(new Vector4(0.62f, 0.65f, 0.74f, 1f)));
        }

        var removeMin = new Vector2(max.X - S(25f), min.Y + S(6f));
        ImGui.SetCursorScreenPos(removeMin);
        if (ImGui.InvisibleButton($"##remove-{widget.Id}", S(new Vector2(19, 19))))
        {
            config.ControlCenterWidgets.RemoveAll(id => id.Equals(widget.Id, StringComparison.OrdinalIgnoreCase));
            try { widget.Removed?.Invoke(); }
            catch (Exception ex) { DalamudServices.Log.Warning(ex, "Control Center widget {Widget} could not clear its removed state.", widget.Id); }
            saveImmediate();
        }
        var removeCenter = removeMin + S(new Vector2(9.5f, 9.5f));
        draw.AddCircleFilled(removeCenter, S(8.5f), ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.07f, hovered ? 0.62f : 0.28f)));
        draw.AddLine(removeCenter - S(new Vector2(3.5f, 0)), removeCenter + S(new Vector2(3.5f, 0)), ImGui.GetColorU32(new Vector4(0.88f, 0.89f, 0.94f, 0.95f)), S(1.3f));
        DrawTooltip($"Remove {widget.Title} from Control Center.");

    }

    private void DrawControlCenterMacroPad(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 size,
        ControlCenterWidget widget,
        ThemePalette palette)
    {
        ControlCenterMacroPadSnapshot pad;
        try { pad = widget.ReadMacroPad!(); }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "Control Center macro pad {Widget} could not refresh.", widget.Id);
            pad = new([null, null, null, null], []);
        }
        var gap = S(3f);
        var areaMin = min + S(new Vector2(7, 27));
        var cellSize = new Vector2((size.X - S(17f)) * 0.5f, S(27f));
        for (var index = 0; index < 4; index++)
        {
            var row = index / 2;
            var column = index % 2;
            var cellMin = areaMin + new Vector2(column * (cellSize.X + gap), row * (cellSize.Y + gap));
            var cellMax = cellMin + cellSize;
            var macro = index < pad.Slots.Count ? pad.Slots[index] : null;
            var hovered = ImGui.IsMouseHoveringRect(cellMin, cellMax);
            draw.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(macro is null
                ? new Vector4(0.10f, 0.11f, 0.15f, hovered ? 0.95f : 0.76f)
                : new Vector4(palette.Accent.X * 0.45f, palette.Accent.Y * 0.45f, palette.Accent.Z * 0.45f, hovered ? 1f : 0.92f)), S(7f));
            draw.AddRect(cellMin, cellMax, ImGui.GetColorU32(new Vector4(palette.AccentHover.X, palette.AccentHover.Y, palette.AccentHover.Z, hovered ? 0.75f : 0.28f)), S(7f));
            var label = macro?.Title ?? "+";
            DrawCenteredHoverMarquee(
                draw,
                label,
                cellMin,
                cellSize,
                ImGui.GetColorU32(Vector4.One),
                hovered,
                $"pad:{widget.Id}:{index}:{macro?.Id ?? "empty"}");
            ImGui.SetCursorScreenPos(cellMin);
            ImGui.InvisibleButton($"##macro-pad-{widget.Id}-{index}", cellSize);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) || macro is null && ImGui.IsItemClicked())
            {
                controlCenterMacroPickerWidgetId = widget.Id;
                controlCenterMacroPickerSlot = index;
                controlCenterMacroPickerPage = 0;
            }
            else if (macro is not null && ImGui.IsItemClicked())
            {
                widget.ActivateMacro?.Invoke(macro.Id);
            }
            DrawTooltip(macro is null ? "Click to assign" : "Right Click to edit");
        }
    }

    private void DrawControlCenterMacroPicker(
        ImDrawListPtr draw,
        Vector2 panelMin,
        Vector2 panelMax,
        ThemePalette palette,
        ControlCenterWidget widget)
    {
        var surfaceMin = panelMin + S(new Vector2(14, 10));
        var surfaceMax = panelMax - S(new Vector2(14, 34));
        draw.AddRectFilled(surfaceMin, surfaceMax, ImGui.GetColorU32(new Vector4(0.055f, 0.058f, 0.085f, 0.975f)), S(24f));
        draw.AddRect(surfaceMin, surfaceMax, ImGui.GetColorU32(new Vector4(palette.AccentHover.X, palette.AccentHover.Y, palette.AccentHover.Z, 0.20f)), S(24f), ImDrawFlags.None, S(1.2f));
        var backMin = panelMin + S(new Vector2(25, 17));
        ImGui.SetCursorScreenPos(backMin);
        if (ImGui.InvisibleButton("##macro-picker-back", S(new Vector2(44, 38))))
        {
            controlCenterMacroPickerWidgetId = string.Empty;
            controlCenterMacroPickerSlot = -1;
        }
        DrawChevron(draw, backMin + S(new Vector2(20, 19)), false, ImGui.GetColorU32(palette.AccentHover), S(7f), S(2.8f));
        draw.AddText(panelMin + S(new Vector2(78, 25)), ImGui.GetColorU32(Vector4.One), $"Choose macro for quick key {controlCenterMacroPickerSlot + 1}");
        draw.AddText(panelMin + S(new Vector2(78, 49)), ImGui.GetColorU32(new Vector4(0.68f, 0.70f, 0.78f, 1f)), "Macros come from the active MacroDeck venue profile");

        var pad = widget.ReadMacroPad?.Invoke() ?? new ControlCenterMacroPadSnapshot([null, null, null, null], []);
        const int columns = 6;
        const int pageSize = 24;
        var pageCount = Math.Max(1, (int)Math.Ceiling(pad.Available.Count / (double)pageSize));
        controlCenterMacroPickerPage = Math.Clamp(controlCenterMacroPickerPage, 0, pageCount - 1);
        var choices = pad.Available.Skip(controlCenterMacroPickerPage * pageSize).Take(pageSize).ToList();
        var gap = S(8f);
        var width = (panelMax.X - panelMin.X - S(52f) - gap * (columns - 1)) / columns;
        var size = new Vector2(width, S(64f));
        for (var index = 0; index < choices.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var min = panelMin + new Vector2(S(26f) + column * (width + gap), S(82f) + row * (size.Y + gap));
            if (min.Y + size.Y > panelMax.Y - S(42f)) break;
            var macro = choices[index];
            var hovered = ImGui.IsMouseHoveringRect(min, min + size);
            draw.AddRectFilled(min, min + size, ImGui.GetColorU32(new Vector4(0.16f, 0.17f, 0.23f, hovered ? 0.99f : 0.92f)), S(14f));
            draw.AddRect(min, min + size, ImGui.GetColorU32(new Vector4(palette.AccentHover.X, palette.AccentHover.Y, palette.AccentHover.Z, hovered ? 0.68f : 0.22f)), S(14f));
            DrawCenteredHoverMarquee(
                draw,
                macro.Title,
                min,
                size,
                ImGui.GetColorU32(Vector4.One),
                hovered,
                $"picker:{widget.Id}:{macro.Id}");
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##choose-macro-{macro.Id}", size))
            {
                widget.AssignMacro?.Invoke(controlCenterMacroPickerSlot, macro.Id);
                controlCenterMacroPickerWidgetId = string.Empty;
                controlCenterMacroPickerSlot = -1;
            }
            DrawTooltip($"Assign {macro.Title} to this quick key.");
        }
        if (pad.Available.Count == 0)
            draw.AddText(panelMin + S(new Vector2(28, 94)), ImGui.GetColorU32(new Vector4(0.70f, 0.72f, 0.80f, 1f)), "Create a MacroDeck macro in the active venue profile first.");

        if (pageCount > 1)
        {
            var navY = panelMax.Y - S(46f);
            ImGui.SetCursorScreenPos(new Vector2(panelMin.X + S(170f), navY));
            if (ImGui.Button("<##macro-page-back", S(new Vector2(34, 28))) && controlCenterMacroPickerPage > 0) controlCenterMacroPickerPage--;
            ImGui.SetCursorScreenPos(new Vector2(panelMin.X + S(212f), navY + S(5f)));
            ImGui.TextUnformatted($"{controlCenterMacroPickerPage + 1} of {pageCount}");
            ImGui.SetCursorScreenPos(new Vector2(panelMin.X + S(276f), navY));
            if (ImGui.Button(">##macro-page-next", S(new Vector2(34, 28))) && controlCenterMacroPickerPage < pageCount - 1) controlCenterMacroPickerPage++;
        }
        if (controlCenterMacroPickerSlot >= 0 && controlCenterMacroPickerSlot < pad.Slots.Count && pad.Slots[controlCenterMacroPickerSlot] is not null)
        {
            var clearMin = new Vector2(surfaceMin.X + S(12f), surfaceMax.Y - S(42f));
            ImGui.SetCursorScreenPos(clearMin);
            if (ImGui.Button("Clear quick key", S(new Vector2(120, 30))))
            {
                widget.AssignMacro?.Invoke(controlCenterMacroPickerSlot, null);
                controlCenterMacroPickerWidgetId = string.Empty;
                controlCenterMacroPickerSlot = -1;
            }
        }
    }

    private void DrawControlCenterAddCard(ImDrawListPtr draw, Vector2 min, Vector2 size, ThemePalette palette)
    {
        var max = min + size;
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        draw.AddRectFilled(min + S(new Vector2(2, 3)), max + S(new Vector2(2, 3)), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.22f)), S(18f));
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.14f, 0.15f, 0.20f, hovered ? 0.96f : 0.82f)), S(18f));
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(palette.Accent.X, palette.Accent.Y, palette.Accent.Z, hovered ? 0.62f : 0.34f)), S(18f), ImDrawFlags.None, S(1.3f));
        var center = (min + max) * 0.5f - new Vector2(0, S(6f));
        draw.AddCircleFilled(center, S(17f), ImGui.GetColorU32(hovered ? palette.AccentHover : palette.Accent));
        draw.AddLine(center - new Vector2(S(6f), 0), center + new Vector2(S(6f), 0), ImGui.GetColorU32(Vector4.One), S(2f));
        draw.AddLine(center - new Vector2(0, S(6f)), center + new Vector2(0, S(6f)), ImGui.GetColorU32(Vector4.One), S(2f));
        var label = "Add widget";
        var labelSize = ImGui.CalcTextSize(label);
        draw.AddText(new Vector2(center.X - labelSize.X * 0.5f, center.Y + S(19f)), ImGui.GetColorU32(new Vector4(0.75f, 0.77f, 0.82f, 1f)), label);
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton("##add-control-widget", size))
            {
                controlCenterPickerOpen = true;
                controlCenterPickerPage = 0;
            }
        DrawTooltip("Choose a widget to add.");
    }

    private void DrawControlCenterPicker(
        ImDrawListPtr draw,
        Vector2 panelMin,
        Vector2 panelMax,
        ThemePalette palette)
    {
        var backMin = panelMin + S(new Vector2(25, 17));
        ImGui.SetCursorScreenPos(backMin);
        if (ImGui.InvisibleButton("##control-widget-picker-back", S(new Vector2(44, 38))))
            controlCenterPickerOpen = false;
        DrawChevron(
            draw,
            backMin + S(new Vector2(20f, 19f)),
            false,
            ImGui.GetColorU32(palette.AccentHover),
            S(7f),
            S(2.8f));
        DrawTooltip("Go back to Control Center.");
        draw.AddText(panelMin + S(new Vector2(78, 25)), ImGui.GetColorU32(Vector4.One), "Add a widget");
        draw.AddText(panelMin + S(new Vector2(78, 49)), ImGui.GetColorU32(new Vector4(0.68f, 0.70f, 0.78f, 1f)), "Choose a local control or live venue summary");

        var allWidgets = appHost.GetControlCenterWidgets();
        var selected = new HashSet<string>(config.ControlCenterWidgets, StringComparer.OrdinalIgnoreCase);
        var usedSlots = allWidgets
            .Where(widget => selected.Contains(widget.Id))
            .Sum(widget => widget.Size == ControlCenterWidgetSize.Wide ? 2 : 1);
        var availableSlots = Math.Max(0, 18 - usedSlots);
        var candidates = allWidgets
            .Where(widget => !selected.Contains(widget.Id))
            .Where(widget => (widget.Size == ControlCenterWidgetSize.Wide ? 2 : 1) <= availableSlots)
            .OrderBy(widget => widget.AppId)
            .ThenBy(widget => widget.Title)
            .GroupBy(
                widget => string.IsNullOrWhiteSpace(widget.RepeatableGroup)
                    ? $"widget:{widget.Id}"
                    : $"repeatable:{widget.RepeatableGroup}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (candidates.Count == 0)
        {
            draw.AddText(panelMin + S(new Vector2(34, 98)), ImGui.GetColorU32(new Vector4(0.74f, 0.76f, 0.82f, 1f)), "All widgets that fit have been added.");
            return;
        }

        const int pageSize = 12;
        var pageCount = Math.Max(1, (int)Math.Ceiling(candidates.Count / (double)pageSize));
        controlCenterPickerPage = Math.Clamp(controlCenterPickerPage, 0, pageCount - 1);
        var pageCandidates = candidates.Skip(controlCenterPickerPage * pageSize).Take(pageSize).ToList();
        var gap = S(8f);
        var width = (panelMax.X - panelMin.X - S(52f) - gap * 5f) / 6f;
        var size = new Vector2(width, S(98f));
        var hoveredWidgetThisFrame = string.Empty;
        for (var index = 0; index < pageCandidates.Count; index++)
        {
            var column = index % 6;
            var row = index / 6;
            var min = panelMin + new Vector2(S(26f) + column * (width + gap), S(78f) + row * (size.Y + gap));
            if (min.Y + size.Y > panelMax.Y - S(12f))
                break;
            var widget = pageCandidates[index];
            var hovered = ImGui.IsMouseHoveringRect(min, min + size);
            if (hovered)
            {
                hoveredWidgetThisFrame = widget.Id;
                if (!controlCenterPickerHoveredWidgetId.Equals(widget.Id, StringComparison.OrdinalIgnoreCase))
                {
                    controlCenterPickerHoveredWidgetId = widget.Id;
                    controlCenterPickerHoverStartedAt = ImGui.GetTime();
                }
            }
            draw.AddRectFilled(min + S(new Vector2(3, 4)), min + size + S(new Vector2(3, 4)), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.24f)), S(20f));
            draw.AddRectFilled(min, min + size, ImGui.GetColorU32(new Vector4(0.16f, 0.17f, 0.23f, hovered ? 0.99f : 0.92f)), S(20f));
            draw.AddRect(min, min + size, ImGui.GetColorU32(new Vector4(1, 1, 1, hovered ? 0.18f : 0.08f)), S(20f));
            DrawMarqueeText(draw, widget.AppId, min + S(new Vector2(10, 10)), width - S(20f), ImGui.GetColorU32(new Vector4(0.67f, 0.70f, 0.80f, 1f)), hovered, controlCenterPickerHoverStartedAt, 0.18d);
            DrawMarqueeText(draw, widget.Title, min + S(new Vector2(10, 31)), width - S(20f), ImGui.GetColorU32(Vector4.One), hovered, controlCenterPickerHoverStartedAt, 0.18d);
            DrawMarqueeText(draw, widget.Description, min + S(new Vector2(10, 51)), width - S(20f), ImGui.GetColorU32(new Vector4(0.61f, 0.64f, 0.73f, 1f)), hovered, controlCenterPickerHoverStartedAt, 0.18d);
            var plus = min + new Vector2(S(21f), size.Y - S(18f));
            draw.AddCircleFilled(plus, S(11f), ImGui.GetColorU32(hovered ? palette.AccentHover : palette.Accent));
            draw.AddLine(plus - new Vector2(S(4.5f), 0), plus + new Vector2(S(4.5f), 0), ImGui.GetColorU32(Vector4.One), S(1.5f));
            draw.AddLine(plus - new Vector2(0, S(4.5f)), plus + new Vector2(0, S(4.5f)), ImGui.GetColorU32(Vector4.One), S(1.5f));
            draw.AddText(plus + S(new Vector2(18, -8)), ImGui.GetColorU32(new Vector4(0.72f, 0.74f, 0.82f, 1f)), "Add");
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##pick-{widget.Id}", size))
            {
                config.ControlCenterWidgets.Add(widget.Id);
                controlCenterPickerOpen = false;
                saveImmediate();
            }
            DrawTooltip(widget.Description);
        }
        if (string.IsNullOrEmpty(hoveredWidgetThisFrame))
            controlCenterPickerHoveredWidgetId = string.Empty;

        if (pageCount > 1)
        {
            var navY = panelMax.Y - S(48f);
            var pageText = $"{controlCenterPickerPage + 1} of {pageCount}";
            var pageTextSize = ImGui.CalcTextSize(pageText);
            draw.AddText(
                new Vector2((panelMin.X + panelMax.X - pageTextSize.X) * 0.5f, navY + S(6f)),
                ImGui.GetColorU32(new Vector4(0.72f, 0.74f, 0.80f, 1f)),
                pageText);
            var previousMin = new Vector2(panelMin.X + S(22f), navY);
            var nextMin = new Vector2(panelMax.X - S(56f), navY);
            ImGui.SetCursorScreenPos(previousMin);
            if (ImGui.InvisibleButton("##control-picker-previous", S(new Vector2(34, 30))) && controlCenterPickerPage > 0)
                controlCenterPickerPage--;
            draw.AddText(previousMin + S(new Vector2(11, 4)), ImGui.GetColorU32(Vector4.One), "‹");
            ImGui.SetCursorScreenPos(nextMin);
            if (ImGui.InvisibleButton("##control-picker-next", S(new Vector2(34, 30))) && controlCenterPickerPage < pageCount - 1)
                controlCenterPickerPage++;
            draw.AddText(nextMin + S(new Vector2(11, 4)), ImGui.GetColorU32(Vector4.One), "›");
        }
    }

    private void DrawMarqueeText(
        ImDrawListPtr draw,
        string text,
        Vector2 position,
        float maxWidth,
        uint color,
        bool animate = true,
        double animationStartedAt = 0d,
        double pauseSeconds = 1.15d)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0f)
            return;

        var textWidth = ImGui.CalcTextSize(text).X;
        if (textWidth <= maxWidth)
        {
            draw.AddText(position, color, text);
            return;
        }

        var lineHeight = ImGui.GetTextLineHeight();
        if (!animate)
        {
            draw.PushClipRect(position, position + new Vector2(maxWidth, lineHeight), true);
            draw.AddText(position, color, text);
            draw.PopClipRect();
            return;
        }

        var gap = Math.Max(S(34f), maxWidth * 0.28f);
        var loopDistance = textWidth + gap;
        var travelSeconds = Math.Max(0.85d, loopDistance / Math.Max(1f, S(25f)));
        var cycleSeconds = pauseSeconds + travelSeconds;
        var phase = Math.Max(0d, ImGui.GetTime() - animationStartedAt) % cycleSeconds;
        var offset = 0f;
        if (phase < pauseSeconds)
            offset = 0f;
        else
            offset = loopDistance * (float)((phase - pauseSeconds) / travelSeconds);

        draw.PushClipRect(position, position + new Vector2(maxWidth, lineHeight), true);
        draw.AddText(position - new Vector2(offset, 0f), color, text);
        draw.AddText(position + new Vector2(loopDistance - offset, 0f), color, text);
        draw.PopClipRect();
    }

    private void DrawCenteredHoverMarquee(
        ImDrawListPtr draw,
        string text,
        Vector2 min,
        Vector2 size,
        uint color,
        bool hovered,
        string hoverId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var padding = S(6f);
        var availableWidth = MathF.Max(1f, size.X - padding * 2f);
        var textSize = ImGui.CalcTextSize(text);
        var y = min.Y + (size.Y - textSize.Y) * 0.5f;
        if (textSize.X <= availableWidth)
        {
            draw.AddText(new Vector2(min.X + (size.X - textSize.X) * 0.5f, y), color, text);
            return;
        }

        var frame = ImGui.GetFrameCount();
        if (hovered)
        {
            if (!controlCenterMacroHoveredKeyId.Equals(hoverId, StringComparison.Ordinal) ||
                controlCenterMacroHoverFrame < frame - 1)
            {
                controlCenterMacroHoveredKeyId = hoverId;
                controlCenterMacroHoverStartedAt = ImGui.GetTime();
            }
            controlCenterMacroHoverFrame = frame;
        }
        DrawMarqueeText(
            draw,
            text,
            new Vector2(min.X + padding, y),
            availableWidth,
            color,
            hovered,
            controlCenterMacroHoverStartedAt,
            0.18d);
    }

    private void DrawHome(ThemePalette palette)
    {
        var entries = OrderedApps();
        const int columns = 5;
        var horizontalMargin = S(42f);
        var firstRowY = S(28f);
        var cellHeight = S(122f);
        var clockSize = S(108f);
        var contentSize = ImGui.GetWindowSize();
        var cellWidth = (contentSize.X - horizontalMargin * 2f) / columns;

        DrawAnalogClockWidget(
            palette,
            new Vector2(horizontalMargin + (cellWidth - clockSize) * 0.5f, firstRowY),
            new Vector2(clockSize, clockSize));

        for (var index = 0; index < entries.Count; index++)
        {
            var slot = index + 1;
            var column = slot % columns;
            var row = slot / columns;
            ImGui.SetCursorPos(new Vector2(
                horizontalMargin + column * cellWidth,
                firstRowY + row * cellHeight));
            DrawAppTile(entries[index], palette, cellWidth, S(108f));
        }

        DrawHomeDock(palette, contentSize);

        if (!string.IsNullOrWhiteSpace(draggedAppId))
        {
            var dragged = entries.FirstOrDefault(app =>
                app.Id.Equals(draggedAppId, StringComparison.OrdinalIgnoreCase));
            if (dragged is not null)
                DrawDraggedAppPreview(dragged, palette);
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            draggedAppId = string.Empty;
    }

    private void DrawAppTile(AppDescriptor app, ThemePalette palette, float width, float height)
    {
        ImGui.PushID(app.Id);
        var start = ImGui.GetCursorScreenPos();
        var end = start + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var bundled = appHost.IsAvailable(app.Id);
        var enabled = appHost.IsEnabled(app.Id);
        var failed = bundled && enabled && !appHost.IsRunning(app.Id);
        var hovered = !ControlCenterVisible && ImGui.IsMouseHoveringRect(start, end);
        var isBeingDragged = draggedAppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase);
        var isDropTarget =
            hovered &&
            !string.IsNullOrWhiteSpace(draggedAppId) &&
            !isBeingDragged;
        var targetScale = isBeingDragged
            ? 0.95f
            : isDropTarget
                ? 1.15f
                : hovered && string.IsNullOrWhiteSpace(draggedAppId)
                    ? 1.12f
                    : 1f;
        var currentScale = appTileScales.GetValueOrDefault(app.Id, 1f);
        var response = 1f - MathF.Exp(
            -13f * MathF.Max(0.001f, ImGui.GetIO().DeltaTime));
        currentScale += (targetScale - currentScale) * response;
        if (MathF.Abs(currentScale - targetScale) < 0.001f)
            currentScale = targetScale;
        appTileScales[app.Id] = currentScale;

        var baseIconSize = S(72f);
        var iconSize = baseIconSize * currentScale;
        var icon = textures.GetIcon(app);
        var iconCenter = start + new Vector2(width * 0.5f, baseIconSize * 0.5f);
        var iconMin = iconCenter - new Vector2(iconSize * 0.5f);
        var iconMax = iconMin + new Vector2(iconSize, iconSize);
        if (isDropTarget)
        {
            draw.AddRectFilled(
                start + S(new Vector2(8, -7)),
                end - S(new Vector2(8, 4)),
                ImGui.GetColorU32(new Vector4(
                    palette.Accent.X,
                    palette.Accent.Y,
                    palette.Accent.Z,
                    0.18f)),
                S(22f));
        }
        if (icon is not null)
            draw.AddImage(icon.Handle, iconMin, iconMax);
        else
        {
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(palette.Accent), S(18f));
            var initials = Initials(app.Name);
            var initialsSize = ImGui.CalcTextSize(initials);
            draw.AddText((iconMin + iconMax - initialsSize) * 0.5f, ImGui.GetColorU32(Vector4.One), initials);
        }
        if (isBeingDragged)
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.03f, 0.58f)), S(18f));

        if (bundled && !enabled)
        {
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.025f, 0.52f)), S(18f));
            var crossColor = ImGui.GetColorU32(new Vector4(0.98f, 0.20f, 0.24f, 1f));
            draw.AddLine(iconMin + S(new Vector2(13, 13)), iconMax - S(new Vector2(13, 13)), crossColor, S(5f));
            draw.AddLine(
                new Vector2(iconMax.X - S(13f), iconMin.Y + S(13f)),
                new Vector2(iconMin.X + S(13f), iconMax.Y - S(13f)),
                crossColor,
                S(5f));
        }
        else if (failed)
        {
            draw.AddRectFilled(
                iconMin,
                iconMax,
                ImGui.GetColorU32(new Vector4(0.12f, 0.025f, 0.025f, 0.58f)),
                S(18f));
            var badgeCenter = iconMax - S(new Vector2(10, 10));
            draw.AddCircleFilled(
                badgeCenter,
                S(11f),
                ImGui.GetColorU32(new Vector4(0.92f, 0.18f, 0.20f, 1f)),
                24);
            var marker = "!";
            var markerSize = ImGui.CalcTextSize(marker);
            draw.AddText(
                badgeCenter - markerSize * 0.5f,
                ImGui.GetColorU32(Vector4.One),
                marker);
        }

        DrawCenteredText(
            draw,
            app.Name,
            start.Y + baseIconSize + S(10f),
            start.X,
            end.X,
            bundled && (!enabled || failed)
                ? new Vector4(0.66f, 0.67f, 0.71f, 1f)
                : new Vector4(0.95f, 0.96f, 1f, 1f));

        ImGui.InvisibleButton("##app-tile", new Vector2(width, height));
        if (!ControlCenterVisible && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 5f))
            draggedAppId = app.Id;

        if (!ControlCenterVisible && hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!string.IsNullOrWhiteSpace(draggedAppId))
            {
                if (!draggedAppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase))
                    ReorderApp(draggedAppId, app.Id);
            }
            else if (failed)
                ShowNotice($"{app.Name} did not start. Open Settings > Apps and tap Retry.");
            else if (bundled && enabled)
            {
                activeModuleId = app.Id;
                BeginOpening(Screen.Module);
            }
            else if (!bundled)
                ShowNotice($"{app.Name} is listed in the hub but is not included in this AirTablet build.");
            else
                ShowNotice($"{app.Name} is disabled. Enable it in Settings > Apps.");
        }

        if (string.IsNullOrWhiteSpace(draggedAppId))
            DrawTooltip(!bundled
                ? $"{app.Name} is not included in this build."
                : failed
                    ? $"{app.Name} failed to start. Retry it in Settings > Apps."
                    : enabled
                    ? $"Open {app.Name}. Drag to rearrange."
                    : $"{app.Name} is disabled. Drag to rearrange.");
        ImGui.PopID();
    }

    private void DrawDraggedAppPreview(AppDescriptor app, ThemePalette palette)
    {
        var draw = ImGui.GetWindowDrawList();
        var mouse = ImGui.GetMousePos();
        var pulse = 1f + MathF.Sin((float)ImGui.GetTime() * 8f) * 0.025f;
        var iconSize = S(78f) * pulse;
        var iconMin = mouse - new Vector2(iconSize * 0.5f, iconSize + S(14f));
        var iconMax = iconMin + new Vector2(iconSize);

        draw.AddCircleFilled(
            (iconMin + iconMax) * 0.5f + S(new Vector2(4, 7)),
            iconSize * 0.56f,
            ImGui.GetColorU32(new Vector4(0, 0, 0, 0.48f)),
            40);
        draw.AddRect(
            iconMin - new Vector2(S(4f)),
            iconMax + new Vector2(S(4f)),
            ImGui.GetColorU32(palette.AccentHover),
            S(21f),
            ImDrawFlags.None,
            S(3f));

        var icon = textures.GetIcon(app);
        if (icon is not null)
            draw.AddImage(icon.Handle, iconMin, iconMax);
        else
        {
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(palette.Accent), S(18f));
            var initials = Initials(app.Name);
            var initialsSize = ImGui.CalcTextSize(initials);
            draw.AddText((iconMin + iconMax - initialsSize) * 0.5f, ImGui.GetColorU32(Vector4.One), initials);
        }

        var labelSize = ImGui.CalcTextSize(app.Name);
        var labelMin = new Vector2(
            mouse.X - labelSize.X * 0.5f - S(8f),
            iconMax.Y + S(6f));
        var labelMax = labelMin + labelSize + S(new Vector2(16, 8));
        draw.AddRectFilled(
            labelMin,
            labelMax,
            ImGui.GetColorU32(new Vector4(0.025f, 0.027f, 0.035f, 0.92f)),
            S(8f));
        draw.AddText(labelMin + S(new Vector2(8, 4)), ImGui.GetColorU32(Vector4.One), app.Name);
    }

    private void DrawAnalogClockWidget(
        ThemePalette palette,
        Vector2 localPosition,
        Vector2 size)
    {
        ImGui.SetCursorPos(localPosition);
        var min = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var side = MathF.Min(size.X, size.Y);
        var center = min + new Vector2(side * 0.5f);
        var radius = side * 0.46f;
        var faceColor = ImGui.GetColorU32(new Vector4(
            palette.SurfaceRaised.X,
            palette.SurfaceRaised.Y,
            palette.SurfaceRaised.Z,
            0.94f));
        var markerColor = ImGui.GetColorU32(new Vector4(0.92f, 0.93f, 0.97f, 0.92f));
        draw.AddCircleFilled(center, radius, faceColor, 64);

        for (var index = 0; index < 12; index++)
        {
            var angle = index * MathF.PI / 6f - MathF.PI / 2f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var innerRadius = radius - S(index % 3 == 0 ? 9f : 6f);
            draw.AddLine(
                center + direction * innerRadius,
                center + direction * (radius - S(3f)),
                markerColor,
                S(index % 3 == 0 ? 2.2f : 1.2f));
        }

        var now = DateTime.Now;
        var hourAngle = ((now.Hour % 12) + now.Minute / 60f) * MathF.PI / 6f - MathF.PI / 2f;
        var minuteAngle = (now.Minute + now.Second / 60f) * MathF.PI / 30f - MathF.PI / 2f;
        var secondAngle = now.Second * MathF.PI / 30f - MathF.PI / 2f;
        draw.AddLine(
            center,
            center + new Vector2(MathF.Cos(hourAngle), MathF.Sin(hourAngle)) * (radius * 0.48f),
            markerColor,
            S(4f));
        draw.AddLine(
            center,
            center + new Vector2(MathF.Cos(minuteAngle), MathF.Sin(minuteAngle)) * (radius * 0.70f),
            markerColor,
            S(3f));
        draw.AddLine(
            center - new Vector2(MathF.Cos(secondAngle), MathF.Sin(secondAngle)) * S(5f),
            center + new Vector2(MathF.Cos(secondAngle), MathF.Sin(secondAngle)) * (radius * 0.76f),
            ImGui.GetColorU32(palette.Accent),
            S(1.5f));
        draw.AddCircleFilled(center, S(4f), ImGui.GetColorU32(palette.Accent), 20);
    }

    private void DrawHomeDock(ThemePalette palette, Vector2 contentSize)
    {
        var dockWidth = S(680f);
        var dockHeight = S(102f);
        var localMin = new Vector2((contentSize.X - dockWidth) * 0.5f, contentSize.Y - dockHeight - S(18f));
        ImGui.SetCursorPos(localMin);
        var start = ImGui.GetCursorScreenPos();
        var end = start + new Vector2(dockWidth, dockHeight);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, end, ImGui.GetColorU32(new Vector4(
            palette.SurfaceRaised.X,
            palette.SurfaceRaised.Y,
            palette.SurfaceRaised.Z,
            0.76f)), S(25f));
        draw.AddRect(start, end, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.16f)), S(25f));

        var itemWidth = dockWidth / 5f;
        DrawDockApp(
            start,
            itemWidth,
            0,
            "discord",
            "Discord",
            new Vector4(0.35f, 0.40f, 0.95f, 1f),
            palette,
            @"Resources\Dock\Discord.png",
            DrawDiscordGlyph,
            () => OpenExternalUrl(DiscordInviteUrl, "Discord"),
            "Open the community Discord.");
        DrawDockApp(
            start,
            itemWidth,
            1,
            "settings",
            "Settings",
            new Vector4(0.43f, 0.46f, 0.56f, 1f),
            palette,
            string.Empty,
            DrawGearGlyph,
            () =>
            {
                settingsPage = SettingsPage.General;
                activeModuleId = string.Empty;
                BeginOpening(Screen.Settings);
            },
            "Open AirTablet settings.");
        DrawDockApp(
            start,
            itemWidth,
            2,
            "wiki",
            "Wiki",
            new Vector4(0.18f, 0.52f, 0.78f, 1f),
            palette,
            string.Empty,
            DrawWikiGlyph,
            () =>
            {
                activeModuleId = string.Empty;
                BeginOpening(Screen.Wiki);
            },
            "Open the AirTablet guides and troubleshooting wiki.");
        DrawDockApp(
            start,
            itemWidth,
            3,
            "feedback",
            "Feedback",
            new Vector4(0.18f, 0.68f, 0.62f, 1f),
            palette,
            string.Empty,
            DrawFeedbackGlyph,
            () =>
            {
                activeModuleId = string.Empty;
                BeginOpening(Screen.Feedback);
            },
            "Bug reports, feedback, and feature requests.");
        DrawDockApp(
            start,
            itemWidth,
            4,
            "kofi",
            "Ko-fi",
            new Vector4(0.95f, 0.32f, 0.42f, 1f),
            palette,
            @"Resources\Dock\KoFi.png",
            DrawKofiGlyph,
            () => OpenExternalUrl(KofiUrl, "Ko-fi"),
            "Support Airi on Ko-fi.");
    }

    private void DrawDockApp(
        Vector2 dockStart,
        float itemWidth,
        int index,
        string id,
        string label,
        Vector4 background,
        ThemePalette palette,
        string iconPath,
        Action<ImDrawListPtr, Vector2, Vector4> drawGlyph,
        Action clicked,
        string tooltip)
    {
        var cellMin = dockStart + new Vector2(itemWidth * index, 0);
        var baseIconSize = S(58f);
        var baseCenter = cellMin +
            new Vector2(itemWidth * 0.5f, S(10f) + baseIconSize * 0.5f);
        var baseHitMin = baseCenter -
            new Vector2(S(39f), S(34f));
        var hitSize = S(new Vector2(78, 88));
        var hovered = !ControlCenterVisible && ImGui.IsMouseHoveringRect(
            baseHitMin,
            baseHitMin + hitSize);
        var scaleKey = $"dock:{id}";
        var targetScale = hovered ? 1.12f : 1f;
        var currentScale = appTileScales.GetValueOrDefault(scaleKey, 1f);
        var response = 1f - MathF.Exp(
            -13f * MathF.Max(0.001f, ImGui.GetIO().DeltaTime));
        currentScale += (targetScale - currentScale) * response;
        if (MathF.Abs(currentScale - targetScale) < 0.001f)
            currentScale = targetScale;
        appTileScales[scaleKey] = currentScale;

        var iconSize = baseIconSize * currentScale;
        var iconMin = baseCenter - new Vector2(iconSize * 0.5f);
        var iconMax = iconMin + new Vector2(iconSize);
        var draw = ImGui.GetWindowDrawList();

        var icon = string.IsNullOrWhiteSpace(iconPath)
            ? null
            : textures.GetResourceIcon($"dock-{id}", iconPath);
        if (icon is not null)
        {
            draw.AddImageRounded(
                icon.Handle,
                iconMin,
                iconMax,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(Vector4.One),
                S(15f));
        }
        else
        {
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(background), S(15f));
            drawGlyph(draw, (iconMin + iconMax) * 0.5f, Vector4.One);
        }
        if (id.Equals("settings", StringComparison.OrdinalIgnoreCase) && HasUnreadChangelog)
            DrawNotificationBadge(
                draw,
                new Vector2(iconMax.X - S(1f), iconMin.Y + S(1f)),
                S(10f),
                palette,
                "1");
        DrawCenteredText(
            draw,
            label,
            cellMin.Y + S(10f) + baseIconSize + S(7f),
            cellMin.X,
            cellMin.X + itemWidth,
            new Vector4(0.95f, 0.96f, 1f, 1f));

        ImGui.SetCursorScreenPos(baseHitMin);
        ImGui.InvisibleButton($"##dock-{id}", hitSize);
        if (!ControlCenterVisible && ImGui.IsItemClicked())
            clicked();
        DrawTooltip(tooltip);
    }

    private bool BeginAnimatedAppViewport(
        string id,
        ThemePalette palette,
        ImGuiWindowFlags flags,
        out float contentScale)
    {
        var available = ImGui.GetContentRegionAvail();
        var (transitionScale, opacity) = GetTransitionVisual();
        var viewportSize = new Vector2(
            MathF.Max(S(240f), available.X * transitionScale),
            MathF.Max(S(150f), available.Y * transitionScale));
        var offset = (available - viewportSize) * 0.5f;
        ImGui.SetCursorPos(ImGui.GetCursorPos() + offset);

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, opacity);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            C(new Vector2(20, 16)) * transitionScale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, C(1f));
        ImGui.PushStyleColor(
            ImGuiCol.ChildBg,
            new Vector4(palette.Surface.X, palette.Surface.Y, palette.Surface.Z, 1f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.105f, 0.110f, 0.125f, 1f));
        contentScale = AppContentScale * transitionScale;
        return ImGui.BeginChild(
            id,
            viewportSize,
            false,
            flags | ImGuiWindowFlags.AlwaysUseWindowPadding);
    }

    private static void EndAnimatedAppViewport()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    private void DrawModuleScreen(ThemePalette palette)
    {
        var moduleVisible = BeginAnimatedAppViewport(
            "##module-safe-area",
            palette,
            ImGuiWindowFlags.None,
            out var contentScale);
        if (moduleVisible)
        {
            TabletAppTheme.Begin(palette, contentScale);
            TabletAppTheme.Push();
            try
            {
                ImGui.SetWindowFontScale(contentScale);
                if (appHost.CanNavigateBack(activeModuleId))
                    DrawAppBackChevron(palette);

                if (!appHost.Draw(activeModuleId))
                {
                    ImGui.TextColored(new Vector4(0.95f, 0.56f, 0.48f, 1f), "This bundled app could not be drawn.");
                    ImGui.TextWrapped("Use the home gesture and check the Dalamud log for the app error.");
                }
            }
            finally
            {
                TabletAppTheme.End();
            }
        }
        EndAnimatedAppViewport();
    }

    private void DrawWikiApp(ThemePalette palette)
    {
        var visible = BeginAnimatedAppViewport(
            "##wiki-app",
            palette,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
            out var contentScale);
        if (!visible)
        {
            EndAnimatedAppViewport();
            return;
        }

        ImGui.SetWindowFontScale(contentScale);
        TabletAppTheme.Begin(palette, contentScale);
        TabletAppTheme.Push();
        try
        {
            var headerStart = ImGui.GetCursorScreenPos();
            ImGui.SetWindowFontScale(contentScale * 1.30f);
            ImGui.TextColored(palette.AccentHover, "AirTablet Wiki");
            ImGui.SetWindowFontScale(contentScale);
            ImGui.TextColored(
                new Vector4(0.62f, 0.64f, 0.72f, 1f),
                "Quick starts, complete option references, and troubleshooting.");

            var reloadWidth = ImGui.CalcTextSize("Reload wiki").X + C(30f);
            ImGui.SetCursorScreenPos(new Vector2(
                ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - reloadWidth - C(20f),
                headerStart.Y + C(6f)));
            if (ImGui.Button("Reload wiki", new Vector2(reloadWidth, C(30f))))
                wiki.Reload();
            DrawTooltip("Reload the editable .wiki.txt files from the Resources/Wiki folder.");

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + C(8f));
            ImGui.SetNextItemWidth(C(330f));
            if (ImGui.InputTextWithHint("##wiki-search", "Search every guide...", ref wikiSearch, 100) ||
                !string.Equals(previousWikiSearch, wikiSearch, StringComparison.Ordinal))
            {
                previousWikiSearch = wikiSearch;
                selectedWikiMatch = 0;
                wikiJumpPending = true;
            }

            var searchTerms = GetWikiSearchTerms(wikiSearch);
            var articleMatchCounts = wiki.Articles.ToDictionary(
                article => article.Id,
                article => CountWikiMatches(article, searchTerms),
                StringComparer.OrdinalIgnoreCase);
            var matchedArticles = wiki.Articles
                .Where(article => articleMatchCounts[article.Id] > 0)
                .ToList();
            var selectedHasMatches = articleMatchCounts.TryGetValue(selectedWikiArticleId, out var selectedCount) &&
                                     selectedCount > 0;
            if (searchTerms.Length > 0 && matchedArticles.Count > 0 && !selectedHasMatches)
            {
                selectedWikiArticleId = matchedArticles[0].Id;
                selectedWikiMatch = 0;
                wikiJumpPending = true;
                wikiArticleScrollResetPending = true;
                wikiArticleJumpRepeatPending = true;
            }

            var selectedArticle = wiki.Articles.FirstOrDefault(item =>
                item.Id.Equals(selectedWikiArticleId, StringComparison.OrdinalIgnoreCase));
            var matchCount = selectedArticle is null
                ? 0
                : articleMatchCounts.GetValueOrDefault(selectedArticle.Id);
            var totalMatchCount = articleMatchCounts.Values.Sum();
            if (searchTerms.Length > 0)
            {
                ImGui.SameLine(0f, C(10f));
                if (totalMatchCount == 0)
                {
                    ImGui.TextDisabled("No matches");
                }
                else
                {
                    selectedWikiMatch = Math.Clamp(selectedWikiMatch, 0, matchCount - 1);
                    var selectedArticleIndex = matchedArticles.FindIndex(article =>
                        article.Id.Equals(selectedWikiArticleId, StringComparison.OrdinalIgnoreCase));
                    var matchesBeforeSelectedArticle = matchedArticles
                        .Take(Math.Max(0, selectedArticleIndex))
                        .Sum(article => articleMatchCounts[article.Id]);
                    ImGui.TextDisabled($"{matchesBeforeSelectedArticle + selectedWikiMatch + 1} of {totalMatchCount}");
                    ImGui.SameLine(0f, C(8f));
                    if (ImGui.Button("Next match", new Vector2(C(96f), 0f)))
                    {
                        if (selectedWikiMatch + 1 < matchCount)
                        {
                            selectedWikiMatch++;
                        }
                        else
                        {
                            var nextArticleIndex = (Math.Max(0, selectedArticleIndex) + 1) % matchedArticles.Count;
                            selectedWikiArticleId = matchedArticles[nextArticleIndex].Id;
                            selectedWikiMatch = 0;
                            wikiArticleScrollResetPending = true;
                            wikiArticleJumpRepeatPending = true;
                        }
                        wikiJumpPending = true;
                    }
                }
            }
            ImGui.Dummy(new Vector2(0f, C(7f)));

            var navigationWidth = C(230f);
            ImGui.PushStyleColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    palette.SurfaceRaised.X,
                    palette.SurfaceRaised.Y,
                    palette.SurfaceRaised.Z,
                    0.96f));
            if (ImGui.BeginChild("##wiki-navigation", new Vector2(navigationWidth, 0f), true))
            {
                if (wiki.Articles.Count == 0)
                {
                    ImGui.TextWrapped(wiki.Status);
                }
                else
                {
                    foreach (var category in wiki.Articles.GroupBy(article => article.Category))
                    {
                        ImGui.TextColored(palette.AccentHover, category.Key.ToUpperInvariant());
                        ImGui.Dummy(new Vector2(0f, C(3f)));
                        foreach (var article in category)
                        {
                            var selected = article.Id.Equals(selectedWikiArticleId, StringComparison.OrdinalIgnoreCase);
                            if (ImGui.Selectable($"{article.Title}##wiki-{article.Id}", selected, ImGuiSelectableFlags.None, new Vector2(0f, C(34f))))
                            {
                                selectedWikiArticleId = article.Id;
                                selectedWikiMatch = 0;
                                wikiJumpPending = true;
                                wikiArticleScrollResetPending = true;
                                wikiArticleJumpRepeatPending = true;
                            }
                            if (searchTerms.Length > 0)
                            {
                                var countText = articleMatchCounts[article.Id].ToString();
                                var countSize = ImGui.CalcTextSize(countText);
                                var rowMin = ImGui.GetItemRectMin();
                                var rowMax = ImGui.GetItemRectMax();
                                ImGui.GetWindowDrawList().AddText(
                                    new Vector2(
                                        rowMax.X - countSize.X - C(8f),
                                        rowMin.Y + (rowMax.Y - rowMin.Y - countSize.Y) * 0.5f),
                                    ImGui.GetColorU32(articleMatchCounts[article.Id] > 0
                                        ? palette.AccentHover
                                        : new Vector4(0.48f, 0.50f, 0.58f, 1f)),
                                    countText);
                            }
                            DrawTooltip(article.Summary);
                        }
                        ImGui.Dummy(new Vector2(0f, C(9f)));
                    }
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();

            ImGui.SameLine(0f, C(12f));
            ImGui.PushStyleColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    palette.Surface.X,
                    palette.Surface.Y,
                    palette.Surface.Z,
                    1f));
            selectedArticle = wiki.Articles.FirstOrDefault(item =>
                item.Id.Equals(selectedWikiArticleId, StringComparison.OrdinalIgnoreCase));
            if (ImGui.BeginChild("##wiki-article", Vector2.Zero, true))
            {
                if (wikiArticleScrollResetPending)
                {
                    ImGui.SetScrollY(0f);
                    wikiArticleScrollResetPending = false;
                }
                var article = selectedArticle;
                if (article is null)
                    ImGui.TextWrapped(wiki.Articles.Count == 0 ? wiki.Status : "Choose an article from the left.");
                else
                    DrawWikiArticle(article, palette, contentScale, searchTerms);
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
        finally
        {
            TabletAppTheme.End();
        }
        EndAnimatedAppViewport();
    }

    private void DrawWikiArticle(WikiArticle article, ThemePalette palette, float contentScale, string[] searchTerms)
    {
        var matchOrdinal = 0;
        ImGui.SetWindowFontScale(contentScale * 1.42f);
        DrawWikiText(article.Title, palette.AccentHover, palette, searchTerms, ref matchOrdinal);
        ImGui.SetWindowFontScale(contentScale);
        DrawWikiText(article.Summary, new Vector4(0.66f, 0.68f, 0.76f, 1f), palette, searchTerms, ref matchOrdinal);
        ImGui.Dummy(new Vector2(0f, C(8f)));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, C(8f)));

        foreach (var block in article.Blocks)
        {
            switch (block.Kind)
            {
                case WikiBlockKind.Heading:
                    ImGui.Dummy(new Vector2(0f, C(7f)));
                    ImGui.SetWindowFontScale(contentScale * 1.22f);
                    DrawWikiText(block.Text, palette.AccentHover, palette, searchTerms, ref matchOrdinal);
                    ImGui.SetWindowFontScale(contentScale);
                    ImGui.Dummy(new Vector2(0f, C(3f)));
                    break;
                case WikiBlockKind.Subheading:
                    ImGui.Dummy(new Vector2(0f, C(4f)));
                    DrawWikiText(block.Text, palette.Accent, palette, searchTerms, ref matchOrdinal);
                    ImGui.Dummy(new Vector2(0f, C(2f)));
                    break;
                case WikiBlockKind.Bullet:
                    ImGui.TextColored(palette.Accent, "•");
                    ImGui.SameLine();
                    DrawWikiText(block.Text, new Vector4(0.91f, 0.92f, 0.96f, 1f), palette, searchTerms, ref matchOrdinal);
                    break;
                case WikiBlockKind.Tip:
                    DrawWikiCallout("TIP", block.Text, palette.Accent, palette, searchTerms, ref matchOrdinal);
                    break;
                case WikiBlockKind.Warning:
                    DrawWikiCallout("CHECK THIS", block.Text, new Vector4(0.96f, 0.58f, 0.22f, 1f), palette, searchTerms, ref matchOrdinal);
                    break;
                case WikiBlockKind.Code:
                    ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.058f, 0.072f, 1f));
                    var codeWrapWidth = MathF.Max(C(140f), ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X * 2f - C(8f));
                    var codeHeight = ImGui.CalcTextSize(block.Text, false, codeWrapWidth).Y + ImGui.GetStyle().WindowPadding.Y * 2f + C(10f);
                    if (ImGui.BeginChild(
                            $"##wiki-code-{ImGui.GetCursorPosY()}",
                            new Vector2(0f, codeHeight),
                            true,
                            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        DrawWikiText(block.Text, new Vector4(0.91f, 0.92f, 0.96f, 1f), palette, searchTerms, ref matchOrdinal);
                    }
                    ImGui.EndChild();
                    ImGui.PopStyleColor();
                    ImGui.Dummy(new Vector2(0f, C(5f)));
                    break;
                case WikiBlockKind.Divider:
                    ImGui.Dummy(new Vector2(0f, C(4f)));
                    ImGui.Separator();
                    ImGui.Dummy(new Vector2(0f, C(4f)));
                    break;
                default:
                    DrawWikiText(block.Text, new Vector4(0.91f, 0.92f, 0.96f, 1f), palette, searchTerms, ref matchOrdinal);
                    ImGui.Dummy(new Vector2(0f, C(4f)));
                    break;
            }
        }
        ImGui.Dummy(new Vector2(0f, C(14f)));
    }

    private void DrawWikiCallout(
        string label,
        string body,
        Vector4 accent,
        ThemePalette palette,
        string[] searchTerms,
        ref int matchOrdinal)
    {
        var wrapWidth = MathF.Max(C(140f), ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X * 2f - C(8f));
        var height = ImGui.CalcTextSize(body, false, wrapWidth).Y + ImGui.GetTextLineHeight() +
                     ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y * 2f + C(10f);
        ImGui.PushStyleColor(
            ImGuiCol.ChildBg,
            new Vector4(
                palette.SurfaceRaised.X,
                palette.SurfaceRaised.Y,
                palette.SurfaceRaised.Z,
                0.96f));
        if (ImGui.BeginChild(
                $"##wiki-callout-{label}-{ImGui.GetCursorPosY()}",
                new Vector2(0f, height),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(accent, label);
            DrawWikiText(body, new Vector4(0.91f, 0.92f, 0.96f, 1f), palette, searchTerms, ref matchOrdinal);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0f, C(6f)));
    }

    private readonly record struct WikiMatchSpan(int Start, int Length);

    private static string[] GetWikiSearchTerms(string search) =>
        search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int CountWikiMatches(WikiArticle article, string[] terms) =>
        terms.Length == 0
            ? 0
            : FindWikiMatches(article.Title, terms).Count +
              FindWikiMatches(article.Summary, terms).Count +
              article.Blocks.Sum(block => FindWikiMatches(block.Text, terms).Count);

    private static List<WikiMatchSpan> FindWikiMatches(string text, string[] terms)
    {
        var matches = new List<WikiMatchSpan>();
        if (string.IsNullOrEmpty(text) || terms.Length == 0)
            return matches;

        for (var position = 0; position < text.Length;)
        {
            var length = terms
                .Where(term => position + term.Length <= text.Length &&
                               text.AsSpan(position, term.Length).Equals(term, StringComparison.OrdinalIgnoreCase))
                .Select(term => term.Length)
                .DefaultIfEmpty(0)
                .Max();
            if (length == 0)
            {
                position++;
                continue;
            }
            matches.Add(new WikiMatchSpan(position, length));
            position += length;
        }
        return matches;
    }

    private void DrawWikiText(
        string text,
        Vector4 normalColor,
        ThemePalette palette,
        string[] searchTerms,
        ref int matchOrdinal)
    {
        var matches = FindWikiMatches(text, searchTerms);
        var firstOrdinal = matchOrdinal;

        if (matches.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, normalColor);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        var originCursorY = ImGui.GetCursorPosY();
        var origin = ImGui.GetCursorScreenPos();
        var availableWidth = MathF.Max(C(80f), ImGui.GetContentRegionAvail().X);
        var lineHeight = ImGui.GetTextLineHeight();
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var x = 0f;
        var line = 0;
        var position = 0;
        var localMatch = 0;

        while (position < text.Length)
        {
            if (text[position] == '\n')
            {
                line++;
                x = 0f;
                position++;
                continue;
            }
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            var wordStart = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]))
                position++;
            var wordLength = position - wordStart;
            var word = text.Substring(wordStart, wordLength);
            var wordWidth = ImGui.CalcTextSize(word).X;
            var leadingSpace = x > 0f ? spaceWidth : 0f;
            if (x > 0f && x + leadingSpace + wordWidth > availableWidth)
            {
                line++;
                x = 0f;
                leadingSpace = 0f;
            }
            x += leadingSpace;

            var wordEnd = wordStart + wordLength;
            var segmentStart = wordStart;
            foreach (var match in matches.Where(match => match.Start >= wordStart && match.Start < wordEnd))
            {
                if (match.Start > segmentStart)
                    DrawWikiTextSegment(text[segmentStart..match.Start], normalColor, false, false);

                var isSelected = firstOrdinal + localMatch == selectedWikiMatch;
                DrawWikiTextSegment(text.Substring(match.Start, match.Length), palette.AccentHover, true, isSelected);
                if (isSelected && wikiJumpPending)
                {
                    ImGui.SetScrollY(MathF.Max(0f, originCursorY + line * lineHeight - C(8f)));
                    if (wikiArticleJumpRepeatPending)
                        wikiArticleJumpRepeatPending = false;
                    else
                        wikiJumpPending = false;
                }
                segmentStart = match.Start + match.Length;
                localMatch++;
            }
            if (segmentStart < wordEnd)
                DrawWikiTextSegment(text[segmentStart..wordEnd], normalColor, false, false);
        }

        ImGui.Dummy(new Vector2(0f, (line + 1) * lineHeight));
        matchOrdinal += matches.Count;
        return;

        void DrawWikiTextSegment(string segment, Vector4 color, bool highlighted, bool selected)
        {
            if (segment.Length == 0)
                return;
            var size = ImGui.CalcTextSize(segment);
            var at = origin + new Vector2(x, line * lineHeight);
            if (highlighted)
            {
                var background = selected
                    ? new Vector4(1.00f, 0.72f, 0.18f, 0.96f)
                    : new Vector4(palette.Accent.X, palette.Accent.Y, palette.Accent.Z, 0.28f);
                draw.AddRectFilled(at - C(new Vector2(1f, 1f)), at + size + C(new Vector2(1f, 1f)), ImGui.GetColorU32(background), C(2f));
            }
            var textColor = selected
                ? new Vector4(0.08f, 0.055f, 0.015f, 1f)
                : color;
            draw.AddText(at, ImGui.GetColorU32(textColor), segment);
            x += size.X;
        }
    }

    private void DrawFeedbackApp(ThemePalette palette)
    {
        var visible = BeginAnimatedAppViewport(
            "##feedback-app",
            palette,
            ImGuiWindowFlags.None,
            out var contentScale);
        if (visible)
        {
            ImGui.SetWindowFontScale(contentScale);
            TabletAppTheme.Begin(palette, contentScale);
            TabletAppTheme.Push();
            try
            {
                ImGui.TextWrapped("Bug reports, feedback, and feature requests are handled in Airi's community Discord.");
                ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(10f)));

                DrawFeedbackCard(
                    "Report a bug",
                    "Include the app name, what you were doing, what happened, and any relevant Dalamud log messages.");
                DrawFeedbackCard(
                    "Request a feature",
                    "Describe the problem you want solved and how you would expect the feature to work inside the tablet.");
                DrawFeedbackCard(
                    "Share feedback",
                    "Tell us what feels good, what feels confusing, or what would make the tablet easier to use.");

                ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(8f)));
                if (ImGui.Button(
                        "Join the community Discord",
                        new Vector2(TabletAppTheme.Px(230f), TabletAppTheme.Px(36f))))
                {
                    OpenExternalUrl(DiscordInviteUrl, "Discord");
                }
                DrawTooltip("Open the Discord invite in your browser.");
            }
            finally
            {
                TabletAppTheme.End();
            }
        }
        EndAnimatedAppViewport();
    }

    private void DrawWelcome(ThemePalette palette)
    {
        var visible = BeginAnimatedAppViewport(
            "##welcome-app",
            palette,
            ImGuiWindowFlags.None,
            out var contentScale);
        if (!visible)
        {
            EndAnimatedAppViewport();
            return;
        }

        ImGui.SetWindowFontScale(contentScale);
        TabletAppTheme.Begin(palette, contentScale);
        TabletAppTheme.Push();
        try
        {
            if (ImGui.BeginChild("##welcome-scroll", Vector2.Zero, false))
            {
                var apps = AvailableBundledApps();
                ImGui.TextColored(palette.AccentHover, "Welcome to AirTablet");
                ImGui.SameLine();
                ImGui.TextDisabled($"Page {welcomePage + 1} of 2");
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(7f)));

                if (welcomePage == 0)
                    DrawWelcomeAppSelectionPage(apps, palette);
                else
                    DrawWelcomeMultiMonitorPage(apps, palette);
            }
            ImGui.EndChild();
        }
        finally
        {
            TabletAppTheme.End();
        }
        EndAnimatedAppViewport();
    }

    private void DrawWelcomeAppSelectionPage(
        IReadOnlyCollection<AppDescriptor> apps,
        ThemePalette palette)
    {
        ImGui.TextColored(palette.AccentHover, "Choose your apps");
        ImGui.TextWrapped(
            "Everything starts off. Pick the apps you want now; you can change " +
            "these choices later in Settings > Apps.");
        ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(10f)));

        var columns = ImGui.GetContentRegionAvail().X >= TabletAppTheme.Px(720f)
            ? 2
            : 1;
        if (ImGui.BeginTable(
                "##welcome-app-grid",
                columns,
                ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var app in apps)
            {
                ImGui.TableNextColumn();
                DrawWelcomeAppCard(app, palette);
            }
            ImGui.EndTable();
        }

        ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(8f)));
        if (ImGui.Button(
                "Select all",
                TabletAppTheme.Px(new Vector2(112f, 32f))))
        {
            foreach (var app in apps)
                setupSelectedApps.Add(app.Id);
        }
        ImGui.SameLine();
        if (ImGui.Button(
                "Clear all",
                TabletAppTheme.Px(new Vector2(112f, 32f))))
        {
            setupSelectedApps.Clear();
        }
        ImGui.SameLine();
        if (ImGui.Button(
                "Next: Display setup",
                TabletAppTheme.Px(new Vector2(170f, 32f))))
        {
            welcomePage = 1;
        }
        ImGui.TextDisabled(
            $"{setupSelectedApps.Count} of {apps.Count} apps selected");
    }

    private void DrawWelcomeMultiMonitorPage(
        IReadOnlyCollection<AppDescriptor> apps,
        ThemePalette palette)
    {
        ImGui.TextColored(palette.AccentHover, "Use AirTablet on another monitor");
        ImGui.TextWrapped(
            "If Final Fantasy XIV is running in Borderless Windowed or Windowed mode, " +
            "Dalamud can allow plugin windows to move onto your other screens. This is " +
            "especially useful for keeping AirTablet open without covering your game.");
        ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(12f)));

        if (ImGui.BeginChild(
                "##welcome-multi-monitor-steps",
                new Vector2(0, TabletAppTheme.Px(248f)),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(palette.AccentHover, "Enable multi-monitor windows");
            ImGui.Separator();
            ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(5f)));
            ImGui.TextUnformatted("1. Enter /xlsettings in chat.");
            ImGui.TextUnformatted("2. Open the Look & Feel tab.");
            ImGui.TextWrapped("3. Enable Multi-monitor windows.");
            ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(10f)));
            ImGui.TextColored(
                new Vector4(0.66f, 0.68f, 0.74f, 1f),
                "This lets AirTablet and other plugin windows move beyond the game window. " +
                "Without it, you may need to use AirTablet's minimize button more often.");
            ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(14f)));
            if (ImGui.Button(
                    "Open Dalamud Settings",
                    TabletAppTheme.Px(new Vector2(190f, 34f))))
            {
                OpenDalamudSettings();
            }
        }
        ImGui.EndChild();

        ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(12f)));
        if (ImGui.Button(
                "Back",
                TabletAppTheme.Px(new Vector2(100f, 32f))))
        {
            welcomePage = 0;
        }
        ImGui.SameLine();
        if (ImGui.Button(
                "Finish setup",
                TabletAppTheme.Px(new Vector2(150f, 32f))))
        {
            CompleteWelcomeSetup(apps);
        }
    }

    private void OpenDalamudSettings()
    {
        try
        {
            if (!DalamudServices.CommandManager.ProcessCommand("/xlsettings"))
                ShowNotice("Dalamud Settings could not be opened.");
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(
                ex,
                "AirTablet could not run /xlsettings from the welcome screen.");
            ShowNotice("Dalamud Settings could not be opened.");
        }
    }

    private void DrawWelcomeAppCard(AppDescriptor app, ThemePalette palette)
    {
        ImGui.PushID($"welcome-{app.Id}");
        if (ImGui.BeginChild(
                "##welcome-card",
                new Vector2(-1f, TabletAppTheme.Px(132f)),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var selected = setupSelectedApps.Contains(app.Id);
            var icon = textures.GetIcon(app);
            var iconSize = TabletAppTheme.Px(48f);
            if (icon is not null)
                ImGui.Image(icon.Handle, new Vector2(iconSize));
            else
            {
                var min = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddRectFilled(
                    min,
                    min + new Vector2(iconSize),
                    ImGui.GetColorU32(palette.Accent),
                    TabletAppTheme.Px(10f));
                ImGui.Dummy(new Vector2(iconSize));
            }

            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextColored(TabletAppTheme.Text, app.Name);
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                TabletAppTheme.MutedText,
                WelcomeDescription(app));
            ImGui.PopTextWrapPos();
            if (ImGui.Checkbox("Enable this app", ref selected))
            {
                if (selected)
                    setupSelectedApps.Add(app.Id);
                else
                    setupSelectedApps.Remove(app.Id);
            }
            ImGui.EndGroup();
        }
        ImGui.EndChild();
        ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(4f)));
        ImGui.PopID();
    }

    private static string WelcomeDescription(AppDescriptor app) =>
        app.Id.ToUpperInvariant() switch
        {
            "AUTOGREET" =>
                "Detect venue visitors, organize greeting queues, and run greeting macros.",
            "BARMANAGER" =>
                "Track bar sales, venue audits, drink sessions, dice rolls, and payouts.",
            "GAMBAASSISTANT" =>
                "Run Blackjack tables and Death Roll tournaments with dealer tools.",
            "MACRODECK" =>
                "Build Stream Deck-style venue macro panels with folders, images, chat, and emotes.",
            "RAFFLEMANAGER" =>
                "Sell and manage raffle tickets, grow jackpots, and draw weighted winners.",
            "SHIFTKEEPER" =>
                "Schedule staff, track worked time, calculate payroll, and record payments.",
            "SHOPHELPER" =>
                "Purchase custom item quantities and stacks from supported game shops.",
            "SHOUTRUNNER" =>
                "Plan and run venue shout routes across selected cities, worlds, and data centres.",
            _ when !string.IsNullOrWhiteSpace(app.Tagline) => app.Tagline,
            _ => "Bundled AirTablet app.",
        };

    private void CompleteWelcomeSetup(IReadOnlyCollection<AppDescriptor> apps)
    {
        var failed = new List<string>();
        config.AppSelectionInitialized = true;
        config.EnabledApps.Clear();
        foreach (var app in apps)
        {
            var shouldEnable = setupSelectedApps.Contains(app.Id);
            if (!appHost.SetEnabled(app.Id, shouldEnable) && shouldEnable)
                failed.Add(app.Name);
        }

        config.SetupCompleted = true;
        config.LastReadChangelogVersion = ReleaseVersion;
        config.TutorialCompleted = false;
        welcomePage = 0;
        tutorialStep = TutorialStep.Home;
        settingsPage = SettingsPage.General;
        activeModuleId = string.Empty;
        saveImmediate();
        BeginOpening(Screen.Settings);
        if (failed.Count > 0)
            ShowNotice($"Could not start: {string.Join(", ", failed)}.");
    }

    private void RestartWelcomeSetup()
    {
        foreach (var id in AppHostService.BundledAppIds)
            appHost.SetEnabled(id, false);

        setupSelectedApps.Clear();
        welcomePage = 0;
        config.SetupCompleted = false;
        config.TutorialCompleted = false;
        config.AppSelectionInitialized = false;
        config.EnabledApps.Clear();
        config.Minimized = false;
        tutorialStep = TutorialStep.None;
        activeModuleId = string.Empty;
        saveImmediate();
        BeginOpening(Screen.Welcome);
    }

    private static void DrawFeedbackCard(string title, string body)
    {
        if (ImGui.BeginChild(
                $"##feedback-{title}",
                new Vector2(0, TabletAppTheme.Px(86f)),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(TabletAppTheme.AccentHover, title);
            ImGui.TextWrapped(body);
        }
        ImGui.EndChild();
        ImGui.Dummy(new Vector2(0, TabletAppTheme.Px(7f)));
    }

    private void DrawAppBackChevron(ThemePalette palette)
    {
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        DrawChevron(
            draw,
            start + C(new Vector2(16f, 14f)),
            false,
            ImGui.GetColorU32(palette.AccentHover),
            C(6f),
            C(2.6f));
        ImGui.InvisibleButton("##app-back", C(new Vector2(38, 28)));
        if (ImGui.IsItemClicked())
            appHost.NavigateBack(activeModuleId);
        DrawTooltip("Go back inside this app.");
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(0, C(28f)));
    }

    private static void DrawChevron(
        ImDrawListPtr draw,
        Vector2 center,
        bool pointsRight,
        uint color,
        float halfWidth,
        float thickness)
    {
        var direction = pointsRight ? 1f : -1f;
        var tip = center + new Vector2(direction * halfWidth, 0f);
        var top = center + new Vector2(-direction * halfWidth, -halfWidth * 1.25f);
        var bottom = center + new Vector2(-direction * halfWidth, halfWidth * 1.25f);
        draw.AddLine(top, tip, color, thickness);
        draw.AddLine(tip, bottom, color, thickness);
    }

    private void DrawSettingsApp(ThemePalette palette)
    {
        var settingsVisible = BeginAnimatedAppViewport(
            "##settings-safe-area",
            palette,
            ImGuiWindowFlags.None,
            out var contentScale);
        if (!settingsVisible)
        {
            EndAnimatedAppViewport();
            return;
        }
        ImGui.SetWindowFontScale(contentScale);
        TabletAppTheme.Begin(palette, contentScale);
        TabletAppTheme.Push();
        try
        {
            if (settingsPage == SettingsPage.Menu)
                settingsPage = SettingsPage.General;

            var available = ImGui.GetContentRegionAvail();
            var sidebarWidth = Math.Clamp(
                available.X * 0.24f,
                C(220f),
                C(270f));

            ImGui.PushStyleColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    palette.SurfaceRaised.X,
                    palette.SurfaceRaised.Y,
                    palette.SurfaceRaised.Z,
                    1f));
            if (ImGui.BeginChild(
                    "##settings-sidebar",
                    new Vector2(sidebarWidth, 0),
                    true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawSettingsSidebar(palette);
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();

            ImGui.SameLine(0, C(14f));
            ImGui.PushStyleColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    palette.Surface.X,
                    palette.Surface.Y,
                    palette.Surface.Z,
                    1f));
            if (ImGui.BeginChild(
                    "##settings-detail",
                    Vector2.Zero,
                    false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.SetWindowFontScale(contentScale * 1.22f);
                ImGui.TextUnformatted(GetSettingsPageTitle(settingsPage));
                ImGui.SetWindowFontScale(contentScale);
                ImGui.TextColored(
                    new Vector4(0.62f, 0.64f, 0.72f, 1f),
                    GetSettingsPageDescription(settingsPage));
                ImGui.Dummy(new Vector2(0, C(10f)));

                if (ImGui.BeginChild(
                        "##settings-page-scroll",
                        ImGui.GetContentRegionAvail(),
                        false))
                {
                    DrawSettingsPage(palette);
                }
                ImGui.EndChild();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
        finally
        {
            TabletAppTheme.End();
        }
        EndAnimatedAppViewport();
    }

    private void DrawSettingsMenu(ThemePalette palette)
    {
        ImGui.PushStyleColor(
            ImGuiCol.ChildBg,
            new Vector4(
                palette.SurfaceRaised.X,
                palette.SurfaceRaised.Y,
                palette.SurfaceRaised.Z,
                1f));
        var listVisible = ImGui.BeginChild(
            "##settings-list",
            ImGui.GetContentRegionAvail(),
            true);
        ImGui.PopStyleColor();
        if (listVisible)
        {
            DrawSettingsRow("General", "Welcome setup", palette.Accent, @"Resources\Settings\General.png", SettingsPage.General);
            DrawSettingsRow("Appearance", $"{config.Theme} · {config.TabletSize}", palette.Accent, @"Resources\Settings\Appearance.png", SettingsPage.Appearance);
            DrawSettingsRow("Apps", $"{appHost.AvailableAppIds.Count} installed", new Vector4(0.26f, 0.72f, 0.46f, 1f), @"Resources\Settings\Apps.png", SettingsPage.Apps);
            DrawSettingsRow("What's New", "Bundled updates", new Vector4(0.56f, 0.36f, 0.96f, 1f), @"Resources\Settings\WhatsNew.png", SettingsPage.WhatsNew);
            DrawSettingsRow("Status Bar", config.Use24HourClock ? "24-hour clock" : "12-hour clock", new Vector4(0.20f, 0.58f, 0.96f, 1f), @"Resources\Settings\StatusBar.png", SettingsPage.StatusBar);
            DrawSettingsRow("Migrate Configs", "Import original plugins", new Vector4(0.90f, 0.53f, 0.18f, 1f), @"Resources\Settings\Migration.png", SettingsPage.Migration);
            DrawSettingsRow("About", $"AirTabOS {ReleaseVersion}", new Vector4(0.48f, 0.53f, 0.66f, 1f), @"Resources\Settings\About.png", SettingsPage.About, false);
        }
        ImGui.EndChild();
    }

    private void DrawSettingsSidebar(ThemePalette palette)
    {
        ImGui.TextDisabled("AIRTABLET");
        ImGui.TextUnformatted("Settings");
        ImGui.Dummy(new Vector2(0, C(12f)));

        DrawSettingsSidebarItem(
            "General",
            @"Resources\Settings\General.png",
            palette.Accent,
            SettingsPage.General,
            palette);
        DrawSettingsSidebarItem(
            "Appearance",
            @"Resources\Settings\Appearance.png",
            palette.Accent,
            SettingsPage.Appearance,
            palette);
        DrawSettingsSidebarItem(
            "Apps",
            @"Resources\Settings\Apps.png",
            new Vector4(0.26f, 0.72f, 0.46f, 1f),
            SettingsPage.Apps,
            palette);
        DrawSettingsSidebarItem(
            "What's New",
            @"Resources\Settings\WhatsNew.png",
            new Vector4(0.56f, 0.36f, 0.96f, 1f),
            SettingsPage.WhatsNew,
            palette);
        DrawSettingsSidebarItem(
            "Status Bar",
            @"Resources\Settings\StatusBar.png",
            new Vector4(0.20f, 0.58f, 0.96f, 1f),
            SettingsPage.StatusBar,
            palette);
        DrawSettingsSidebarItem(
            "Migrate Configs",
            @"Resources\Settings\Migration.png",
            new Vector4(0.90f, 0.53f, 0.18f, 1f),
            SettingsPage.Migration,
            palette);
        DrawSettingsSidebarItem(
            "About",
            @"Resources\Settings\About.png",
            new Vector4(0.48f, 0.53f, 0.66f, 1f),
            SettingsPage.About,
            palette,
            false);
    }

    private void DrawSettingsSidebarItem(
        string label,
        string iconPath,
        Vector4 fallbackColor,
        SettingsPage page,
        ThemePalette palette,
        bool drawSeparator = true)
    {
        ImGui.PushID($"settings-sidebar-{page}");
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, C(54f));
        var end = start + size;
        var selected = settingsPage == page;
        var hovered = ImGui.IsMouseHoveringRect(start, end);
        var draw = ImGui.GetWindowDrawList();

        if (selected || hovered)
        {
            var background = selected
                ? new Vector4(
                    palette.Accent.X,
                    palette.Accent.Y,
                    palette.Accent.Z,
                    0.24f)
                : new Vector4(1f, 1f, 1f, 0.055f);
            draw.AddRectFilled(start, end, ImGui.GetColorU32(background), C(10f));
        }

        var iconMin = start + C(new Vector2(10f, 10f));
        var iconMax = iconMin + C(new Vector2(34f, 34f));
        var icon = textures.GetResourceIcon($"settings-sidebar-icon-{page}", iconPath);
        if (icon is not null)
        {
            draw.AddImageRounded(
                icon.Handle,
                iconMin,
                iconMax,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(Vector4.One),
                C(7f));
        }
        else
        {
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(fallbackColor), C(7f));
        }
        if (page == SettingsPage.WhatsNew && HasUnreadChangelog)
            DrawNotificationBadge(
                draw,
                new Vector2(iconMax.X - C(1f), iconMin.Y + C(1f)),
                C(9f),
                palette,
                "1");

        var labelSize = ImGui.CalcTextSize(label);
        draw.AddText(
            new Vector2(
                iconMax.X + C(10f),
                start.Y + (size.Y - labelSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(0.94f, 0.95f, 0.99f, 1f)),
            label);
        DrawChevron(
            draw,
            new Vector2(end.X - C(14f), start.Y + size.Y * 0.5f),
            true,
            ImGui.GetColorU32(selected
                ? palette.AccentHover
                : new Vector4(0.68f, 0.70f, 0.77f, 1f)),
            C(5.5f),
            C(2.3f));

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.InvisibleButton("##select-page", size);
        ImGui.PopStyleVar();
        if (ImGui.IsItemClicked())
        {
            settingsPage = page;
            if (page == SettingsPage.WhatsNew)
                MarkChangelogRead();
            if (page == SettingsPage.WhatsNew &&
                changelog.Items.Count == 0 &&
                !changelog.IsRefreshing)
            {
                _ = changelog.RefreshAsync(catalog.Apps);
            }
        }
        if (drawSeparator)
        {
            var separatorGap = C(3f);
            var separatorY = end.Y + separatorGap;
            draw.AddLine(
                new Vector2(start.X + C(7f), separatorY),
                new Vector2(end.X - C(7f), separatorY),
                ImGui.GetColorU32(new Vector4(0.27f, 0.28f, 0.34f, 0.82f)),
                C(1f));
            ImGui.SetCursorScreenPos(
                new Vector2(start.X, end.Y + separatorGap * 2f));
        }
        ImGui.PopID();
    }

    private void DrawSettingsPage(ThemePalette palette)
    {
        switch (settingsPage)
        {
            case SettingsPage.General:
                DrawGeneralSettings(palette);
                break;
            case SettingsPage.Appearance:
                DrawAppearanceSettings(palette);
                break;
            case SettingsPage.Apps:
                DrawAppSettings(palette);
                break;
            case SettingsPage.WhatsNew:
                DrawWhatsNew(palette);
                break;
            case SettingsPage.StatusBar:
                DrawStatusSettings(palette);
                break;
            case SettingsPage.Migration:
                DrawMigrationSettings(palette);
                break;
            case SettingsPage.About:
                DrawAboutSettings(palette);
                break;
            default:
                DrawGeneralSettings(palette);
                break;
        }
    }

    private static string GetSettingsPageTitle(SettingsPage page) =>
        page switch
        {
            SettingsPage.General => "General",
            SettingsPage.Appearance => "Appearance",
            SettingsPage.Apps => "Apps",
            SettingsPage.WhatsNew => "What's New",
            SettingsPage.StatusBar => "Status Bar",
            SettingsPage.Migration => "Migrate Configs",
            SettingsPage.About => "About AirTablet",
            _ => "Settings",
        };

    private static string GetSettingsPageDescription(SettingsPage page) =>
        page switch
        {
            SettingsPage.General => "Run the welcome setup again when needed.",
            SettingsPage.Appearance => "Personalize the tablet theme and wallpaper.",
            SettingsPage.Apps => "Choose which bundled apps appear and run on your tablet.",
            SettingsPage.WhatsNew => "Review changes included with AirTablet and its bundled apps.",
            SettingsPage.StatusBar => "Control the clock and battery information shown at the top.",
            SettingsPage.Migration => "Bring settings across from the original standalone plugins.",
            SettingsPage.About => "Version, support, and project information.",
            _ => string.Empty,
        };

    private static void DrawSettingsGroupLabel(string label)
    {
        ImGui.TextColored(
            new Vector4(0.58f, 0.60f, 0.68f, 1f),
            label.ToUpperInvariant());
        ImGui.Dummy(new Vector2(0, C(3f)));
    }

    private static bool BeginSettingsGroup(
        string id,
        float height,
        ThemePalette palette,
        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)
    {
        ImGui.PushStyleColor(
            ImGuiCol.ChildBg,
            new Vector4(
                palette.SurfaceRaised.X,
                palette.SurfaceRaised.Y,
                palette.SurfaceRaised.Z,
                0.92f));
        var visible = ImGui.BeginChild(
            id,
            new Vector2(0, C(height)),
            true,
            flags);
        ImGui.PopStyleColor();
        return visible;
    }

    private static void EndSettingsGroup(float spacing = 8f)
    {
        ImGui.EndChild();
        ImGui.Dummy(new Vector2(0, C(spacing)));
    }

    private static bool DrawSettingsToggleRow(
        string id,
        string label,
        ref bool value,
        bool drawSeparator = true)
    {
        ImGui.PushID(id);
        var start = ImGui.GetCursorScreenPos();
        var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, C(52f));
        var draw = ImGui.GetWindowDrawList();
        var labelSize = ImGui.CalcTextSize(label);
        draw.AddText(
            new Vector2(
                start.X + C(4f),
                start.Y + (rowSize.Y - labelSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(0.94f, 0.95f, 0.99f, 1f)),
            label);

        var pillSize = C(new Vector2(48f, 26f));
        var pillMin = start + new Vector2(
            rowSize.X - pillSize.X - C(4f),
            (rowSize.Y - pillSize.Y) * 0.5f);
        var pillMax = pillMin + pillSize;
        draw.AddRectFilled(
            pillMin,
            pillMax,
            ImGui.GetColorU32(value
                ? new Vector4(0.18f, 0.72f, 0.36f, 1f)
                : new Vector4(0.42f, 0.43f, 0.47f, 1f)),
            pillSize.Y * 0.5f);
        var knobCenter = new Vector2(
            value ? pillMax.X - C(14f) : pillMin.X + C(14f),
            (pillMin.Y + pillMax.Y) * 0.5f);
        draw.AddCircleFilled(
            knobCenter,
            C(11f),
            ImGui.GetColorU32(new Vector4(0.96f, 0.97f, 0.99f, 1f)),
            24);

        ImGui.SetCursorScreenPos(pillMin);
        ImGui.InvisibleButton("##toggle", pillSize);
        var changed = ImGui.IsItemClicked();
        if (changed)
            value = !value;

        if (drawSeparator)
        {
            draw.AddLine(
                start + C(new Vector2(8f, 51f)),
                start + new Vector2(rowSize.X - C(8f), C(51f)),
                ImGui.GetColorU32(new Vector4(0.26f, 0.27f, 0.31f, 0.82f)),
                C(1f));
        }
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(rowSize);
        ImGui.PopID();
        return changed;
    }

    private void DrawGeneralSettings(ThemePalette palette)
    {
        DrawSettingsGroupLabel("Setup");
        if (BeginSettingsGroup(
                "##settings-onboarding-group",
                68f,
                palette))
        {
            var rowStart = ImGui.GetCursorScreenPos();
            var rowHeight = C(34f);
            var labelSize = ImGui.CalcTextSize("Welcome setup");
            ImGui.SetCursorScreenPos(new Vector2(
                rowStart.X,
                rowStart.Y + (rowHeight - labelSize.Y) * 0.5f));
            ImGui.TextUnformatted("Welcome setup");
            var buttonWidth = C(154f);
            ImGui.SetCursorScreenPos(new Vector2(
                rowStart.X + ImGui.GetContentRegionAvail().X - buttonWidth,
                rowStart.Y));
            if (ImGui.Button(
                    "Run welcome setup",
                    new Vector2(buttonWidth, rowHeight)))
            {
                welcomeSetupConfirmationPending = true;
                TabletAppTheme.OpenCenteredModal(
                    "Run welcome setup again?##airtablet-repeat-welcome");
            }
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("Minimized tablet");
        const float miniTabletGroupHeight = 274f;
        if (BeginSettingsGroup(
                "##settings-mini-tablet-group",
                miniTabletGroupHeight,
                palette))
        {
            var anchorMini = config.AnchorMiniToCollapseCorner;
            if (DrawSettingsToggleRow(
                    "anchor-mini",
                    "Anchor mini tablet to the selected collapse corner",
                    ref anchorMini))
            {
                config.AnchorMiniToCollapseCorner = anchorMini;
                save();
            }

            ImGui.TextUnformatted("Collapse corner");
            ImGui.TextColored(
                anchorMini
                    ? new Vector4(0.62f, 0.64f, 0.72f, 1f)
                    : new Vector4(0.43f, 0.44f, 0.50f, 1f),
                anchorMini
                    ? "Choose which corner stays in place when the tablet minimizes."
                    : "Turn on corner anchoring to choose a collapse corner.");
            ImGui.Dummy(new Vector2(0, C(5f)));
            DrawMiniCollapseCornerPicker(palette, anchorMini);
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("Startup");
        if (BeginSettingsGroup(
                "##settings-startup-group",
                250f,
                palette))
        {
            var showStartupAnimation = config.ShowStartupAnimation;
            if (DrawSettingsToggleRow(
                    "startup-animation",
                    "Show AirTablet loading animation",
                    ref showStartupAnimation,
                    true))
            {
                config.ShowStartupAnimation = showStartupAnimation;
                save();
            }

            var showBeforeLogin = config.ShowBeforeCharacterLogin;
            if (DrawSettingsToggleRow(
                    "show-before-character-login",
                    "Show AirTablet before character login",
                    ref showBeforeLogin,
                    false))
            {
                config.ShowBeforeCharacterLogin = showBeforeLogin;
                save();
            }

            ImGui.Dummy(new Vector2(0f, C(8f)));
            ImGui.TextUnformatted("Opening size");
            ImGui.Dummy(new Vector2(0f, C(4f)));
            var openingButtonWidth = MathF.Max(
                C(100f),
                (ImGui.GetContentRegionAvail().X - C(16f)) / 3f);
            foreach (var option in new[]
                     {
                         (Value: "RememberLast", Label: "Remember last size"),
                         (Value: "Full", Label: "Open full size"),
                         (Value: "Mini", Label: "Open mini"),
                     })
            {
                var selected = config.StartupTabletMode.Equals(option.Value, StringComparison.OrdinalIgnoreCase);
                if (selected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, palette.Accent);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, palette.AccentHover);
                }
                if (ImGui.Button(option.Label, new Vector2(openingButtonWidth, C(30f))))
                {
                    config.StartupTabletMode = option.Value;
                    save();
                }
                if (selected)
                    ImGui.PopStyleColor(2);
                if (option.Value != "Mini")
                    ImGui.SameLine(0f, C(8f));
            }
            ImGui.Dummy(new Vector2(0f, C(10f)));
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("AirTabOS help");
        if (BeginSettingsGroup(
                "##settings-airtabos-help-group",
                84f,
                palette))
        {
            var showOsTooltips = config.ShowAirTabOsTooltips;
            if (DrawSettingsToggleRow(
                    "airtabos-tooltips",
                    "Show AirTabOS tooltips",
                    ref showOsTooltips,
                    false))
            {
                config.ShowAirTabOsTooltips = showOsTooltips;
                save();
            }
        }
        EndSettingsGroup();
        DrawWelcomeSetupConfirmation();
    }

    private void DrawWelcomeSetupConfirmation()
    {
        if (!welcomeSetupConfirmationPending)
            return;

        const string modalName =
            "Run welcome setup again?##airtablet-repeat-welcome";
        TabletAppTheme.OpenCenteredModal(modalName);
        if (!TabletAppTheme.BeginCenteredModal(
                modalName,
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + C(430f));
        ImGui.TextUnformatted(
            "Run the welcome setup again? This disables the current app selections and restarts onboarding and the control tutorial. The bundled apps' own configurations and saved data are not changed.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Run setup", C(new Vector2(120f, 0f))))
        {
            welcomeSetupConfirmationPending = false;
            RestartWelcomeSetup();
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", C(new Vector2(100f, 0f))))
        {
            welcomeSetupConfirmationPending = false;
            TabletAppTheme.CloseCenteredModal();
        }

        TabletAppTheme.EndCenteredModal();
    }

    private void DrawMiniCollapseCornerPicker(
        ThemePalette palette,
        bool enabled)
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var diagramSize = C(new Vector2(260f, 110f));
        var rowStart = ImGui.GetCursorScreenPos();
        var diagramMin = rowStart + new Vector2(
            MathF.Max(0f, (availableWidth - diagramSize.X) * 0.5f),
            0f);
        var diagramMax = diagramMin + diagramSize;
        var draw = ImGui.GetWindowDrawList();
        var frameColor = ImGui.GetColorU32(
            enabled
                ? new Vector4(0.50f, 0.52f, 0.60f, 1f)
                : new Vector4(0.30f, 0.31f, 0.36f, 1f));
        var screenColor = ImGui.GetColorU32(
            enabled
                ? new Vector4(
                    palette.Surface.X,
                    palette.Surface.Y,
                    palette.Surface.Z,
                    1f)
                : new Vector4(0.10f, 0.105f, 0.12f, 1f));

        draw.AddRectFilled(
            diagramMin,
            diagramMax,
            ImGui.GetColorU32(new Vector4(0.05f, 0.055f, 0.065f, 1f)),
            C(12f));
        draw.AddRect(
            diagramMin,
            diagramMax,
            frameColor,
            C(12f),
            ImDrawFlags.None,
            C(2f));
        draw.AddRectFilled(
            diagramMin + C(new Vector2(12f, 10f)),
            diagramMax - C(new Vector2(12f, 10f)),
            screenColor,
            C(7f));

        var corners = new[]
        {
            ("TopLeft", "top-left", diagramMin + C(new Vector2(24f, 22f))),
            ("TopRight", "top-right", new Vector2(diagramMax.X - C(24f), diagramMin.Y + C(22f))),
            ("BottomLeft", "bottom-left", new Vector2(diagramMin.X + C(24f), diagramMax.Y - C(22f))),
            ("BottomRight", "bottom-right", diagramMax - C(new Vector2(24f, 22f))),
        };

        foreach (var (value, label, center) in corners)
        {
            var selected =
                enabled &&
                config.MiniCollapseCorner.Equals(
                    value,
                    StringComparison.Ordinal);
            draw.AddCircleFilled(
                center,
                C(11f),
                ImGui.GetColorU32(selected
                    ? palette.Accent
                    : enabled
                        ? new Vector4(0.24f, 0.25f, 0.30f, 1f)
                        : new Vector4(0.18f, 0.185f, 0.21f, 1f)),
                24);
            draw.AddCircle(
                center,
                C(11f),
                ImGui.GetColorU32(selected
                    ? palette.AccentHover
                    : enabled
                        ? new Vector4(0.70f, 0.71f, 0.76f, 1f)
                        : new Vector4(0.38f, 0.39f, 0.44f, 1f)),
                24,
                C(1.5f));
            if (selected)
            {
                draw.AddLine(
                    center + C(new Vector2(-5f, 0f)),
                    center + C(new Vector2(-1f, 4f)),
                    ImGui.GetColorU32(Vector4.One),
                    C(2f));
                draw.AddLine(
                    center + C(new Vector2(-1f, 4f)),
                    center + C(new Vector2(6f, -5f)),
                    ImGui.GetColorU32(Vector4.One),
                    C(2f));
            }

            ImGui.SetCursorScreenPos(center - C(new Vector2(14f, 14f)));
            if (ImGui.InvisibleButton(
                    $"##mini-collapse-{value}",
                    C(new Vector2(28f, 28f))) &&
                enabled)
            {
                config.MiniCollapseCorner = value;
                save();
            }
            DrawTooltip(enabled
                ? $"Keep the {label} corner in place when minimizing."
                : "Turn on corner anchoring to select a collapse corner.");
        }

        ImGui.SetCursorScreenPos(new Vector2(
            rowStart.X,
            diagramMax.Y));
        ImGui.Dummy(new Vector2(0f, C(1f)));
    }

    private void DrawAppSettings(ThemePalette palette)
    {
        var apps = AvailableBundledApps();
        DrawSettingsGroupLabel("Bundled apps");
        var groupHeight =
            apps.Count * C(58f) +
            Math.Max(0, apps.Count - 1) * ImGui.GetStyle().ItemSpacing.Y +
            ImGui.GetStyle().WindowPadding.Y * 2f +
            ImGui.GetStyle().ChildBorderSize * 2f +
            C(4f);
        if (BeginSettingsGroup(
                "##settings-app-list",
                groupHeight,
                palette))
        {
            for (var index = 0; index < apps.Count; index++)
                DrawAppToggleRow(
                    apps[index],
                    palette,
                    index < apps.Count - 1);
        }
        EndSettingsGroup();
        ImGui.TextColored(
            new Vector4(0.58f, 0.60f, 0.68f, 1f),
            "Disabled apps stay on the Home screen with a red X.");
    }

    private void DrawAppToggleRow(
        AppDescriptor app,
        ThemePalette palette,
        bool drawSeparator)
    {
        ImGui.PushID($"toggle-{app.Id}");
        var start = ImGui.GetCursorScreenPos();
        var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, C(58f));
        var draw = ImGui.GetWindowDrawList();
        var icon = textures.GetIcon(app);
        var iconMin = start + C(new Vector2(10f, 11f));
        var iconMax = iconMin + C(new Vector2(36f, 36f));
        if (icon is not null)
        {
            draw.AddImageRounded(
                icon.Handle,
                iconMin,
                iconMax,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(Vector4.One),
                C(8f));
        }
        else
        {
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(palette.Accent), C(8f));
        }

        draw.AddText(
            start + C(new Vector2(56f, 9f)),
            ImGui.GetColorU32(new Vector4(0.94f, 0.95f, 0.99f, 1f)),
            app.Name);

        var enabled = appHost.IsEnabled(app.Id);
        var running = appHost.IsRunning(app.Id);
        var error = appHost.GetError(app.Id);
        var status = !enabled ? "Disabled" : running ? "Running" : "Startup failed";
        var statusColor = !enabled
            ? new Vector4(0.62f, 0.63f, 0.68f, 1f)
            : running
                ? new Vector4(0.34f, 0.82f, 0.49f, 1f)
                : new Vector4(0.96f, 0.36f, 0.36f, 1f);
        draw.AddText(
            start + C(new Vector2(56f, 31f)),
            ImGui.GetColorU32(statusColor),
            status);

        var pillSize = C(new Vector2(48, 26));
        var pillMin = start + new Vector2(
            rowSize.X - pillSize.X - C(10f),
            (rowSize.Y - pillSize.Y) * 0.5f);
        var pillMax = pillMin + pillSize;
        draw.AddRectFilled(
            pillMin,
            pillMax,
            ImGui.GetColorU32(enabled
                ? new Vector4(0.18f, 0.72f, 0.36f, 1f)
                : new Vector4(0.42f, 0.43f, 0.47f, 1f)),
            C(14f));
        var knobCenter = new Vector2(
            enabled ? pillMax.X - C(14f) : pillMin.X + C(14f),
            (pillMin.Y + pillMax.Y) * 0.5f);
        draw.AddCircleFilled(knobCenter + C(new Vector2(0, 1)), C(11f), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.20f)), 24);
        draw.AddCircleFilled(knobCenter, C(11f), ImGui.GetColorU32(new Vector4(0.96f, 0.97f, 0.99f, 1f)), 24);
        if (enabled && !running)
        {
            var retrySize = C(new Vector2(58, 26));
            var retryMin = pillMin - new Vector2(retrySize.X + C(8f), 0);
            draw.AddRectFilled(
                retryMin,
                retryMin + retrySize,
                ImGui.GetColorU32(new Vector4(
                    palette.Accent.X,
                    palette.Accent.Y,
                    palette.Accent.Z,
                    0.88f)),
                retrySize.Y * 0.5f);
            var retryText = "Retry";
            var retryTextSize = ImGui.CalcTextSize(retryText);
            draw.AddText(
                retryMin + (retrySize - retryTextSize) * 0.5f,
                ImGui.GetColorU32(Vector4.One),
                retryText);
            ImGui.SetCursorScreenPos(retryMin);
            if (ImGui.InvisibleButton("##retry-app", retrySize))
            {
                if (appHost.Retry(app.Id))
                    ShowNotice($"{app.Name} started successfully.");
                else
                    ShowNotice($"{app.Name} still could not start. Check the Dalamud log.");
            }
            if (!string.IsNullOrWhiteSpace(error))
                DrawTooltip(error);
        }

        ImGui.SetCursorScreenPos(pillMin);
        if (ImGui.InvisibleButton("##app-enabled", pillSize))
        {
            if (!appHost.SetEnabled(app.Id, !enabled))
                ShowNotice($"{app.Name} could not be enabled. Check the Dalamud log.");
            saveImmediate();
        }
        DrawTooltip(enabled ? $"Disable {app.Name}." : $"Enable {app.Name}.");
        if (drawSeparator)
        {
            draw.AddLine(
                start + C(new Vector2(8f, 57f)),
                start + new Vector2(rowSize.X - C(8f), C(57f)),
                ImGui.GetColorU32(new Vector4(0.26f, 0.27f, 0.31f, 0.82f)),
                C(1f));
        }
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(rowSize);
        ImGui.PopID();
    }

    private void DrawSettingsRow(
        string label,
        string value,
        Vector4 color,
        string iconPath,
        SettingsPage? target,
        bool drawSeparator = true)
    {
        var rowStart = ImGui.GetCursorScreenPos();
        var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, C(64f));
        var draw = ImGui.GetWindowDrawList();
        var iconMin = rowStart + C(new Vector2(12, 11));
        var iconMax = rowStart + C(new Vector2(48, 47));
        var icon = textures.GetResourceIcon($"settings-{label}", iconPath);
        if (icon is not null)
        {
            draw.AddImageRounded(
                icon.Handle,
                iconMin,
                iconMax,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(Vector4.One),
                C(8f));
        }
        else
        {
            draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(color), C(8f));
            var letter = label[..1];
            var letterSize = ImGui.CalcTextSize(letter);
            draw.AddText(
                rowStart + C(new Vector2(30, 29)) - letterSize * 0.5f,
                ImGui.GetColorU32(Vector4.One),
                letter);
        }
        draw.AddText(rowStart + C(new Vector2(61, 21)), ImGui.GetColorU32(new Vector4(0.94f, 0.95f, 0.99f, 1f)), label);
        var valueSize = ImGui.CalcTextSize(value);
        draw.AddText(rowStart + new Vector2(rowSize.X - valueSize.X - C(33f), C(21f)), ImGui.GetColorU32(new Vector4(0.61f, 0.62f, 0.69f, 1f)), value);
        if (target is not null)
        {
            var arrowX = rowStart.X + rowSize.X - C(17f);
            draw.AddLine(new Vector2(arrowX - C(3f), rowStart.Y + C(27f)), new Vector2(arrowX + C(2f), rowStart.Y + C(32f)), ImGui.GetColorU32(new Vector4(0.68f, 0.70f, 0.77f, 1f)), C(2f));
            draw.AddLine(new Vector2(arrowX + C(2f), rowStart.Y + C(32f)), new Vector2(arrowX - C(3f), rowStart.Y + C(37f)), ImGui.GetColorU32(new Vector4(0.68f, 0.70f, 0.77f, 1f)), C(2f));
        }
        if (drawSeparator)
        {
            draw.AddLine(
                rowStart + C(new Vector2(12, 63)),
                rowStart + new Vector2(rowSize.X - C(12f), C(63f)),
                ImGui.GetColorU32(new Vector4(0.24f, 0.25f, 0.29f, 1f)));
        }
        ImGui.InvisibleButton($"##settings-{label}", rowSize);
        if (target is not null && ImGui.IsItemClicked())
        {
            settingsPage = target.Value;
            if (target == SettingsPage.WhatsNew)
                MarkChangelogRead();
            if (target == SettingsPage.WhatsNew && changelog.Items.Count == 0 && !changelog.IsRefreshing)
                _ = changelog.RefreshAsync(catalog.Apps);
        }
    }

    private void DrawAppearanceSettings(ThemePalette palette)
    {
        DrawSettingsGroupLabel("Theme colour");
        if (BeginSettingsGroup(
                "##appearance-theme-group",
                64f,
                palette))
        {
            foreach (var candidate in ThemePalette.All)
            {
                var selected = candidate.Name.Equals(config.Theme, StringComparison.OrdinalIgnoreCase);
                var start = ImGui.GetCursorScreenPos();
                var size = C(new Vector2(50, 30));
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(start, start + size, ImGui.GetColorU32(candidate.Accent), C(15f));
                if (selected)
                    draw.AddRect(start - new Vector2(C(1f)), start + size + new Vector2(C(1f)), ImGui.GetColorU32(Vector4.One), C(16f), ImDrawFlags.None, C(2f));
                ImGui.InvisibleButton($"##theme-{candidate.Name}", size);
                if (ImGui.IsItemClicked())
                {
                    config.Theme = candidate.Name;
                    save();
                }
                DrawTooltip(candidate.Name);
                if (candidate != ThemePalette.All[^1])
                    ImGui.SameLine();
            }
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("Wallpaper");
        if (BeginSettingsGroup(
                "##appearance-wallpaper-group",
                132f,
                palette))
        {
        ImGui.TextColored(
            new Vector4(0.62f, 0.64f, 0.72f, 1f),
            string.IsNullOrWhiteSpace(config.WallpaperPath) ? "Default AirTablet wallpaper" : Path.GetFileName(config.WallpaperPath));
        if (ImGui.Button("Choose image", C(new Vector2(124, 30))))
        {
            dialogs.PickWallpaper(path =>
            {
                config.WallpaperPath = path;
                textures.InvalidateWallpaper();
                save();
            });
        }
        DrawTooltip("A 16:9 image will work best as an AirTablet wallpaper.");
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(config.WallpaperPath));
        if (ImGui.Button("Clear", C(new Vector2(76, 30))))
        {
            config.WallpaperPath = string.Empty;
            textures.InvalidateWallpaper();
            save();
        }
        ImGui.EndDisabled();

        var opacity = config.WallpaperOpacity;
        ImGui.SetNextItemWidth(C(300f));
        if (ImGui.SliderFloat("Wallpaper opacity", ref opacity, 0.10f, 1f, "%.2f"))
        {
            config.WallpaperOpacity = opacity;
            save();
        }
        }
        EndSettingsGroup();
    }

    private void DrawTabletSizeSelector(ThemePalette palette)
    {
        ImGui.TextUnformatted("Tablet size");
        ImGui.Dummy(new Vector2(0, C(5f)));

        foreach (var option in new[] { "Small", "Normal", "Large" })
        {
            var selected = option.Equals(config.TabletSize, StringComparison.OrdinalIgnoreCase);
            var start = ImGui.GetCursorScreenPos();
            var size = C(new Vector2(92, 30));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(
                start,
                start + size,
                ImGui.GetColorU32(selected
                    ? palette.Accent
                    : new Vector4(0.30f, 0.31f, 0.35f, 1f)),
                size.Y * 0.5f);
            if (selected)
                draw.AddRect(
                    start,
                    start + size,
                    ImGui.GetColorU32(palette.AccentHover),
                    size.Y * 0.5f,
                    ImDrawFlags.None,
                    C(2f));

            var textSize = ImGui.CalcTextSize(option);
            draw.AddText(
                start + (size - textSize) * 0.5f,
                ImGui.GetColorU32(Vector4.One),
                option);
            ImGui.InvisibleButton($"##tablet-size-{option}", size);
            if (ImGui.IsItemClicked())
            {
                config.TabletSize = option;
                save();
            }
            DrawTooltip($"{option} tablet size.");
            if (!option.Equals("Large", StringComparison.Ordinal))
                ImGui.SameLine();
        }
    }

    private void DrawWhatsNew(ThemePalette palette)
    {
        DrawSettingsGroupLabel("Updates");
        if (BeginSettingsGroup(
                "##whats-new-refresh-group",
                62f,
                palette))
        {
        if (ImGui.Button(changelog.IsRefreshing ? "Loading..." : "Reload file", C(new Vector2(110, 30))) && !changelog.IsRefreshing)
            _ = changelog.RefreshAsync(catalog.Apps);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.62f, 0.64f, 0.72f, 1f), changelog.Status);
        }
        EndSettingsGroup();

        if (changelog.Items.Count == 0)
        {
            DrawSettingsGroupLabel("AirTabOS & App Updates");
            if (BeginSettingsGroup(
                    "##whats-new-empty-group",
                    62f,
                    palette))
            {
            ImGui.TextWrapped("No changelog entries are available yet.");
            }
            EndSettingsGroup();
            return;
        }

        DrawSettingsGroupLabel("AirTabOS & App Updates");
        var releases = new List<List<ChangelogItem>>();
        foreach (var item in changelog.Items)
        {
            var currentRelease = releases.LastOrDefault();
            if (currentRelease is not null &&
                currentRelease[0].Date.Date == item.Date.Date &&
                currentRelease[0].Version.Equals(item.Version, StringComparison.OrdinalIgnoreCase))
            {
                currentRelease.Add(item);
            }
            else
            {
                releases.Add([item]);
            }
        }

        for (var releaseIndex = 0; releaseIndex < releases.Count; releaseIndex++)
        {
            var items = releases[releaseIndex];
            var release = items[0];
            var cardHeight = CalculateChangelogReleaseHeight(items);
            if (BeginSettingsGroup(
                    $"##change-{releaseIndex}-{release.Date:yyyyMMdd}-{release.Version}",
                    cardHeight,
                    palette))
            {
                ImGui.TextColored(palette.AccentHover, $"Version {release.Version}");
                ImGui.SameLine();
                ImGui.TextColored(
                    new Vector4(0.60f, 0.62f, 0.70f, 1f),
                    release.Date.ToLocalTime().ToString("dd MMM yyyy"));
                ImGui.Separator();
                foreach (var item in items)
                {
                    ImGui.TextColored(palette.AccentHover, item.PluginName);
                    foreach (var change in item.Changes)
                    {
                        ImGui.TextColored(palette.Accent, "•");
                        ImGui.SameLine();
                        ImGui.PushTextWrapPos();
                        ImGui.TextUnformatted(change);
                        ImGui.PopTextWrapPos();
                    }
                }
            }
            EndSettingsGroup();
        }
    }

    private static float CalculateChangelogReleaseHeight(
        IReadOnlyList<ChangelogItem> items)
    {
        var style = ImGui.GetStyle();
        var contentWidth = MathF.Max(
            C(160f),
            ImGui.GetContentRegionAvail().X -
            style.WindowPadding.X * 2f -
            style.ChildBorderSize * 2f);
        var wrappedTextWidth = MathF.Max(
            C(120f),
            contentWidth -
            ImGui.CalcTextSize("•").X -
            style.ItemSpacing.X);
        var height =
            style.WindowPadding.Y * 2f +
            style.ChildBorderSize * 2f +
            ImGui.GetTextLineHeight() +
            style.ItemSpacing.Y +
            C(1f) +
            style.ItemSpacing.Y;

        foreach (var item in items)
        {
            height += ImGui.GetTextLineHeight() + style.ItemSpacing.Y;
            foreach (var change in item.Changes)
            {
                height +=
                    ImGui.CalcTextSize(change, false, wrappedTextWidth).Y +
                    style.ItemSpacing.Y;
            }
        }
        return height + C(4f);
    }

    private void DrawStatusSettings(ThemePalette palette)
    {
        DrawSettingsGroupLabel("Status bar");
        if (BeginSettingsGroup(
                "##status-options-group",
                140f,
                palette))
        {
        var use24Hour = config.Use24HourClock;
        if (DrawSettingsToggleRow(
                "24-hour-clock",
                "Use a 24-hour local clock",
                ref use24Hour))
        {
            config.Use24HourClock = use24Hour;
            save();
        }
        var showBattery = config.ShowBattery;
        if (DrawSettingsToggleRow(
                "show-battery",
                "Show battery percentage and fill",
                ref showBattery,
                false))
        {
            config.ShowBattery = showBattery;
            save();
        }
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("Preview");
        if (BeginSettingsGroup(
                "##status-preview-group",
                54f,
                palette))
        {
        ImGui.TextUnformatted(ClockText());
        ImGui.SameLine();
        DrawSignal();
        if (config.ShowBattery)
        {
            ImGui.SameLine();
            DrawBattery();
        }
        }
        EndSettingsGroup();
    }

    private void DrawAboutSettings(ThemePalette palette)
    {
        DrawSettingsGroupLabel("AirTablet");
        if (BeginSettingsGroup(
                "##about-airtablet-group",
                104f,
                palette))
        {
            var icon = textures.GetResourceIcon(
                "settings-about-large",
                @"Resources\Settings\About.png");
            var start = ImGui.GetCursorScreenPos();
            var iconSize = C(new Vector2(68f, 68f));
            if (icon is not null)
                ImGui.Image(icon.Handle, iconSize);
            else
                ImGui.Dummy(iconSize);

            ImGui.SetCursorScreenPos(
                start + new Vector2(iconSize.X + C(14f), 0f));
            ImGui.BeginGroup();
            ImGui.TextColored(palette.AccentHover, "AirTabOS");
            ImGui.TextUnformatted($"Version {ReleaseVersion}");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(
                "A modern tablet environment for Airi Tsukino's bundled Dalamud apps.");
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("System status");
        if (BeginSettingsGroup(
                "##about-status-group",
                52f,
                palette))
        {
        ImGui.TextColored(
            new Vector4(0.62f, 0.64f, 0.72f, 1f),
            appHost.Status);
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("Supporters");
        var supporterAvailableWidth = MathF.Max(
            C(100f),
            ImGui.GetContentRegionAvail().X -
            ImGui.GetStyle().WindowPadding.X * 2f -
            ImGui.GetStyle().ChildBorderSize * 2f);
        var supporterPillWidth = supporters.Length == 0
            ? C(132f)
            : MathF.Max(
                C(132f),
                supporters.Max(name => ImGui.CalcTextSize(name).X) +
                C(42f));
        var supporterMinimumGap = C(12f);
        var supporterColumns = Math.Clamp(
            (int)MathF.Floor(
                (supporterAvailableWidth + supporterMinimumGap) /
                (supporterPillWidth + supporterMinimumGap)),
            1,
            5);
        var supporterRows = supporters.Length == 0
            ? 0
            : (supporters.Length + supporterColumns - 1) / supporterColumns;
        var supporterRowHeight =
            28f +
            ImGui.GetStyle().CellPadding.Y * 2f / AppContentScale;
        var supporterGroupHeight = supporters.Length == 0
            ? 72f
            : 88f + supporterRows * supporterRowHeight;
        if (BeginSettingsGroup(
                "##about-supporters-group",
                supporterGroupHeight,
                palette))
        {
            ImGui.TextColored(
                palette.AccentHover,
                "Thank you for supporting Airi on Ko-Fi");
            ImGui.Dummy(new Vector2(0, C(4f)));

            if (supporters.Length == 0)
            {
                ImGui.TextColored(
                    new Vector4(0.62f, 0.64f, 0.72f, 1f),
                    "Supporter names will be listed here.");
            }
            else if (ImGui.BeginTable(
                         "##supporter-list",
                         supporterColumns,
                         ImGuiTableFlags.SizingStretchSame |
                         ImGuiTableFlags.NoSavedSettings))
            {
                for (var index = 0; index < supporters.Length; index++)
                {
                    ImGui.TableNextColumn();
                    DrawSupporterPill(
                        supporters[index],
                        index,
                        supporterPillWidth,
                        palette);
                }
                ImGui.EndTable();
                // This sentinel is part of the group's measured height, giving
                // the outer Settings scrollbar guaranteed space beneath the
                // final supporter row when scrolled fully to the bottom.
                ImGui.Dummy(new Vector2(0, C(16f)));
            }
        }
        EndSettingsGroup();
    }

    private static void DrawSupporterPill(
        string name,
        int index,
        float pillWidth,
        ThemePalette palette)
    {
        ImGui.PushID(index);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(
            MathF.Min(pillWidth, availableWidth),
            C(28f));
        var horizontalOffset =
            MathF.Max(0f, (availableWidth - size.X) * 0.5f);
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + horizontalOffset);
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(new Vector4(
                palette.Surface.X,
                palette.Surface.Y,
                palette.Surface.Z,
                0.78f)),
            C(7f));
        draw.AddCircleFilled(
            min + C(new Vector2(14f, 14f)),
            C(5f),
            ImGui.GetColorU32(palette.AccentHover),
            20);
        var textSize = ImGui.CalcTextSize(name);
        draw.AddText(
            new Vector2(
                min.X + C(27f),
                min.Y + (size.Y - textSize.Y) * 0.5f),
            ImGui.GetColorU32(
                new Vector4(0.94f, 0.95f, 0.99f, 1f)),
            name);
        ImGui.Dummy(size);
        ImGui.PopID();
    }

    private static string[] LoadSupporters()
    {
        try
        {
            var pluginDirectory =
                DalamudServices.PluginInterface.AssemblyLocation.Directory?.FullName;
            if (string.IsNullOrWhiteSpace(pluginDirectory))
                return [];

            var path = Path.Combine(
                pluginDirectory,
                "Resources",
                "Supporters.txt");
            if (!File.Exists(path))
                return [];

            return File.ReadLines(path)
                .Select(name => name.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(
                ex,
                "AirTablet could not load Resources/Supporters.txt.");
            return [];
        }
    }

    private void DrawMigrationSettings(ThemePalette palette)
    {
        DrawSettingsGroupLabel("Import");
        if (BeginSettingsGroup(
                "##migration-intro-group",
                106f,
                palette))
        {
        ImGui.TextColored(palette.AccentHover, "Import existing plugin settings");
        ImGui.TextWrapped(
            "This copies configuration and data from the original AutoGreet, BarManager, " +
            "GambaAssistant, RaffleManager, ShiftKeeper, and ShopHelper locations into AirTablet.");
        ImGui.TextWrapped(
            "Original files are never changed; AirTablet backs up its current app copies first.");
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("Source folder");
        if (BeginSettingsGroup(
                "##migration-source-group",
                152f,
                palette))
        {
        ImGui.TextUnformatted("Plugin config source");
        var sourceDirectory = config.PluginConfigSourceDirectory;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText(
                "##plugin-config-source",
                ref sourceDirectory,
                1024))
        {
            config.PluginConfigSourceDirectory = sourceDirectory;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            save();

        var resolvedSource = appHost.ConfigSourceDirectory;
        ImGui.TextColored(
            Directory.Exists(resolvedSource)
                ? new Vector4(0.34f, 0.82f, 0.49f, 1f)
                : new Vector4(0.96f, 0.50f, 0.38f, 1f),
            Directory.Exists(resolvedSource)
                ? $"Found: {resolvedSource}"
                : $"Folder not found: {resolvedSource}");

        if (ImGui.Button("Use default location", C(new Vector2(158f, 32f))))
        {
            config.PluginConfigSourceDirectory =
                Configuration.DefaultPluginConfigSourceDirectory;
            save();
        }
        }
        EndSettingsGroup();

        DrawSettingsGroupLabel("Migration");
        var migrationTextWidth = MathF.Max(
            C(160f),
            ImGui.GetContentRegionAvail().X -
            ImGui.GetStyle().WindowPadding.X * 2f -
            ImGui.GetStyle().ChildBorderSize * 2f);
        var migrationTextHeight =
            ImGui.CalcTextSize(
                migrationStatus,
                false,
                migrationTextWidth).Y;
        var migrationGroupHeight = MathF.Max(
            102f,
            (
                ImGui.GetStyle().WindowPadding.Y * 2f +
                ImGui.GetStyle().ItemSpacing.Y * 2f +
                C(48f) +
                migrationTextHeight) /
            AppContentScale);
        if (BeginSettingsGroup(
                "##migration-action-group",
                migrationGroupHeight,
                palette))
        {
        if (ImGui.Button("Migrate original configs", C(new Vector2(190, 36))))
        {
            if (config.OriginalConfigMigrationCount > 0)
            {
                migrationConfirmationPending = true;
                TabletAppTheme.OpenCenteredModal(
                    "Migrate original configs again?##airtablet-repeat-migration");
            }
            else
            {
                RunOriginalConfigMigration();
            }
        }
        DrawTooltip("Copy original plugin settings into AirTablet without deleting or modifying the originals.");

        ImGui.Dummy(new Vector2(0, C(8f)));
        ImGui.TextWrapped(migrationStatus);
        }
        EndSettingsGroup();
        DrawMigrationConfirmation();
    }

    private void RunOriginalConfigMigration()
    {
        var result = appHost.MigrateOriginalConfigs();
        config.OriginalConfigMigrationCount =
            Math.Max(0, config.OriginalConfigMigrationCount) + 1;
        saveImmediate();
        migrationStatus =
            $"{result.Summary} {GetMigrationHistoryStatus()}";
        ShowNotice(result.Imported.Count > 0
            ? $"Imported settings for {result.Imported.Count} app(s)."
            : "No original plugin settings were imported.");
    }

    private void DrawMigrationConfirmation()
    {
        if (!migrationConfirmationPending)
            return;

        const string modalName =
            "Migrate original configs again?##airtablet-repeat-migration";
        TabletAppTheme.OpenCenteredModal(modalName);
        if (!TabletAppTheme.BeginCenteredModal(
                modalName,
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + C(430f));
        ImGui.TextUnformatted(
            "Migrating again may overwrite app settings you changed in AirTablet since the last migration. The imported values come from the original standalone plugin configurations.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Migrate again", C(new Vector2(130f, 0f))))
        {
            migrationConfirmationPending = false;
            RunOriginalConfigMigration();
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", C(new Vector2(100f, 0f))))
        {
            migrationConfirmationPending = false;
            TabletAppTheme.CloseCenteredModal();
        }

        TabletAppTheme.EndCenteredModal();
    }

    private string GetMigrationHistoryStatus()
    {
        var count = Math.Max(0, config.OriginalConfigMigrationCount);
        return count switch
        {
            0 => "No migration has been run.",
            1 => "Migration has been run 1 time on this AirTablet.",
            _ => $"Migrations have been run {count:N0} times on this AirTablet.",
        };
    }

    private void DrawSettingsBackHeader(ThemePalette palette)
    {
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(start + C(new Vector2(8, 5)), ImGui.GetColorU32(palette.AccentHover), "<");
        ImGui.InvisibleButton("##settings-back", C(new Vector2(38, 30)));
        if (ImGui.IsItemClicked())
            settingsPage = SettingsPage.General;
        DrawTooltip("Go back.");
    }

    private string GetStatusBarTitle()
    {
        return screen switch
        {
            Screen.Welcome => "Welcome",
            Screen.Settings => settingsPage switch
            {
                SettingsPage.General => "General",
                SettingsPage.Appearance => "Appearance",
                SettingsPage.Apps => "Apps",
                SettingsPage.WhatsNew => "What's New",
                SettingsPage.StatusBar => "Status Bar",
                SettingsPage.Migration => "Migrate Configs",
                SettingsPage.About => "About",
                _ => "Settings",
            },
            Screen.Module => catalog.Apps
                .FirstOrDefault(app => app.Id.Equals(
                    activeModuleId,
                    StringComparison.OrdinalIgnoreCase))
                ?.Name ?? activeModuleId,
            Screen.Wiki => "Wiki",
            Screen.Feedback => "Feedback",
            _ => string.Empty,
        };
    }

    private void DrawGestureBar(ThemePalette palette)
    {
        var parentMin = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var layerHeight = S(HomeGestureAreaHeight);
        ImGui.SetCursorScreenPos(new Vector2(parentMin.X, parentMin.Y + parentSize.Y - layerHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (!ImGui.BeginChild(
                "##home-gesture-visual-layer",
                new Vector2(parentSize.X, layerHeight),
                false,
                ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
            return;
        }

        var screenSize = ImGui.GetWindowSize();
        var barWidth = S(118f);
        var min = ImGui.GetWindowPos() + new Vector2((screenSize.X - barWidth) * 0.5f, screenSize.Y - S(10f));
        var max = min + new Vector2(barWidth, S(4f));
        var draw = ImGui.GetWindowDrawList();
        var hitMin = min - S(new Vector2(10, 5));
        var hitSize = S(new Vector2(138, 14));
        var hovered = ImGui.IsMouseHoveringRect(hitMin, hitMin + hitSize);
        var hoverAmount = AnimateControlHover("home-gesture", hovered);
        var barColor = Vector4.Lerp(
            new Vector4(0.64f, 0.65f, 0.70f, 0.95f),
            new Vector4(palette.AccentHover.X, palette.AccentHover.Y, palette.AccentHover.Z, 1f),
            hoverAmount);
        var expanded = S(1.5f) * hoverAmount;
        draw.AddRectFilled(
            min - new Vector2(expanded, expanded * 0.5f),
            max + new Vector2(expanded, expanded * 0.5f),
            ImGui.GetColorU32(barColor),
            S(3f));
        ImGui.SetCursorScreenPos(hitMin);
        if (ImGui.InvisibleButton("##home-gesture", hitSize))
        {
            if (controlCenterOpen)
            {
                controlCenterOpen = false;
                controlCenterPickerOpen = false;
                if (tutorialStep == TutorialStep.ControlCenterClose)
                    AdvanceControlTutorial(TutorialStep.LockPosition);
            }
            else if (tutorialStep == TutorialStep.Home)
            {
                ReturnHome();
                AdvanceControlTutorial(TutorialStep.ControlCenterOpen);
            }
            else if (tutorialStep == TutorialStep.None)
            {
                ReturnHome();
            }
        }
        DrawTooltip("Return to the AirTablet home screen.");
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private float AnimateControlHover(string key, bool hovered)
    {
        var target = hovered ? 1f : 0f;
        var current = controlHoverAmounts.GetValueOrDefault(key);
        var response = 1f - MathF.Exp(
            -15f * MathF.Max(0.001f, ImGui.GetIO().DeltaTime));
        current += (target - current) * response;
        if (MathF.Abs(current - target) < 0.001f)
            current = target;
        controlHoverAmounts[key] = current;
        return current;
    }

    private void AdvanceControlTutorial(TutorialStep nextStep)
    {
        tutorialStep = nextStep;
        saveImmediate();
    }

    private void CompleteControlTutorial()
    {
        tutorialStep = TutorialStep.None;
        config.TutorialCompleted = true;
        config.Minimized = false;
        saveImmediate();
        ShowNotice("AirTablet tutorial complete.");
    }

    private async Task RefreshCatalogAndChanges()
    {
        await catalog.RefreshAsync();
        await changelog.RefreshAsync(catalog.Apps);
    }

    private void ReturnHome()
    {
        if (screen == Screen.Home)
            return;
        transitionPhase = TransitionPhase.Closing;
        transitionStartedAt = ImGui.GetTime();
    }

    private void BeginOpening(Screen target)
    {
        controlCenterOpen = false;
        controlCenterPickerOpen = false;
        controlCenterProgress = 0f;
        screen = target;
        transitionPhase = TransitionPhase.Opening;
        transitionStartedAt = ImGui.GetTime();
    }

    private void UpdateScreenTransition()
    {
        if (transitionPhase == TransitionPhase.None)
            return;

        if (ImGui.GetTime() - transitionStartedAt < ScreenTransitionSeconds)
            return;

        if (transitionPhase == TransitionPhase.Closing)
        {
            screen = Screen.Home;
            settingsPage = SettingsPage.General;
            activeModuleId = string.Empty;
        }
        transitionPhase = TransitionPhase.None;
    }

    private (float Scale, float Opacity) GetTransitionVisual()
    {
        if (transitionPhase == TransitionPhase.None)
            return (1f, 1f);

        var progress = (float)Math.Clamp(
            (ImGui.GetTime() - transitionStartedAt) / ScreenTransitionSeconds,
            0d,
            1d);
        var eased = 1f - MathF.Pow(1f - progress, 3f);
        return transitionPhase == TransitionPhase.Opening
            ? (0.84f + 0.16f * eased, 0.22f + 0.78f * eased)
            : (1f - 0.16f * eased, 1f - 0.78f * eased);
    }

    private List<AppDescriptor> OrderedApps()
    {
        var remaining = catalog.Apps.ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AppDescriptor>(remaining.Count);
        foreach (var id in config.AppOrder)
        {
            if (remaining.Remove(id, out var app))
                ordered.Add(app);
        }
        ordered.AddRange(catalog.Apps.Where(app => remaining.ContainsKey(app.Id)));
        return ordered;
    }

    private List<AppDescriptor> AvailableBundledApps()
    {
        var apps = OrderedApps()
            .Where(app => appHost.IsAvailable(app.Id))
            .ToList();
        var knownIds = apps
            .Select(app => app.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in appHost.AvailableAppIds)
        {
            if (!knownIds.Add(id))
                continue;
            apps.Add(new AppDescriptor
            {
                Id = id,
                Name = id,
                Tagline = "Bundled AirTablet app.",
            });
        }
        return apps;
    }

    private void ReorderApp(string sourceId, string targetId)
    {
        var order = OrderedApps().Select(app => app.Id).ToList();
        var sourceIndex = order.FindIndex(id => id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
        var targetIndex = order.FindIndex(id => id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            return;

        (order[sourceIndex], order[targetIndex]) =
            (order[targetIndex], order[sourceIndex]);
        config.AppOrder = order;
        save();
    }

    private void DrawWallpaper(Vector2 min, Vector2 max, ThemePalette palette)
    {
        var wallpaper = textures.GetWallpaper(config.WallpaperPath);
        if (wallpaper is null)
            return;
        var draw = ImGui.GetWindowDrawList();
        var rounding = S(config.Minimized ? 10f : 17f);
        draw.PushClipRect(min, max, true);
        draw.AddImageRounded(
            wallpaper.Handle,
            min,
            max,
            Vector2.Zero,
            Vector2.One,
            ImGui.GetColorU32(new Vector4(1, 1, 1, config.WallpaperOpacity)),
            rounding);
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(new Vector4(palette.Background.X, palette.Background.Y, palette.Background.Z, 0.42f)),
            rounding);
        draw.PopClipRect();
    }

    private void DrawScreenSurface(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 max,
        ThemePalette palette,
        float rounding)
    {
        draw.AddRectFilled(
            min - new Vector2(S(4f)),
            max + new Vector2(S(4f)),
            ImGui.GetColorU32(new Vector4(0.005f, 0.006f, 0.008f, 1f)),
            rounding + S(4f));
        draw.AddRectFilled(min, max, ImGui.GetColorU32(palette.Background), rounding);
    }

    private void DrawBlackChassis(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 max,
        float rounding)
    {
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(new Vector4(0.010f, 0.011f, 0.014f, 1f)),
            rounding);

        // The body stays black; only its narrow grey rim carries a metallic grain.
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(new Vector4(0.12f, 0.13f, 0.15f, 1f)),
            rounding,
            ImDrawFlags.None,
            S(4f));
        draw.AddRect(
            min + new Vector2(S(1.5f)),
            max - new Vector2(S(1.5f)),
            ImGui.GetColorU32(new Vector4(0.48f, 0.50f, 0.56f, 0.92f)),
            rounding - S(1.5f),
            ImDrawFlags.None,
            S(1f));
        draw.AddRect(
            min + new Vector2(S(3f)),
            max - new Vector2(S(3f)),
            ImGui.GetColorU32(new Vector4(0.20f, 0.21f, 0.24f, 0.82f)),
            rounding - S(3f),
            ImDrawFlags.None,
            S(1f));

        for (var x = min.X + rounding; x < max.X - rounding; x += S(7f))
        {
            var bright = ((int)((x - min.X) / S(7f)) & 1) == 0;
            var color = ImGui.GetColorU32(bright
                ? new Vector4(0.78f, 0.80f, 0.84f, 0.18f)
                : new Vector4(0, 0, 0, 0.22f));
            draw.AddLine(
                new Vector2(x, min.Y + S(1f)),
                new Vector2(MathF.Min(x + S(4f), max.X - rounding), min.Y + S(1f)),
                color,
                S(1f));
            draw.AddLine(
                new Vector2(x, max.Y - S(1f)),
                new Vector2(MathF.Min(x + S(4f), max.X - rounding), max.Y - S(1f)),
                color,
                S(1f));
        }
    }

    private void DrawSignal()
    {
        var draw = ImGui.GetWindowDrawList();
        var height = ImGui.GetTextLineHeight();
        var start = ImGui.GetCursorScreenPos() + new Vector2(0, height);
        DrawSignalGlyph(draw, start, height);
        ImGui.Dummy(new Vector2(SignalGlyphWidth(height), height));
    }

    private void DrawBattery()
    {
        ImGui.TextUnformatted("100%");
        ImGui.SameLine();
        var draw = ImGui.GetWindowDrawList();
        var height = ImGui.GetTextLineHeight();
        var start = ImGui.GetCursorScreenPos();
        DrawBatteryGlyph(draw, start, height);
        ImGui.Dummy(new Vector2(BatteryGlyphWidth(height), height));
    }

    private static float SignalGlyphWidth(float height) => height * 1.35f;

    private static float BatteryGlyphWidth(float height) => height * 2.15f;

    private static void DrawSignalGlyph(
        ImDrawListPtr draw,
        Vector2 bottomLeft,
        float glyphHeight)
    {
        var color = ImGui.GetColorU32(new Vector4(0.90f, 0.92f, 0.98f, 1f));
        var width = SignalGlyphWidth(glyphHeight);
        var gap = glyphHeight * 0.10f;
        var barWidth = (width - gap * 3f) / 4f;
        for (var index = 0; index < 4; index++)
        {
            var height = glyphHeight * (0.28f + index * 0.22f);
            var x = index * (barWidth + gap);
            draw.AddRectFilled(
                bottomLeft + new Vector2(x, -height),
                bottomLeft + new Vector2(x + barWidth, 0),
                color,
                MathF.Max(1f, glyphHeight * 0.07f));
        }
    }

    private static void DrawBatteryGlyph(
        ImDrawListPtr draw,
        Vector2 start,
        float glyphHeight)
    {
        var outline = ImGui.GetColorU32(new Vector4(0.90f, 0.92f, 0.98f, 1f));
        var bodyWidth = glyphHeight * 1.88f;
        var capWidth = glyphHeight * 0.18f;
        var capHeight = glyphHeight * 0.38f;
        var inset = glyphHeight * 0.18f;
        var bodyMax = start + new Vector2(bodyWidth, glyphHeight);
        draw.AddRect(
            start,
            bodyMax,
            outline,
            glyphHeight * 0.22f,
            ImDrawFlags.None,
            MathF.Max(1f, glyphHeight * 0.08f));
        draw.AddRectFilled(
            start + new Vector2(inset),
            bodyMax - new Vector2(inset),
            ImGui.GetColorU32(new Vector4(0.42f, 0.88f, 0.58f, 1f)),
            glyphHeight * 0.10f);
        draw.AddRectFilled(
            new Vector2(bodyMax.X, start.Y + (glyphHeight - capHeight) * 0.5f),
            new Vector2(bodyMax.X + capWidth, start.Y + (glyphHeight + capHeight) * 0.5f),
            outline,
            glyphHeight * 0.06f);
    }

    private void DrawExpandGlyph(ImDrawListPtr draw, Vector2 center, float scale = 1f)
    {
        var color = ImGui.GetColorU32(Vector4.One);
        Vector2 Offset(float x, float y) => center + S(new Vector2(x, y)) * scale;
        var thickness = S(2f) * scale;
        draw.AddLine(Offset(-10, -3), Offset(-10, -10), color, thickness);
        draw.AddLine(Offset(-10, -10), Offset(-3, -10), color, thickness);
        draw.AddLine(Offset(10, -3), Offset(10, -10), color, thickness);
        draw.AddLine(Offset(10, -10), Offset(3, -10), color, thickness);
        draw.AddLine(Offset(-10, 3), Offset(-10, 10), color, thickness);
        draw.AddLine(Offset(-10, 10), Offset(-3, 10), color, thickness);
        draw.AddLine(Offset(10, 3), Offset(10, 10), color, thickness);
        draw.AddLine(Offset(10, 10), Offset(3, 10), color, thickness);
    }

    private void DrawGearGlyph(ImDrawListPtr draw, Vector2 center, Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        draw.AddCircle(center, S(14f), packed, 24, S(3f));
        draw.AddCircle(center, S(5f), packed, 18, S(3f));
        for (var index = 0; index < 8; index++)
        {
            var angle = MathF.PI * index / 4f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(center + direction * S(14f), center + direction * S(19f), packed, S(4f));
        }
    }

    private void DrawDiscordGlyph(ImDrawListPtr draw, Vector2 center, Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        draw.AddBezierCubic(
            center + S(new Vector2(-18, 8)),
            center + S(new Vector2(-18, -13)),
            center + S(new Vector2(18, -13)),
            center + S(new Vector2(18, 8)),
            packed,
            S(3f));
        draw.AddLine(center + S(new Vector2(-18, 8)), center + S(new Vector2(-10, 15)), packed, S(3f));
        draw.AddLine(center + S(new Vector2(18, 8)), center + S(new Vector2(10, 15)), packed, S(3f));
        draw.AddCircleFilled(center + S(new Vector2(-7, 1)), S(3f), packed, 16);
        draw.AddCircleFilled(center + S(new Vector2(7, 1)), S(3f), packed, 16);
    }

    private void DrawKofiGlyph(ImDrawListPtr draw, Vector2 center, Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        var cupMin = center + S(new Vector2(-15, -9));
        var cupMax = center + S(new Vector2(10, 10));
        draw.AddRect(cupMin, cupMax, packed, S(5f), ImDrawFlags.None, S(3f));
        draw.AddCircle(
            center + S(new Vector2(12, 0)),
            S(8f),
            packed,
            20,
            S(3f));
        var heart = ImGui.GetColorU32(new Vector4(1f, 0.82f, 0.88f, 1f));
        draw.AddCircleFilled(center + S(new Vector2(-6, -1)), S(3.5f), heart, 18);
        draw.AddCircleFilled(center + S(new Vector2(0, -1)), S(3.5f), heart, 18);
        draw.AddTriangleFilled(
            center + S(new Vector2(-9, 0)),
            center + S(new Vector2(3, 0)),
            center + S(new Vector2(-3, 8)),
            heart);
    }

    private void DrawWikiGlyph(ImDrawListPtr draw, Vector2 center, Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        var leftTop = center + S(new Vector2(-19f, -15f));
        var middleTop = center + S(new Vector2(0f, -11f));
        var rightTop = center + S(new Vector2(19f, -15f));
        var bottom = center + S(new Vector2(0f, 17f));
        draw.AddQuad(
            leftTop,
            middleTop,
            bottom,
            center + S(new Vector2(-19f, 12f)),
            packed,
            S(2.6f));
        draw.AddQuad(
            middleTop,
            rightTop,
            center + S(new Vector2(19f, 12f)),
            bottom,
            packed,
            S(2.6f));
        draw.AddLine(middleTop, bottom, packed, S(2f));
        draw.AddLine(
            center + S(new Vector2(-14f, -6f)),
            center + S(new Vector2(-5f, -4f)),
            packed,
            S(1.5f));
        draw.AddLine(
            center + S(new Vector2(5f, -4f)),
            center + S(new Vector2(14f, -6f)),
            packed,
            S(1.5f));
    }

    private static void DrawNotificationBadge(
        ImDrawListPtr draw,
        Vector2 center,
        float radius,
        ThemePalette palette,
        string text)
    {
        draw.AddCircleFilled(
            center,
            radius,
            ImGui.GetColorU32(new Vector4(0.96f, 0.20f, 0.25f, 1f)),
            24);
        draw.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(new Vector4(
                palette.Surface.X,
                palette.Surface.Y,
                palette.Surface.Z,
                1f)),
            24,
            MathF.Max(1f, radius * 0.16f));
        var textSize = ImGui.CalcTextSize(text);
        draw.AddText(
            center - textSize * 0.5f,
            ImGui.GetColorU32(Vector4.One),
            text);
    }

    private void MarkChangelogRead()
    {
        if (!HasUnreadChangelog)
            return;
        config.LastReadChangelogVersion = ReleaseVersion;
        saveImmediate();
    }

    private void DrawFeedbackGlyph(ImDrawListPtr draw, Vector2 center, Vector4 color)
    {
        var packed = ImGui.GetColorU32(color);
        var min = center + S(new Vector2(-18, -13));
        var max = center + S(new Vector2(18, 10));
        draw.AddRect(min, max, packed, S(7f), ImDrawFlags.None, S(3f));
        draw.AddTriangleFilled(
            center + S(new Vector2(-9, 9)),
            center + S(new Vector2(-2, 9)),
            center + S(new Vector2(-10, 17)),
            packed);
        for (var index = -1; index <= 1; index++)
            draw.AddCircleFilled(center + S(new Vector2(index * 8f, -2)), S(2.2f), packed, 14);
    }

    private void OpenExternalUrl(string url, string destination)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AirTablet could not open {Destination}.", destination);
            ShowNotice($"Could not open {destination}. Check the Dalamud log.");
        }
    }

    private static void DrawCenteredText(ImDrawListPtr draw, string text, float y, float minX, float maxX, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        draw.AddText(new Vector2((minX + maxX - size.X) * 0.5f, y), ImGui.GetColorU32(color), text);
    }

    private static string Initials(string name)
    {
        var capitals = name.Where(char.IsUpper).Take(2).ToArray();
        return capitals.Length > 0 ? new string(capitals) : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }

    private string ClockText() => DateTime.Now.ToString(config.Use24HourClock ? "HH:mm" : "h:mm tt");

    private void ShowNotice(string message)
    {
        notice = message;
        noticeStartedAt = ImGui.GetTime();
        noticeUntil = DateTime.UtcNow.AddSeconds(4);
    }

    private void DrawTooltip(string text)
    {
        if (!config.ShowAirTabOsTooltips || !ImGui.IsItemHovered())
            return;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, C(new Vector2(12, 10)));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, C(8f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.105f, 0.110f, 0.125f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.32f, 0.33f, 0.38f, 1f));
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + C(260f));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void PushShellStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, S(12f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, S(8f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.30f, 0.31f, 0.36f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.91f, 0.92f, 0.96f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.17f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.27f, 0.28f, 0.34f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.36f, 0.29f, 0.56f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.13f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.105f, 0.110f, 0.125f, 1f));
    }

    private static void PopShellStyle()
    {
        ImGui.PopStyleColor(7);
        ImGui.PopStyleVar(4);
    }

    private float S(float value) => value * uiScale;

    private Vector2 S(Vector2 value) => value * uiScale;

    private static float C(float value) => value * AppContentScale;

    private static Vector2 C(Vector2 value) => value * AppContentScale;

}
