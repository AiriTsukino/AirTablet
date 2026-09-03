using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AirTablet.UI;

internal static class TabletAppTheme
{
    private sealed class ModalAnimationState
    {
        public double AnimationStartedAt { get; set; }
        public float CardWidth { get; set; }
        public float CardHeight { get; set; }
        public bool MeasurementComplete { get; set; }
        public bool CloseRequested { get; set; }
        public int RequestedFrame { get; set; }
        public int LastPresentedFrame { get; set; } = -1;
    }

    private const double ModalAnimationSeconds = 0.34d;
    private const int ModalOrphanGraceFrames = 3;
    private const string ModalHostPopupId = "##airtablet-confirmation-host";
    private static ThemePalette? palette;
    private static ThemePalette? lastPalette;
    private static float activeScale = 1f;
    private static int pushedColors;
    private static int pushedVariables;
    private static int pushDepth;
    private static Vector2 tabletScreenMin;
    private static Vector2 tabletScreenMax;
    private static string requestedModalName = string.Empty;
    private static ModalAnimationState? requestedModalState;
    private static string activeModalName = string.Empty;
    private static ModalAnimationState? activeModalState;
    private static int activeModalFrame = -1;
    private static bool activeModalChildOpen;
    private static bool activeModalContentAlphaPushed;
    private static int activeModalColorCount;
    private static int activeModalVariableCount;

    public static bool IsActive => palette is not null;
    public static bool HasRememberedTheme => lastPalette is not null;
    public static bool HasOpenModal
    {
        get
        {
            PruneStaleModal();
            return requestedModalState is { CloseRequested: false };
        }
    }
    public static float Scale => palette is null ? 1f : activeScale;
    public static Vector4 Accent => palette?.Accent ?? new Vector4(0.56f, 0.35f, 0.96f, 1f);
    public static Vector4 AccentHover => palette?.AccentHover ?? new Vector4(0.67f, 0.49f, 1f, 1f);
    public static Vector4 Surface => palette is null
        ? new Vector4(0.10f, 0.08f, 0.16f, 1f)
        : Opaque(palette.Surface);
    public static Vector4 SurfaceRaised => palette is null
        ? new Vector4(0.15f, 0.12f, 0.23f, 1f)
        : Opaque(palette.SurfaceRaised);
    public static Vector4 Text => new(0.93f, 0.94f, 0.98f, 1f);
    public static Vector4 MutedText => new(0.63f, 0.65f, 0.72f, 1f);
    public static Vector4 TooltipBackground => new(0.105f, 0.110f, 0.125f, 1f);
    public static Vector4 RememberedAccent => lastPalette?.Accent ?? Accent;
    public static Vector4 RememberedAccentHover => lastPalette?.AccentHover ?? AccentHover;
    public static Vector4 RememberedSurface => lastPalette is null ? Surface : Opaque(lastPalette.Surface);
    public static Vector4 RememberedSurfaceRaised => lastPalette is null ? SurfaceRaised : Opaque(lastPalette.SurfaceRaised);

    public static void RememberTheme(ThemePalette activePalette)
        => lastPalette = activePalette;

    public static void SetTabletScreenBounds(Vector2 min, Vector2 max)
    {
        tabletScreenMin = min;
        tabletScreenMax = max;
    }

    public static void OpenCenteredModal(string name)
    {
        PruneStaleModal();
        if (requestedModalState is not null)
        {
            if (requestedModalName.Equals(name, StringComparison.Ordinal))
                return;
            return;
        }

        requestedModalName = name;
        requestedModalState = new ModalAnimationState
        {
            RequestedFrame = ImGui.GetFrameCount(),
        };
    }

