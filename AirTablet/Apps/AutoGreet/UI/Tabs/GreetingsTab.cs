using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

public sealed class GreetingsTab
{
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private readonly MacroEngine macroEngine;
    private string newProfileName = "New Profile";
    private Guid newProfileVenueId = Guid.Empty;
    private readonly HashSet<Guid> openMacroEditors = new();
    private readonly HashSet<Guid> openProfileEditors = new();

    public GreetingsTab(VenueService venues, PersistenceService persistence, MacroEngine macroEngine)
    {
        this.venues = venues;
        this.persistence = persistence;
        this.macroEngine = macroEngine;
    }

    public void Draw()
    {
        UiHelpers.Section("Greeting profiles");
        UiHelpers.TextDisabledWrapped("Greeting profiles for every venue are shown here so you can edit all macros without changing the active venue.");
        DrawAddProfileControls();
        ImGui.Separator();

        foreach (var venue in venues.Venues.ToArray())
        {
            var flags = venue.Id == venues.ActiveVenue.Id ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{venue.Name}##greetings-venue-{venue.Id}", flags))
                continue;

            ImGui.Indent();
            if (venue.GreetingProfiles.Count == 0)
            {
                ImGui.TextDisabled("No greeting profiles configured for this venue.");
            }

            foreach (var profile in venue.GreetingProfiles.ToArray())
                DrawProfile(venue, profile);

            ImGui.Unindent();
        }
    }

