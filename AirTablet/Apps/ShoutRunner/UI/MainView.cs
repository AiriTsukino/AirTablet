using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;

namespace ShoutRunner.UI;

internal sealed class MainView
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly RunService runner;
    private readonly TravelService travel;
    private readonly CityIconService cityIcons;
    private readonly Action openSettings;
    private string statusMessage = string.Empty;
    private string newProfileName = string.Empty;
    private bool copyProfile = true;
    private Guid? deleteMessageId;

    public MainView(
        Configuration config,
        PersistenceService persistence,
        RunService runner,
        TravelService travel,
        CityIconService cityIcons,
        Action openSettings)
    {
        this.config = config;
        this.persistence = persistence;
        this.runner = runner;
        this.travel = travel;
        this.cityIcons = cityIcons;
        this.openSettings = openSettings;
    }

    private VenueProfile Profile => persistence.ActiveProfile;

    public void DrawMain()
    {
        DrawHeader(showSettingsButton: true);
        ImGui.Separator();
        DrawControls();
        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 8f)));

        if (runner.IsRunning || runner.Phase == RunPhase.Completed)
            DrawTravelScreen();
        else
            DrawDashboard();

        DrawStopConfirmation();
    }

    public void DrawSettings()
    {
        DrawHeader(showSettingsButton: false);
        ImGui.Separator();
        if (ImGui.BeginTabBar("##shoutrunner-settings-tabs"))
        {
            if (ImGui.BeginTabItem("Profiles"))
            {
                DrawProfiles();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Messages"))
            {
                DrawMessages();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Route"))
            {
                DrawRoute();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Travel & retries"))
            {
                DrawTravelSettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Debug Log"))
            {
                DrawDebugLog();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        DrawMessageDeleteConfirmation();
    }

    private void DrawDebugLog()
    {
        ImGui.TextColored(TabletAppTheme.AccentHover, "Travel diagnostics");
        TextMutedWrapped("Use this log when world or data-center travel stops. You can select individual text with the mouse and press Ctrl+C, or copy everything at once.");
        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 5f)));

        var logText = travel.DebugLogText;
        if (ImGui.Button("Copy all", TabletAppTheme.Px(new Vector2(100f, 30f))))
            ImGui.SetClipboardText(logText);
        ImGui.SameLine();
        if (ImGui.Button("Clear", TabletAppTheme.Px(new Vector2(90f, 30f))))
            travel.ClearDebugLog();
        ImGui.SameLine();
        TextMuted($"{(string.IsNullOrEmpty(logText) ? 0 : logText.Count(character => character == '\n') + 1)} line(s)");

        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 5f)));
        logText = travel.DebugLogText;
        var height = MathF.Max(TabletAppTheme.Px(220f), ImGui.GetContentRegionAvail().Y);
        ImGui.InputTextMultiline(
            "##shoutrunner-copyable-debug-log",
            ref logText,
            Math.Max(logText.Length + 1024, 4096),
            new Vector2(-1f, height),
            ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
    }

    private void DrawHeader(bool showSettingsButton)
    {
        ImGui.TextColored(TabletAppTheme.AccentHover, "ShoutRunner");
        ImGui.SameLine(0f, TabletAppTheme.Px(12f));
        TextMuted("Profile");
        ImGui.SameLine(0f, TabletAppTheme.Px(6f));
        var profiles = persistence.Profiles.Keys.OrderBy(name => name).ToArray();
        var selectedProfile = Math.Max(0, Array.FindIndex(profiles, name => name.Equals(config.ActiveVenueProfile, StringComparison.OrdinalIgnoreCase)));
        ImGui.SetNextItemWidth(TabletAppTheme.Px(180f));
        if (runner.IsRunning) ImGui.BeginDisabled();
        if (ImGui.Combo("##sr-header-profile", ref selectedProfile, profiles, profiles.Length) && selectedProfile < profiles.Length)
            persistence.Activate(profiles[selectedProfile]);
        if (runner.IsRunning) ImGui.EndDisabled();
        if (showSettingsButton)
        {
            var width = TabletAppTheme.Px(90f);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - width + ImGui.GetCursorPosX());
            if (ImGui.Button("Settings", new Vector2(width, 0f)))
                openSettings();
        }
    }

    private void DrawControls()
    {
        var canStart = !runner.IsRunning && runner.Phase is not RunPhase.Completed;
        if (!canStart) ImGui.BeginDisabled();
        if (ImGui.Button("Start Run", TabletAppTheme.Px(new Vector2(120f, 32f))))
        {
            if (!runner.Start(Profile, out var error))
                statusMessage = error;
            else
                statusMessage = string.Empty;
        }
        if (!canStart) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!runner.IsRunning) ImGui.BeginDisabled();
        if (runner.IsPaused)
        {
            if (ImGui.Button("Resume", TabletAppTheme.Px(new Vector2(100f, 32f))))
                runner.Resume();
        }
        else if (ImGui.Button("Pause", TabletAppTheme.Px(new Vector2(100f, 32f))))
        {
            runner.Pause();
        }
        if (!runner.IsRunning) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!runner.IsRunning) ImGui.BeginDisabled();
        if (ImGui.Button("Stop", TabletAppTheme.Px(new Vector2(100f, 32f))))
            TabletAppTheme.OpenCenteredModal("Stop ShoutRunner run?");
        if (!runner.IsRunning) ImGui.EndDisabled();

        ImGui.SameLine();
        if (runner.Phase != RunPhase.Completed) ImGui.BeginDisabled();
        if (ImGui.Button("Reset", TabletAppTheme.Px(new Vector2(100f, 32f))))
            runner.ResetCompletedRun();
        if (runner.Phase != RunPhase.Completed) ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            ImGui.SameLine(0f, TabletAppTheme.Px(12f));
            ImGui.TextColored(new Vector4(0.96f, 0.48f, 0.42f, 1f), statusMessage);
        }
    }

    private void DrawDashboard()
    {
        var available = ImGui.GetContentRegionAvail();
        var gap = TabletAppTheme.Px(12f);
        var leftWidth = MathF.Max(TabletAppTheme.Px(360f), (available.X - gap) * 0.58f);
        if (BeginCard("##sr-progress", new Vector2(leftWidth, available.Y)))
        {
            SectionHeader("Run progress");
            var fraction = runner.TotalStops == 0 ? 0f : runner.CompletedStops / (float)runner.TotalStops;
            ImGui.ProgressBar(fraction, new Vector2(-1f, TabletAppTheme.Px(24f)), $"{runner.CompletedStops} / {runner.TotalStops} stops");
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 8f)));
            ImGui.TextWrapped(runner.Status);
            if (runner.CurrentStop is { } stop)
            {
                ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 8f)));
                TextMuted("Current stop");
                ImGui.TextColored(TabletAppTheme.AccentHover, $"{stop.CityName} · {stop.World} · {stop.DataCenter}");
            }
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 12f)));
            SectionHeader("Active profile summary");
            ImGui.TextWrapped($"{Profile.Messages.Count} message block(s) · {Profile.Cities.Count} city selection(s) · {Profile.Worlds.Count} world selection(s)");
            TextMutedWrapped("Each configured message block is sent once at every selected city on every selected world.");
        }
        EndCard();

        ImGui.SameLine(0f, gap);
        if (BeginCard("##sr-readiness", new Vector2(0f, available.Y)))
        {
            SectionHeader("Travel readiness");
            StatusLine("Home region", WorldCatalog.DetectHomeRegion(travel.HomeWorld).ToString());
            StatusLine("Current world", string.IsNullOrWhiteSpace(travel.CurrentWorld) ? "Not loaded" : travel.CurrentWorld);
            StatusLine("Current area", string.IsNullOrWhiteSpace(travel.CurrentTerritoryName) ? "Loading" : travel.CurrentTerritoryName);
            StatusLine("World travel", travel.NavigationStatus);
        }
        EndCard();
    }

    private void DrawTravelScreen()
    {
        var size = ImGui.GetContentRegionAvail();
        if (BeginCard("##sr-travel-screen", size, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var stop = runner.CurrentStop;
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 2f)));
            CenteredAccent(runner.Phase switch
            {
                RunPhase.TravelingDataCenter => "Changing data centres",
                RunPhase.TravelingWorld => "Changing worlds",
                RunPhase.TravelingCity or RunPhase.WaitingForArrival => "Teleporting",
                RunPhase.SendingMessages => "Sending messages",
                RunPhase.Paused => "Run paused",
                RunPhase.Completed => "Run complete",
                _ => "Preparing route",
            });
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 2f)));
            if (stop is not null)
                CenteredText($"{stop.CityName} · {stop.World} · {stop.DataCenter}");
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 3f)));
            var progress = runner.TotalStops == 0 ? 0f : runner.CompletedStops / (float)runner.TotalStops;
            var width = MathF.Min(TabletAppTheme.Px(600f), ImGui.GetContentRegionAvail().X - TabletAppTheme.Px(80f));
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - width) * 0.5f);
            ImGui.ProgressBar(progress, new Vector2(width, TabletAppTheme.Px(26f)), $"Route {runner.CompletedStops} / {runner.TotalStops}");
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 2f)));
            CenteredMuted(runner.CurrentTask, width);
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 3f)));
            if (runner.Phase == RunPhase.Completed)
            {
                DrawCompletionSummary();
                ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 7f)));
            }
            DrawRunChecklist();
        }
        EndCard();
    }

    private void DrawCompletionSummary()
    {
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, TabletAppTheme.Px(62f));
        var draw = ImGui.GetWindowDrawList();
        var accent = ImGui.ColorConvertFloat4ToU32(new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.82f));
        draw.AddRectFilled(start, start + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.09f, 0.07f, 0.14f, 0.96f)), TabletAppTheme.Px(7f));
        draw.AddRect(start, start + size, accent, TabletAppTheme.Px(7f), ImDrawFlags.None, TabletAppTheme.Px(1.5f));
        for (var x = TabletAppTheme.Px(10f); x < size.X; x += TabletAppTheme.Px(44f))
            draw.AddLine(start + new Vector2(x, size.Y), start + new Vector2(x + TabletAppTheme.Px(34f), 0f), ImGui.ColorConvertFloat4ToU32(new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.10f)));

        ImGui.SetCursorScreenPos(start + TabletAppTheme.Px(new Vector2(14f, 8f)));
        ImGui.TextColored(TabletAppTheme.AccentHover, "SHOUTRUNNER RUN COMPLETE");
        ImGui.SetCursorScreenPos(start + TabletAppTheme.Px(new Vector2(14f, 32f)));
        ImGui.TextUnformatted(runner.ReceiptCharacter);

        var completed = runner.ReceiptCompletedUtc.ToLocalTime();
        var duration = runner.ReceiptStartedUtc != default && runner.ReceiptCompletedUtc >= runner.ReceiptStartedUtc
            ? runner.ReceiptCompletedUtc - runner.ReceiptStartedUtc
            : TimeSpan.Zero;
        var timeZoneName = GetLocalTimeZoneAbbreviation(completed);
        var rightTop = $"{completed:yyyy-MM-dd  HH:mm:ss zzz} {timeZoneName} · Duration {duration:d\\.hh\\:mm\\:ss}";
        var stopSummary = runner.SkippedStopCount == 0
            ? $"{runner.SuccessfulStopCount} stops"
            : $"{runner.SuccessfulStopCount} sent · {runner.SkippedStopCount} skipped";
        var rightBottom = $"{stopSummary} · {runner.ReceiptWorldCount} worlds · {runner.ReceiptDataCenterCount} data centres · Gil spent on teleports: {runner.TeleportGilSpent:N0} · {runner.ReceiptCode}";
        ImGui.SetCursorScreenPos(new Vector2(start.X + size.X - ImGui.CalcTextSize(rightTop).X - TabletAppTheme.Px(14f), start.Y + TabletAppTheme.Px(8f)));
        ImGui.TextUnformatted(rightTop);
        ImGui.SetCursorScreenPos(new Vector2(start.X + size.X - ImGui.CalcTextSize(rightBottom).X - TabletAppTheme.Px(14f), start.Y + TabletAppTheme.Px(32f)));
        ImGui.TextColored(TabletAppTheme.MutedText, rightBottom);
        ImGui.SetCursorScreenPos(start + new Vector2(0f, size.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    private static string GetLocalTimeZoneAbbreviation(DateTime localTime)
    {
        var zone = TimeZoneInfo.Local;
        var displayName = zone.IsDaylightSavingTime(localTime)
            ? zone.DaylightName
            : zone.StandardName;
        var abbreviation = new string(displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => char.IsLetterOrDigit(word[0]))
            .Select(word => char.ToUpperInvariant(word[0]))
            .ToArray());
        return string.IsNullOrWhiteSpace(abbreviation)
            ? zone.Id
            : abbreviation;
    }

    private void DrawRunChecklist()
    {
        var route = runner.Route;
        var routeDataCenterOrder = route
            .Select(stop => stop.DataCenter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
        var dataCenters = Profile.Worlds
            .Select(WorldCatalog.FindWorld)
            .Where(world => world is not null)
            .Cast<WorldDefinition>()
            .GroupBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => routeDataCenterOrder.GetValueOrDefault(group.Key, int.MaxValue))
            .ThenBy(group => group.Key)
            .Take(5)
            .ToArray();
        if (dataCenters.Length == 0)
            return;

        ImGui.Separator();
        ImGui.TextColored(TabletAppTheme.AccentHover, "Run checklist");
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, TabletAppTheme.Px(new Vector2(4f, 1f)));
        if (!ImGui.BeginTable(
                "##sr-run-checklist",
                dataCenters.Length,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.PopStyleVar();
            return;
        }

        ImGui.TableNextRow();
        for (var columnIndex = 0; columnIndex < dataCenters.Length; columnIndex++)
        {
            ImGui.TableSetColumnIndex(columnIndex);
            var dataCenter = dataCenters[columnIndex];
            var worlds = dataCenter
                .OrderBy(world => FindFirstWorldStop(route, world.Name))
                .ThenBy(world => world.Name)
                .ToArray();
            var dcComplete = worlds.All(world => Profile.Cities.All(city =>
            {
                var index = FindRouteStop(route, world.Name, city);
                return index >= 0 && index < runner.CompletedStops && !runner.IsStopSkipped(index);
            }));
            ImGui.TextColored(
                dcComplete ? new Vector4(0.35f, 0.86f, 0.53f, 1f) : TabletAppTheme.AccentHover,
                dcComplete ? $"{dataCenter.Key}  complete" : dataCenter.Key);
            ImGui.Separator();
        }

        ImGui.TableNextRow();
        for (var columnIndex = 0; columnIndex < dataCenters.Length; columnIndex++)
        {
            ImGui.TableSetColumnIndex(columnIndex);
            var dataCenter = dataCenters[columnIndex];
            var worlds = dataCenter
                .OrderBy(world => FindFirstWorldStop(route, world.Name))
                .ThenBy(world => world.Name)
                .ToArray();
            foreach (var world in worlds)
                DrawWorldChecklistRow(route, dataCenter.Key, world.Name);
        }
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private void DrawWorldChecklistRow(IReadOnlyList<RouteStop> route, string dataCenter, string world)
    {
        var badgeWidth = TabletAppTheme.Px(20f);
        var badgeGap = TabletAppTheme.Px(3f);
        var badgesWidth = badgeWidth * WorldCatalog.Cities.Count + badgeGap * (WorldCatalog.Cities.Count - 1);
        var rightInset = TabletAppTheme.Px(7f);
        var rowHeight = TabletAppTheme.Px(22f);
        var start = ImGui.GetCursorScreenPos();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(world);

        ImGui.SetCursorScreenPos(start + new Vector2(0f, MathF.Max(0f, (rowHeight - textSize.Y) * 0.5f)));
        ImGui.TextUnformatted(world);
        ImGui.SetCursorScreenPos(new Vector2(
            MathF.Max(start.X + textSize.X + TabletAppTheme.Px(5f), start.X + availableWidth - badgesWidth - rightInset),
            start.Y + MathF.Max(0f, (rowHeight - TabletAppTheme.Px(18f)) * 0.5f)));

        for (var cityIndex = 0; cityIndex < WorldCatalog.Cities.Count; cityIndex++)
        {
            var city = WorldCatalog.Cities[cityIndex];
            var matchingIndex = FindRouteStop(route, world, city.Id);
            var configured = Profile.Cities.Contains(city.Id);
            DrawCityBadge(
                $"{dataCenter}-{world}-{city.Id}",
                city.Id,
                configured,
                configured && matchingIndex >= 0 && matchingIndex < runner.CompletedStops,
                configured && matchingIndex >= 0 && runner.IsStopSkipped(matchingIndex),
                configured && matchingIndex == runner.CompletedStops,
                city.Name);
            if (cityIndex < WorldCatalog.Cities.Count - 1)
                ImGui.SameLine(0f, badgeGap);
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0f, rowHeight));
        ImGui.Dummy(Vector2.Zero);
    }

    private static int FindFirstWorldStop(IReadOnlyList<RouteStop> route, string world)
    {
        for (var index = 0; index < route.Count; index++)
        {
            if (route[index].World.Equals(world, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return int.MaxValue;
    }

    private static int FindRouteStop(IReadOnlyList<RouteStop> route, string world, CityTarget city)
    {
        for (var index = 0; index < route.Count; index++)
        {
            if (route[index].World.Equals(world, StringComparison.OrdinalIgnoreCase) && route[index].City == city)
                return index;
        }
        return -1;
    }

    private void DrawCityBadge(string id, CityTarget city, bool configured, bool complete, bool skipped, bool active, string cityName)
    {
        var size = TabletAppTheme.Px(new Vector2(20f, 18f));
        var position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##sr-city-progress-{id}", size);
        var background = !configured
            ? new Vector4(0.12f, 0.11f, 0.16f, 0.7f)
            : skipped
                ? new Vector4(0.55f, 0.22f, 0.18f, 0.95f)
                : complete
                ? new Vector4(0.18f, 0.55f, 0.32f, 0.95f)
                : active
                    ? TabletAppTheme.Accent
                    : new Vector4(0.22f, 0.19f, 0.30f, 0.95f);
        var border = active
            ? TabletAppTheme.AccentHover
            : new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, configured ? 0.75f : 0.25f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, ImGui.ColorConvertFloat4ToU32(background), TabletAppTheme.Px(4f));
        draw.AddRect(position, position + size, ImGui.ColorConvertFloat4ToU32(border), TabletAppTheme.Px(4f));
        var texture = cityIcons.Get(city);
        if (texture is not null)
        {
            var imageSize = TabletAppTheme.Px(new Vector2(16f, 16f));
            var imagePosition = position + (size - imageSize) * 0.5f;
            draw.AddImage(texture.Handle, imagePosition, imagePosition + imageSize);
        }
        else
        {
            var display = city switch
            {
                CityTarget.LimsaLominsa => "L",
                CityTarget.Gridania => "G",
                _ => "U",
            };
            var textSize = ImGui.CalcTextSize(display);
            draw.AddText(position + (size - textSize) * 0.5f, ImGui.ColorConvertFloat4ToU32(configured ? TabletAppTheme.Text : TabletAppTheme.MutedText), display);
        }

        if (complete)
        {
            var markerRadius = TabletAppTheme.Px(4f);
            var markerCenter = position + new Vector2(size.X - markerRadius, markerRadius);
            draw.AddCircleFilled(markerCenter, markerRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f, 0.85f, 0.45f, 1f)));
            draw.AddText(markerCenter - TabletAppTheme.Px(new Vector2(3f, 6f)), ImGui.ColorConvertFloat4ToU32(Vector4.One), skipped ? "!" : "✓");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(configured
                ? $"{cityName}: {(skipped ? "Skipped" : complete ? "Complete" : active ? "Current stop" : "Pending")}" 
                : $"{cityName}: Not selected");
    }

    private void DrawProfiles()
    {
        if (runner.IsRunning)
        {
            TextMutedWrapped("Profile changes are locked while a run is active so its route and messages remain consistent.");
            ImGui.BeginDisabled();
        }
        if (BeginCard("##sr-profile-card", Vector2.Zero))
        {
            SectionHeader("Venue profiles");
            var names = persistence.Profiles.Keys.OrderBy(name => name).ToArray();
            var current = Array.FindIndex(names, name => name.Equals(config.ActiveVenueProfile, StringComparison.OrdinalIgnoreCase));
            current = Math.Max(0, current);
            ImGui.SetNextItemWidth(TabletAppTheme.Px(320f));
            if (ImGui.Combo("Active profile", ref current, names, names.Length) && current < names.Length)
                persistence.Activate(names[current]);

            ImGui.SetNextItemWidth(TabletAppTheme.Px(320f));
            ImGui.InputTextWithHint("##sr-new-profile", "New profile name", ref newProfileName, 64);
            ImGui.SameLine();
            if (ImGui.Button("Create profile"))
            {
                statusMessage = persistence.Create(newProfileName, copyProfile)
                    ? $"Created profile {config.ActiveVenueProfile}."
                    : "That profile already exists.";
                if (statusMessage.StartsWith("Created", StringComparison.Ordinal))
                    newProfileName = string.Empty;
            }
            ImGui.Checkbox("Copy the current profile settings", ref copyProfile);
            var canDelete = !Profile.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            if (!canDelete) ImGui.BeginDisabled();
            if (ImGui.Button("Delete current profile"))
                TabletAppTheme.OpenCenteredModal("Delete ShoutRunner profile?");
            if (!canDelete) ImGui.EndDisabled();
            if (!string.IsNullOrWhiteSpace(statusMessage))
                TextMutedWrapped(statusMessage);
        }
        EndCard();
        if (runner.IsRunning)
            ImGui.EndDisabled();

        if (TabletAppTheme.BeginCenteredModal("Delete ShoutRunner profile?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Delete venue profile '{Profile.Name}' and all of its ShoutRunner settings?");
            if (ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(120f, 0f))))
            {
                persistence.Delete(Profile.Name);
                TabletAppTheme.CloseCenteredModal();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(120f, 0f))))
                TabletAppTheme.CloseCenteredModal();
            TabletAppTheme.EndCenteredModal();
        }
    }

    private void DrawMessages()
    {
        TextMutedWrapped("Add one or more message blocks. Each block supports /shout, /yell, /say, or /echo and is limited to 400 characters.");
        if (runner.IsRunning) ImGui.BeginDisabled();
        foreach (var block in Profile.Messages.ToArray())
        {
            if (BeginCard($"##sr-message-{block.Id}", new Vector2(0f, TabletAppTheme.Px(150f))))
            {
                var channel = (int)block.Channel;
                ImGui.SetNextItemWidth(TabletAppTheme.Px(150f));
                if (ImGui.Combo("Channel", ref channel, Enum.GetNames<MessageChannel>(), Enum.GetValues<MessageChannel>().Length))
                {
                    block.Channel = (MessageChannel)channel;
                    persistence.SaveProfile(Profile);
                }
                ImGui.SameLine();
                ImGui.TextColored(block.Text.Length >= 400 ? new Vector4(0.96f, 0.46f, 0.40f, 1f) : TabletAppTheme.MutedText, $"{block.Text.Length} / 400");
                ImGui.SameLine();
                if (ImGui.Button($"Delete##{block.Id}"))
                {
                    deleteMessageId = block.Id;
                    TabletAppTheme.OpenCenteredModal("Delete message block?");
                }
                var text = block.Text;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputTextMultiline($"##sr-message-text-{block.Id}", ref text, 400, new Vector2(-1f, TabletAppTheme.Px(78f))))
                {
                    block.Text = text.Length > 400 ? text[..400] : text;
                    persistence.SaveProfile(Profile);
                }
            }
            EndCard();
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 6f)));
        }
        if (ImGui.Button("Add message block", TabletAppTheme.Px(new Vector2(170f, 30f))))
        {
            Profile.Messages.Add(new MessageBlock());
            persistence.SaveProfile(Profile);
        }
        if (runner.IsRunning) ImGui.EndDisabled();
    }

    private void DrawRoute()
    {
        if (runner.IsRunning) ImGui.BeginDisabled();
        if (BeginCard("##sr-city-route", new Vector2(0f, TabletAppTheme.Px(96f))))
        {
            SectionHeader("City states");
            foreach (var city in WorldCatalog.Cities)
            {
                var selected = Profile.Cities.Contains(city.Id);
                if (ImGui.Checkbox(city.Name, ref selected))
                {
                    if (selected) Profile.Cities.Add(city.Id); else Profile.Cities.Remove(city.Id);
                    persistence.SaveProfile(Profile);
                }
                if (city != WorldCatalog.Cities[^1]) ImGui.SameLine(0f, TabletAppTheme.Px(24f));
            }
        }
        EndCard();
        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 8f)));

        var developerMode = Profile.DeveloperMode;
        if (ImGui.Checkbox("Developer mode — show every region", ref developerMode))
        {
            Profile.DeveloperMode = developerMode;
            if (!developerMode)
            {
                var allowed = WorldCatalog.VisibleWorlds(travel.HomeWorld, false).Select(world => world.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Profile.Worlds.RemoveWhere(world => !allowed.Contains(world));
            }
            persistence.SaveProfile(Profile);
        }
        TextMutedWrapped($"Home world: {(string.IsNullOrWhiteSpace(travel.HomeWorld) ? "not loaded" : travel.HomeWorld)}. Visible travel region: {WorldCatalog.DetectHomeRegion(travel.HomeWorld)} plus Materia.");

        foreach (var dataCenter in WorldCatalog.VisibleWorlds(travel.HomeWorld, Profile.DeveloperMode).GroupBy(world => world.DataCenter))
        {
            var dataCenterWorlds = dataCenter.ToArray();
            var style = ImGui.GetStyle();
            var checkboxAndGap = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X;
            var widestWorld = dataCenterWorlds.Max(world => ImGui.CalcTextSize(world.Name).X);
            var minimumCellWidth = checkboxAndGap + widestWorld + TabletAppTheme.Px(14f);
            var innerWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X - style.WindowPadding.X * 2f);
            var columns = Math.Clamp((int)MathF.Floor(innerWidth / minimumCellWidth), 1, dataCenterWorlds.Length);
            var rows = (int)Math.Ceiling(dataCenterWorlds.Length / (double)columns);
            var cardHeight = style.WindowPadding.Y * 2f
                           + ImGui.GetFrameHeight()
                           + style.ItemSpacing.Y
                           + 1f
                           + style.ItemSpacing.Y
                           + rows * ImGui.GetFrameHeight()
                           + Math.Max(0, rows - 1) * style.ItemSpacing.Y;

            if (!BeginCard(
                    $"##sr-dc-{dataCenter.Key}",
                    new Vector2(0f, cardHeight),
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                EndCard();
                continue;
            }
            var allSelected = dataCenterWorlds.All(world => Profile.Worlds.Contains(world.Name));
            ImGui.TextColored(TabletAppTheme.AccentHover, dataCenter.Key);
            ImGui.SameLine(0f, TabletAppTheme.Px(12f));
            if (ImGui.Checkbox($"Select all##sr-dc-all-{dataCenter.Key}", ref allSelected))
            {
                foreach (var world in dataCenterWorlds)
                {
                    if (allSelected) Profile.Worlds.Add(world.Name); else Profile.Worlds.Remove(world.Name);
                }
                persistence.SaveProfile(Profile);
            }
            ImGui.Separator();
            if (ImGui.BeginTable(
                    $"##sr-world-grid-{dataCenter.Key}",
                    columns,
                    ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
            {
                foreach (var world in dataCenterWorlds)
                {
                    ImGui.TableNextColumn();
                    var selected = Profile.Worlds.Contains(world.Name);
                    if (ImGui.Checkbox(world.Name, ref selected))
                    {
                        if (selected) Profile.Worlds.Add(world.Name); else Profile.Worlds.Remove(world.Name);
                        persistence.SaveProfile(Profile);
                    }
                }
                ImGui.EndTable();
            }
            EndCard();
            ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 7f)));
        }
        if (runner.IsRunning) ImGui.EndDisabled();
    }

    private void DrawTravelSettings()
    {
        if (runner.IsRunning) ImGui.BeginDisabled();
        if (BeginCard("##sr-retry-settings", Vector2.Zero))
        {
            SectionHeader($"Travel fallback · {Profile.Name}");
            var firstDelay = Profile.InitialRetryDelaySeconds;
            var increase = Profile.RetryDelayIncreaseSeconds;
            var attempts = Profile.MaximumTravelAttempts;
            var messageDelay = Profile.MessageDelaySeconds;
            if (ImGui.InputInt("Initial retry delay (seconds)", ref firstDelay, 5, 10)) Profile.InitialRetryDelaySeconds = Math.Clamp(firstDelay, 5, 120);
            if (ImGui.InputInt("Additional seconds per attempt", ref increase, 1, 5)) Profile.RetryDelayIncreaseSeconds = Math.Clamp(increase, 0, 120);
            if (ImGui.InputInt("Maximum travel attempts", ref attempts, 1, 2)) Profile.MaximumTravelAttempts = Math.Clamp(attempts, 1, 20);
            if (ImGui.InputInt("Delay between message blocks", ref messageDelay, 1, 5)) Profile.MessageDelaySeconds = Math.Clamp(messageDelay, 1, 30);
            var tryAlternates = Profile.TryAlternateDataCenterWorlds;
            var alternatesChanged = ImGui.Checkbox("Try alternate worlds when entering a data centre", ref tryAlternates);
            if (alternatesChanged)
                Profile.TryAlternateDataCenterWorlds = tryAlternates;
            if (alternatesChanged || ImGui.IsItemEdited() || ImGui.IsAnyItemActive())
                persistence.SaveProfile(Profile);
            TextMutedWrapped("After the selected destination reaches its maximum attempts, try every other world on that data centre once as a gateway. If none work, skip that data centre and continue the run.");
        }
        EndCard();
        if (runner.IsRunning) ImGui.EndDisabled();
    }

    private void DrawStopConfirmation()
    {
        if (!TabletAppTheme.BeginCenteredModal("Stop ShoutRunner run?", ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.TextWrapped("Cancel the current run and clear its saved route progress? A future run will start fresh.");
        if (ImGui.Button("Stop run", TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            runner.Stop();
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep running", TabletAppTheme.Px(new Vector2(120f, 0f))))
            TabletAppTheme.CloseCenteredModal();
        TabletAppTheme.EndCenteredModal();
    }

    private void DrawMessageDeleteConfirmation()
    {
        if (!TabletAppTheme.BeginCenteredModal("Delete message block?", ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.TextWrapped("Delete this message block from the current venue profile?");
        if (ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            if (deleteMessageId is { } id)
            {
                Profile.Messages.RemoveAll(block => block.Id == id);
                persistence.SaveProfile(Profile);
            }
            deleteMessageId = null;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            deleteMessageId = null;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private static bool BeginCard(string id, Vector2 size, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TabletAppTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(TabletAppTheme.Accent.X, TabletAppTheme.Accent.Y, TabletAppTheme.Accent.Z, 0.65f));
        return ImGui.BeginChild(id, size, true, flags);
    }

    private static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private static void SectionHeader(string text)
    {
        ImGui.TextColored(TabletAppTheme.AccentHover, text);
        ImGui.Separator();
    }

    private static void StatusLine(string label, string value)
    {
        TextMuted(label);
        ImGui.SameLine(TabletAppTheme.Px(130f));
        ImGui.TextUnformatted(value);
    }

    private static void TextMuted(string text) => ImGui.TextColored(TabletAppTheme.MutedText, text);

    private static void TextMutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, TabletAppTheme.MutedText);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void CenteredText(string text)
    {
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowSize().X - ImGui.CalcTextSize(text).X) * 0.5f));
        ImGui.TextUnformatted(text);
    }

    private static void CenteredAccent(string text)
    {
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowSize().X - ImGui.CalcTextSize(text).X) * 0.5f));
        ImGui.TextColored(TabletAppTheme.AccentHover, text);
    }

    private static void CenteredMuted(string text, float width)
    {
        var textWidth = MathF.Min(ImGui.CalcTextSize(text).X, width);
        ImGui.SetCursorPosX((ImGui.GetWindowSize().X - textWidth) * 0.5f);
        ImGui.TextColored(TabletAppTheme.MutedText, text);
    }
}