    public static bool BeginCenteredModal(
        string name,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None,
        float preferredWidth = 560f)
    {
        PruneStaleModal();
        if (requestedModalState is not { CloseRequested: false } state ||
            !requestedModalName.Equals(name, StringComparison.Ordinal))
        {
            return false;
        }

        var viewport = ImGui.GetMainViewport();
        var hasTabletBounds =
            tabletScreenMax.X > tabletScreenMin.X &&
            tabletScreenMax.Y > tabletScreenMin.Y;
        var overlayMin = hasTabletBounds
            ? tabletScreenMin
            : viewport.Pos;
        var overlaySize = hasTabletBounds
            ? tabletScreenMax - tabletScreenMin
            : viewport.Size;
        var overlayMax = overlayMin + overlaySize;

        // The overlay itself never moves or auto-sizes. It is a transparent
        // input shield fixed to the tablet screen; only its card animates.
        ImGui.SetNextWindowPos(overlayMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(overlaySize, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(overlaySize, overlaySize);
        PushModalOverlayStyle();
        // Open and begin the shared host together so its ImGui ID scope can
        // never differ between the requesting button and the overlay.
        ImGui.OpenPopup(ModalHostPopupId);
        var visible = ImGui.BeginPopup(
            ModalHostPopupId,
            (flags & ~ImGuiWindowFlags.AlwaysAutoResize) |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoSavedSettings);
        if (!visible)
        {
            PopModalStyle();
            return false;
        }
        state.LastPresentedFrame = ImGui.GetFrameCount();

        if (state.CardWidth <= 0f)
        {
            var isWinnerAnnouncement = name.Contains(
                "WinnerPopup",
                StringComparison.Ordinal);
            var availableWidth = MathF.Max(
                Px(280f),
                overlaySize.X - Px(56f));
            var availableHeight = MathF.Max(
                Px(160f),
                overlaySize.Y - Px(52f));
            var cardWidth = isWinnerAnnouncement
                ? MathF.Min(Px(940f), availableWidth)
                : MathF.Min(Px(preferredWidth), availableWidth);
            state.CardWidth = cardWidth;
            state.CardHeight = availableHeight;
            state.MeasurementComplete = false;
            state.AnimationStartedAt = 0d;
        }

        // Large editors fill the usable tablet area and respond to resize or
        // UI scale changes while open. Existing compact dialogs keep measuring
        // their content as before.
        if (preferredWidth > 560f)
        {
            state.CardWidth = MathF.Min(Px(preferredWidth), MathF.Max(Px(120f), overlaySize.X - Px(56f)));
            state.CardHeight = MathF.Max(Px(120f), overlaySize.Y - Px(52f));
        }

        var progress = state.MeasurementComplete
            ? (float)Math.Clamp(
                (ImGui.GetTime() - state.AnimationStartedAt) /
                ModalAnimationSeconds,
                0d,
                1d)
            : 0f;
        var eased = 1f - MathF.Pow(1f - progress, 3f);
        var cardSize = new Vector2(state.CardWidth, state.CardHeight);
        var targetMin = overlayMin + (overlaySize - cardSize) * 0.5f;
        var startMin = new Vector2(
            targetMin.X,
            overlayMin.Y + Px(18f));
        var cardMin = Vector2.Lerp(startMin, targetMin, eased);
        var cardMax = cardMin + cardSize;
        if (state.MeasurementComplete)
            DrawTabletModalOverlay(
                overlayMin,
                overlayMax,
                cardMin,
                cardMax,
                eased);

        var padding = Px(new Vector2(22f, 18f));
        var contentSize = Vector2.Max(
            Px(new Vector2(40f, 40f)),
            cardSize - padding * 2f);
        ImGui.SetCursorScreenPos(cardMin + padding);
        ImGui.BeginChild(
            $"##airtablet-modal-content-{name}",
            contentSize,
            false,
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse);
        activeModalChildOpen = true;
        ImGui.PushStyleVar(
            ImGuiStyleVar.Alpha,
            state.MeasurementComplete ? eased : 0f);
        activeModalContentAlphaPushed = true;
        activeModalName = name;
        activeModalState = state;
        activeModalFrame = ImGui.GetFrameCount();
        DrawModalHeader(name);
        return true;
    }

    public static void EndCenteredModal()
    {
        if (string.IsNullOrWhiteSpace(activeModalName) ||
            activeModalState is not { } state)
        {
            return;
        }

        var usedContentHeight = ImGui.GetCursorPosY() + Px(2f);
        if (activeModalContentAlphaPushed)
        {
            ImGui.PopStyleVar();
            activeModalContentAlphaPushed = false;
        }
        if (activeModalChildOpen)
        {
            ImGui.EndChild();
            activeModalChildOpen = false;
        }

        if (!state.MeasurementComplete)
        {
            var overlayHeight = MathF.Max(
                Px(160f),
                tabletScreenMax.Y - tabletScreenMin.Y);
            state.CardHeight = Math.Clamp(
                usedContentHeight + Px(36f),
                Px(150f),
                overlayHeight - Px(52f));
            state.MeasurementComplete = true;
            state.AnimationStartedAt = ImGui.GetTime();
        }

        PopModalStyle();
        ImGui.EndPopup();
        if (state.CloseRequested &&
            ReferenceEquals(requestedModalState, state))
        {
            requestedModalName = string.Empty;
            requestedModalState = null;
        }
        activeModalName = string.Empty;
        activeModalState = null;
        activeModalFrame = -1;
    }

    public static void CloseCenteredModal()
    {
        if (activeModalState is { } state)
        {
            state.CloseRequested = true;
            ImGui.CloseCurrentPopup();
        }
    }

    public static void Begin(ThemePalette activePalette, float activeScale = 1f)
    {
        palette = activePalette;
        RememberTheme(activePalette);
        TabletAppTheme.activeScale = Math.Clamp(activeScale, 0.5f, 2f);
        pushedColors = 0;
        pushedVariables = 0;
        pushDepth = 0;
    }

    public static void End()
    {
        Pop();
        palette = null;
        activeScale = 1f;
        pushDepth = 0;
    }

    public static void Push()
    {
        if (palette is null)
            return;
        pushDepth++;
        if (pushDepth > 1)
            return;

        var accent = palette.Accent;
        var hover = palette.AccentHover;
        var surface = palette.Surface;
        var raised = palette.SurfaceRaised;
        PushColor(ImGuiCol.Text, Text);
        PushColor(ImGuiCol.TextDisabled, MutedText);
        PushColor(ImGuiCol.WindowBg, new Vector4(surface.X, surface.Y, surface.Z, 0.98f));
        PushColor(ImGuiCol.ChildBg, new Vector4(surface.X, surface.Y, surface.Z, 1f));
        PushColor(ImGuiCol.PopupBg, TooltipBackground);
        PushColor(
            ImGuiCol.ModalWindowDimBg,
            Vector4.Zero);
        PushColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, 0.42f));
        PushColor(ImGuiCol.FrameBg, new Vector4(surface.X, surface.Y, surface.Z, 1f));
        PushColor(ImGuiCol.FrameBgHovered, raised);
        PushColor(ImGuiCol.FrameBgActive, new Vector4(accent.X, accent.Y, accent.Z, 0.52f));
        PushColor(ImGuiCol.TitleBg, surface);
        PushColor(ImGuiCol.TitleBgActive, raised);
        PushColor(ImGuiCol.TitleBgCollapsed, surface);
        PushColor(ImGuiCol.MenuBarBg, surface);
        PushColor(ImGuiCol.ScrollbarBg, new Vector4(0.055f, 0.058f, 0.070f, 1f));
        PushColor(ImGuiCol.ScrollbarGrab, new Vector4(accent.X, accent.Y, accent.Z, 0.64f));
        PushColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(hover.X, hover.Y, hover.Z, 0.72f));
        PushColor(ImGuiCol.ScrollbarGrabActive, hover);
        PushColor(ImGuiCol.CheckMark, hover);
        PushColor(ImGuiCol.SliderGrab, accent);
        PushColor(ImGuiCol.SliderGrabActive, hover);
        PushColor(ImGuiCol.Button, raised);
        PushColor(ImGuiCol.ButtonHovered, new Vector4(accent.X, accent.Y, accent.Z, 0.58f));
        PushColor(ImGuiCol.ButtonActive, new Vector4(hover.X, hover.Y, hover.Z, 0.76f));
        PushColor(ImGuiCol.Header, new Vector4(accent.X, accent.Y, accent.Z, 0.38f));
        PushColor(ImGuiCol.HeaderHovered, new Vector4(hover.X, hover.Y, hover.Z, 0.56f));
        PushColor(ImGuiCol.HeaderActive, new Vector4(hover.X, hover.Y, hover.Z, 0.72f));
        PushColor(ImGuiCol.Separator, new Vector4(accent.X, accent.Y, accent.Z, 0.50f));
        PushColor(ImGuiCol.SeparatorHovered, hover);
        PushColor(ImGuiCol.SeparatorActive, accent);
        PushColor(ImGuiCol.Tab, surface);
        PushColor(ImGuiCol.TabHovered, raised);
        PushColor(ImGuiCol.TabActive, new Vector4(accent.X, accent.Y, accent.Z, 0.56f));
        PushColor(ImGuiCol.TabUnfocused, surface);
        PushColor(ImGuiCol.TabUnfocusedActive, new Vector4(accent.X, accent.Y, accent.Z, 0.34f));
        PushColor(ImGuiCol.TableHeaderBg, raised);
        PushColor(ImGuiCol.TableBorderStrong, new Vector4(accent.X, accent.Y, accent.Z, 0.52f));
        PushColor(ImGuiCol.TableBorderLight, new Vector4(accent.X, accent.Y, accent.Z, 0.28f));
        PushColor(ImGuiCol.TableRowBg, new Vector4(surface.X, surface.Y, surface.Z, 1f));
        PushColor(ImGuiCol.TableRowBgAlt, new Vector4(raised.X, raised.Y, raised.Z, 1f));

