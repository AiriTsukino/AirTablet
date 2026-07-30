using AutoGreet.Services;
using AutoGreet.UI.Components;
using AutoGreet.UI.Tabs;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI;

public sealed class SettingsWindow : Window
{
    private readonly Configuration config;
    private readonly SettingsTab settings;

    public SettingsWindow(Configuration config, VenueService venueService, VisitorService visitorService, PersistenceService persistence, DetectionService detectionService, GreetingService greetingService, SoundService soundService, EmoteResumeService emoteResumeService)
        : base("AutoGreet Settings###AutoGreetSettingsWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(780, 540)),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue)
        };

        var greetings = new GreetingsTab(venueService, persistence);
        var venues = new VenuesTab(venueService, persistence, detectionService);
        var vipBlacklist = new VipBlacklistTab(venueService, visitorService, persistence);
        settings = new SettingsTab(config, venueService, persistence, detectionService, greetingService, soundService, emoteResumeService, greetings, venues, vipBlacklist);
    }

    public override void PreDraw() => AutoGreetTheme.Push();

    public override void PostDraw() => AutoGreetTheme.Pop();

    public override void Draw() => settings.Draw();
}