    private void DrawAddProfileControls()
    {
        if (newProfileVenueId == Guid.Empty || venues.Venues.All(v => v.Id != newProfileVenueId))
            newProfileVenueId = venues.ActiveVenue.Id;

        var targetVenue = venues.Venues.FirstOrDefault(v => v.Id == newProfileVenueId) ?? venues.ActiveVenue;

        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(240));
        ImGui.InputText("New profile name##new-profile-name", ref newProfileName, 64);
        CaptureKeyboardWhileEditing();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220));
        if (ImGui.BeginCombo("Venue##new-profile-venue", targetVenue.Name))
        {
            foreach (var venue in venues.Venues)
            {
                var selected = venue.Id == newProfileVenueId;
                if (ImGui.Selectable($"{venue.Name}##new-profile-venue-{venue.Id}", selected))
                    newProfileVenueId = venue.Id;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Add profile##add-greeting-profile"))
        {
            var profile = GreetingProfile.CreateDefault();
            profile.Name = MakeUniqueProfileName(targetVenue, string.IsNullOrWhiteSpace(newProfileName) ? "New Profile" : newProfileName.Trim());
            targetVenue.GreetingProfiles.Add(profile);
            targetVenue.ActiveGreetingProfileId = profile.Id;
            venues.RepairVenueData(targetVenue);
            persistence.SaveNow();
        }
    }

    private void DrawProfile(VenueProfile venue, GreetingProfile profile)
    {
        ImGui.PushID($"profile-{venue.Id}-{profile.Id}");

        var isOpen = openProfileEditors.Contains(profile.Id);
        var arrow = isOpen ? "▼" : "▶";
        var isActiveForVenue = venue.ActiveGreetingProfileId == profile.Id;
        var displayName = string.IsNullOrWhiteSpace(profile.Name) ? "Unnamed profile" : profile.Name;
        var activeLabel = isActiveForVenue ? $"  [active for {venue.Name}]" : string.Empty;

        if (ImGui.Selectable($"{arrow} {displayName}{activeLabel}##profile-header", false))
        {
            if (isOpen)
                openProfileEditors.Remove(profile.Id);
            else
                openProfileEditors.Add(profile.Id);

            isOpen = !isOpen;
        }

        if (!isOpen)
        {
            ImGui.PopID();
            return;
        }
        var name = profile.Name;
        if (ImGui.InputText("Name", ref name, 80))
            profile.Name = name;
        CaptureKeyboardWhileEditing();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            profile.Name = MakeUniqueProfileName(venue, profile.Name, profile.Id);
            venues.RepairVenueData(venue);
            persistence.SaveNow();
        }

        if (ImGui.Button("Use profile for this venue"))
        {
            venue.ActiveGreetingProfileId = profile.Id;
            venues.RepairVenueData(venue);
            persistence.SaveNow();
        }
        ImGui.SameLine();
        if (ImGui.Button("Add macro"))
        {
            var macro = new GreetingMacro();
            profile.Macros.Add(macro);
            openMacroEditors.Add(macro.Id);
            persistence.SaveNow();
        }
        if (profile.Macros.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button("Sort greetings 0-9, A-Z"))
            {
                SortMacrosByName(profile);
                persistence.SaveNow();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Sorts this profile's greetings by name using natural alphanumeric order. Numbers come before letters.");
        }
        if (venue.GreetingProfiles.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete profile"))
            {
                venue.GreetingProfiles.Remove(profile);
                if (venue.ActiveGreetingProfileId == profile.Id)
                    venue.ActiveGreetingProfileId = venue.GreetingProfiles.FirstOrDefault()?.Id ?? Guid.Empty;
                venues.RepairVenueData(venue);
                persistence.SaveNow();
                openProfileEditors.Remove(profile.Id);
                ImGui.PopID();
                return;
            }
        }

        foreach (var macro in profile.Macros.Where(m => m.Category != GreetingCategory.Blacklisted).ToArray())
            DrawMacro(venue, profile, macro);

        ImGui.PopID();
    }

    private static string MakeUniqueProfileName(VenueProfile venue, string requestedName, Guid? currentProfileId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? "Profile" : requestedName.Trim();
        var name = baseName;
        var suffix = 2;

        while (venue.GreetingProfiles.Any(p => p.Id != currentProfileId && string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";

        return name;
    }

    private static void CaptureKeyboardWhileEditing()
    {
        if (!ImGui.IsItemActive() && !ImGui.IsItemFocused()) return;

        var io = ImGui.GetIO();
        io.WantCaptureKeyboard = true;
        io.WantTextInput = true;
    }

    private static void SortMacrosByName(GreetingProfile profile)
    {
        profile.Macros.Sort((left, right) => NaturalCompare(GetSortName(left), GetSortName(right)));
    }

    private static string GetSortName(GreetingMacro macro)
        => string.IsNullOrWhiteSpace(macro.Name) ? "~" : macro.Name.Trim();

    private static int NaturalCompare(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftChar = left[leftIndex];
            var rightChar = right[rightIndex];

            if (char.IsDigit(leftChar) && char.IsDigit(rightChar))
            {
                var leftStart = leftIndex;
                var rightStart = rightIndex;

                while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                    leftIndex++;
                while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                    rightIndex++;

                var leftNumber = left[leftStart..leftIndex].TrimStart('0');
                var rightNumber = right[rightStart..rightIndex].TrimStart('0');
                if (leftNumber.Length == 0)
                    leftNumber = "0";
                if (rightNumber.Length == 0)
                    rightNumber = "0";

                var lengthCompare = leftNumber.Length.CompareTo(rightNumber.Length);
                if (lengthCompare != 0)
                    return lengthCompare;

                var numberCompare = string.Compare(leftNumber, rightNumber, StringComparison.Ordinal);
                if (numberCompare != 0)
                    return numberCompare;

                continue;
            }

            var compare = char.ToUpperInvariant(leftChar).CompareTo(char.ToUpperInvariant(rightChar));
            if (compare != 0)
                return compare;

            leftIndex++;
            rightIndex++;
        }

        return left.Length.CompareTo(right.Length);
    }

    private void DrawMacro(VenueProfile venue, GreetingProfile profile, GreetingMacro macro)
    {
        ImGui.PushID(macro.Id.ToString());

        var isOpen = openMacroEditors.Contains(macro.Id);
        var arrow = isOpen ? "▼" : "▶";
        var displayName = string.IsNullOrWhiteSpace(macro.Name) ? "Unnamed macro" : macro.Name;

        if (ImGui.Selectable($"{arrow} {displayName} ({macro.Category})##macro-header", false))
        {
            if (isOpen)
                openMacroEditors.Remove(macro.Id);
            else
                openMacroEditors.Add(macro.Id);

            isOpen = !isOpen;
        }

        if (isOpen)
        {
            ImGui.Indent();
            var enabled = macro.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled)) { macro.Enabled = enabled; persistence.SaveNow(); }
            var name = macro.Name;
            if (ImGui.InputText("Macro name", ref name, 80)) { macro.Name = name; }
            CaptureKeyboardWhileEditing();
            if (ImGui.IsItemDeactivatedAfterEdit()) persistence.SaveNow();
            var cat = (int)macro.Category;
            if (cat == (int)GreetingCategory.Blacklisted) cat = (int)GreetingCategory.FirstTime;
            if (ImGui.Combo("Category", ref cat, "First-time\0Returning\0VIP\0")) { macro.Category = (GreetingCategory)Math.Clamp(cat, 0, 2); persistence.SaveNow(); }
            ImGui.TextUnformatted("Macro script");
            var script = macro.Script;
            if (ImGui.InputTextMultiline(
                    "##macro-script",
                    ref script,
                    8192,
                    AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(-1, 160))))
            {
                macro.Script = script;
            }
            CaptureKeyboardWhileEditing();
            if (ImGui.IsItemDeactivatedAfterEdit()) persistence.SaveNow();
            foreach (var issue in macroEngine.Validate(macro))
            {
                ImGui.TextColored(
                    new System.Numerics.Vector4(1f, 0.68f, 0.25f, 1f),
                    $"Line {issue.LineNumber}: {issue.Message}");
            }
            UiHelpers.TextDisabledWrapped("Supported: /tell <t>, /tell <playername>, /t <t>, /t <playername>, /say, /shout, /yell, FFXIV emote commands such as /dote <t>, /hug <t>, /wave, /hum, or /beesknees, /wait X, /wait.X, /waitX, and inline waits like <wait.02>. Use <playername> to show Name@World in non-tell chat. Unsupported syntax pauses AutoGreet and appears in the Log tab.");
            if (ImGui.Button("Clone macro"))
            {
                var clone = new GreetingMacro
                {
                    Name = string.IsNullOrWhiteSpace(macro.Name) ? "Copy of macro" : $"{macro.Name} Copy",
                    Category = macro.Category,
                    Script = macro.Script,
                    Enabled = macro.Enabled,
                };

                var index = profile.Macros.IndexOf(macro);
                if (index >= 0 && index < profile.Macros.Count - 1)
                    profile.Macros.Insert(index + 1, clone);
                else
                    profile.Macros.Add(clone);

                openMacroEditors.Add(clone.Id);
                persistence.SaveNow();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete macro"))
            {
                profile.Macros.Remove(macro);
                openMacroEditors.Remove(macro.Id);
                venues.RepairVenueData(venue);
                persistence.SaveNow();
                ImGui.Unindent();
                ImGui.PopID();
                return;
            }
            ImGui.Unindent();
        }
        ImGui.PopID();
    }
}