        PushVariable(ImGuiStyleVar.WindowRounding, Px(10f));
        PushVariable(ImGuiStyleVar.ChildRounding, Px(10f));
        PushVariable(ImGuiStyleVar.FrameRounding, Px(7f));
        PushVariable(ImGuiStyleVar.GrabRounding, Px(7f));
        PushVariable(ImGuiStyleVar.TabRounding, Px(7f));
        PushVariable(ImGuiStyleVar.PopupRounding, Px(8f));
        PushVariable(ImGuiStyleVar.WindowPadding, Px(new Vector2(18, 16)));
        PushVariable(ImGuiStyleVar.ItemSpacing, Px(new Vector2(10, 9)));
        PushVariable(ImGuiStyleVar.ItemInnerSpacing, Px(new Vector2(7, 6)));
        PushVariable(ImGuiStyleVar.FramePadding, Px(new Vector2(10, 6)));
        PushVariable(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));
        PushVariable(ImGuiStyleVar.CellPadding, Px(new Vector2(9, 7)));
        PushVariable(ImGuiStyleVar.IndentSpacing, Px(20f));
        PushVariable(ImGuiStyleVar.ScrollbarSize, Px(13f));
        PushVariable(ImGuiStyleVar.ScrollbarRounding, Px(7f));
        PushVariable(ImGuiStyleVar.ChildBorderSize, Px(1f));
        PushVariable(ImGuiStyleVar.PopupBorderSize, Px(1f));
        PushVariable(ImGuiStyleVar.FrameBorderSize, Px(1f));
    }

    public static void Pop()
    {
        if (pushDepth <= 0)
            return;
        pushDepth--;
        if (pushDepth > 0)
            return;

        if (pushedVariables > 0)
            ImGui.PopStyleVar(pushedVariables);
        if (pushedColors > 0)
            ImGui.PopStyleColor(pushedColors);
        pushedVariables = 0;
        pushedColors = 0;
    }

    public static float Px(float value) => value * Scale;

    public static Vector2 Px(Vector2 value) => value * Scale;

    // A distinct outline is essential for unchecked boxes: the normal input
    // background can be identical to the surrounding card or modal surface.
    public static bool VisibleCheckbox(string label, ref bool value)
    {
        var accent = HasRememberedTheme ? RememberedAccent : Accent;
        var hover = HasRememberedTheme ? RememberedAccentHover : AccentHover;
        var surface = HasRememberedTheme ? RememberedSurfaceRaised : SurfaceRaised;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Lerp(surface, accent, value ? 0.72f : 0.22f) with { W = 1f });
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Lerp(surface, hover, 0.55f) with { W = 1f });
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, accent with { W = 1f });
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Lerp(hover, Vector4.One, 0.22f) with { W = 1f });
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(1f, 1f, 1f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, MathF.Max(1f, Px(1.5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Px(3f));
        try { return ImGui.Checkbox(label, ref value); }
        finally
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
        }
    }

    private static void PruneStaleModal()
    {
        var state = requestedModalState;
        if (state is null)
            return;

        var frame = ImGui.GetFrameCount();
        if (ReferenceEquals(activeModalState, state) &&
            activeModalFrame == frame)
        {
            return;
        }
        if (activeModalState is not null &&
            activeModalFrame != frame)
        {
            // A caller failed before EndCenteredModal. ImGui recovers its own
            // per-frame stacks; discard our matching bookkeeping so one bad
            // draw cannot permanently disable every confirmation.
            activeModalName = string.Empty;
            activeModalState = null;
            activeModalFrame = -1;
            activeModalChildOpen = false;
            activeModalContentAlphaPushed = false;
            activeModalColorCount = 0;
            activeModalVariableCount = 0;
        }

        var lastRelevantFrame = state.LastPresentedFrame >= 0
            ? state.LastPresentedFrame
            : state.RequestedFrame;
        if (!state.CloseRequested &&
            frame - lastRelevantFrame <= ModalOrphanGraceFrames)
        {
            return;
        }

        requestedModalName = string.Empty;
        requestedModalState = null;
    }

    private static void DrawTabletModalOverlay(
        Vector2 overlayMin,
        Vector2 overlayMax,
        Vector2 cardMin,
        Vector2 cardMax,
        float opacity)
    {
        if (overlayMax.X <= overlayMin.X ||
            overlayMax.Y <= overlayMin.Y ||
            opacity <= 0f)
        {
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(overlayMin, overlayMax, false);
        draw.AddRectFilled(
            overlayMin,
            overlayMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.012f,
                    0.013f,
                    0.020f,
                    0.78f * opacity)),
            Px(17f));

        var raised = SurfaceRaised;
        var accent = Accent;
        draw.AddRectFilled(
            cardMin + Px(new Vector2(4f, 5f)),
            cardMax + Px(new Vector2(4f, 5f)),
            ImGui.GetColorU32(
                new Vector4(0f, 0f, 0f, 0.42f * opacity)),
            Px(16f));
        draw.AddRectFilled(
            cardMin,
            cardMax,
            ImGui.GetColorU32(
                new Vector4(
                    raised.X,
                    raised.Y,
                    raised.Z,
                    0.99f * opacity)),
            Px(16f));
        draw.AddRect(
            cardMin,
            cardMax,
            ImGui.GetColorU32(
                new Vector4(
                    accent.X,
                    accent.Y,
                    accent.Z,
                    0.82f * opacity)),
            Px(16f),
            ImDrawFlags.None,
            Px(1.5f));
        draw.PopClipRect();
    }

    private static void PushModalOverlayStyle()
    {
        activeModalVariableCount = 0;
        activeModalColorCount = 0;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(17f));
        activeModalVariableCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
        activeModalVariableCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        activeModalVariableCount++;

        ImGui.PushStyleColor(ImGuiCol.PopupBg, Vector4.Zero);
        activeModalColorCount++;
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        activeModalColorCount++;
    }

    private static void DrawModalHeader(string name)
    {
        var titleEnd = name.IndexOf("###", StringComparison.Ordinal);
        if (titleEnd < 0)
            titleEnd = name.IndexOf("##", StringComparison.Ordinal);
        var title = (titleEnd >= 0 ? name[..titleEnd] : name).Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = "Confirmation";

        ImGui.TextColored(AccentHover, "AirTablet");
        ImGui.SameLine();
        ImGui.TextUnformatted(title);
        ImGui.Separator();
        ImGui.Dummy(Px(new Vector2(0f, 3f)));
    }

    private static void PopModalStyle()
    {
        if (activeModalColorCount > 0)
            ImGui.PopStyleColor(activeModalColorCount);
        if (activeModalVariableCount > 0)
            ImGui.PopStyleVar(activeModalVariableCount);
        activeModalColorCount = 0;
        activeModalVariableCount = 0;
    }

    private static void PushColor(ImGuiCol target, Vector4 color)
    {
        ImGui.PushStyleColor(target, color);
        pushedColors++;
    }

    private static void PushVariable(ImGuiStyleVar target, float value)
    {
        ImGui.PushStyleVar(target, value);
        pushedVariables++;
    }

    private static void PushVariable(ImGuiStyleVar target, Vector2 value)
    {
        ImGui.PushStyleVar(target, value);
        pushedVariables++;
    }

    private static Vector4 Opaque(Vector4 color) =>
        new(color.X, color.Y, color.Z, 1f);
}
