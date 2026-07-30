using System.Text.RegularExpressions;
using AutoGreet.Models;
using Dalamud.Game.Text;
using Dalamud.Game.Chat;

namespace AutoGreet.Services;

public sealed class GreetingService : IDisposable
{
    private static readonly Regex InlineWaitRegex = new(@"<wait\.(?<seconds>\d+(?:\.\d+)?)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly VenueService venues;
    private readonly Dictionary<string, PendingTell> pendingTellLines = new(StringComparer.OrdinalIgnoreCase);
    private VisitorService? visitorService;

    public string LastCommandText { get; set; } = "None";
    public string LastOutgoingTellObserved { get; private set; } = "None";
    public string LastGreetingConfirmation { get; private set; } = "None";

    public GreetingService(VenueService venues)
    {
        this.venues = venues;
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
    }

    public void AttachVisitorService(VisitorService service) => visitorService = service;

    public GreetingMacro? PickMacro(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return null;
        if (venue.Blacklist.Contains(key.ToString())) return null;
        var profile = venues.GetGreetingProfileForVenue(venue);
        var category = GetCategory(key);
        if (category == GreetingCategory.Vip)
            return PickVipMacro(venue, profile, key);

        var selectedId = venue.GetActiveMacroId(category);
        var selected = selectedId == Guid.Empty
            ? null
            : profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == category && m.Id == selectedId);

        return selected
            ?? profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == category)
            ?? profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == GreetingCategory.FirstTime);
    }

    private static GreetingMacro? PickVipMacro(VenueProfile venue, GreetingProfile profile, VisitorKey key)
    {
        var defaultTierId = venue.GetDefaultVipTier().Id;
        var visitorTierId = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor)
                            && venue.GetVipTier(visitor.VipTierId) is not null
            ? visitor.VipTierId
            : defaultTierId;

        var selectedIds = new[]
            {
                venue.GetActiveVipMacroId(visitorTierId),
                venue.GetActiveVipMacroId(defaultTierId),
                venue.ActiveVipMacroId,
            }
            .Where(id => id != Guid.Empty)
            .Distinct();

        foreach (var selectedId in selectedIds)
        {
            var selected = profile.Macros.FirstOrDefault(m =>
                m.Enabled && m.Category == GreetingCategory.Vip && m.Id == selectedId);
            if (selected is not null)
                return selected;
        }

        return profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == GreetingCategory.Vip)
               ?? profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == GreetingCategory.FirstTime);
    }

    public GreetingMacro? PickMacroById(Guid macroId)
    {
        if (macroId == Guid.Empty) return null;
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return null;
        var profile = venues.GetGreetingProfileForVenue(venue);
        return profile.Macros.FirstOrDefault(m => m.Enabled && m.Id == macroId && m.Category != GreetingCategory.Blacklisted);
    }

    public GreetingCategory GetCategory(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return GreetingCategory.FirstTime;
        if (venue.Blacklist.Contains(key.ToString())) return GreetingCategory.Blacklisted;
        if (venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor) && (visitor.Vip || visitor.VipTierId != Guid.Empty)) return GreetingCategory.Vip;
        if (venue.LifetimeVisitors.TryGetValue(key.ToString(), out visitor) && visitor.HasBeenGreeted) return GreetingCategory.Returning;
        return GreetingCategory.FirstTime;
    }

    public bool IsConfiguredGreetingTell(string message)
    {
        var normalized = Normalize(message);
        return venues.AllGreetingProfiles
            .SelectMany(x => x.Profile.Macros)
            .Where(m => m.Category != GreetingCategory.Blacklisted)
            .SelectMany(GetTellLines)
            .Any(line => Normalize(line).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void RegisterExpectedTell(VisitorKey key, string message, bool markVisitorGreeted = true)
    {
        var normalized = Normalize(message);
        pendingTellLines[normalized] = new PendingTell(key, markVisitorGreeted);
        LastGreetingConfirmation = $"Waiting for outgoing tell to {key.Display}: {message}";
    }

    public bool IsGreetingConfirmed(VisitorKey key) => venues.ActiveVenueOrNull is { } venue && VenueService.ContainsKey(venue.Session.Greeted, key);

    public void ConfirmTellCommandSent(VisitorKey key, string message, bool markVisitorGreeted)
    {
        // Some Dalamud/API combinations do not echo local /tell commands through ChatGui.ChatMessage.
        // CommandManager.ProcessCommand returning without throwing is the best local confirmation available.
        // We still keep the ChatMessage observer for diagnostics when the outgoing tell event is available.
        var normalized = Normalize(message);
        var shouldMark = markVisitorGreeted;
        if (pendingTellLines.Remove(normalized, out var pending))
            shouldMark = pending.MarkVisitorGreeted;

        LastOutgoingTellObserved = message;
        LastGreetingConfirmation = shouldMark
            ? $"Confirmed main greeting command for {key.Display}."
            : $"Confirmed non-greeting macro command for {key.Display}.";
        if (shouldMark)
            visitorService?.MarkGreeted(key);
    }

    public bool MacroHasTell(GreetingMacro macro) => GetTellLines(macro).Any();

    public void ConfirmNonTellMacroCompleted(VisitorKey key, bool markVisitorGreeted)
    {
        LastGreetingConfirmation = markVisitorGreeted
            ? $"Confirmed main non-tell greeting command for {key.Display}."
            : $"Confirmed non-greeting macro command for {key.Display}.";

        if (markVisitorGreeted)
            visitorService?.MarkGreeted(key);
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        if (chatMessage.IsHandled || chatMessage.LogKind != XivChatType.TellOutgoing) return;

        var messageText = chatMessage.Message.TextValue;
        LastOutgoingTellObserved = messageText;
        var text = Normalize(messageText);
        if (!pendingTellLines.TryGetValue(text, out var pending))
        {
            LastGreetingConfirmation = "Outgoing tell observed, but it did not match a pending AutoGreet tell.";
            return;
        }
        if (!IsConfiguredGreetingTell(messageText))
        {
            LastGreetingConfirmation = "Outgoing tell observed, but it did not match the configured greeting macros.";
            return;
        }

        pendingTellLines.Remove(text);
        LastGreetingConfirmation = $"Confirmed greeting for {pending.Key.Display}.";
        if (pending.MarkVisitorGreeted)
            visitorService?.MarkGreeted(pending.Key);
    }

    private static IEnumerable<string> GetTellLines(GreetingMacro macro)
    {
        foreach (var raw in macro.Script.Split('\n'))
        {
            var line = InlineWaitRegex.Replace(raw.Trim(), string.Empty).Trim();
            if (TryReadTellLine(line, "/tell <t>", 9, out var tellText)
                || TryReadTellLine(line, "/tell <playername>", 18, out tellText)
                || TryReadTellLine(line, "/t <t>", 6, out tellText)
                || TryReadTellLine(line, "/t <playername>", 15, out tellText))
            {
                yield return tellText;
            }
        }
    }

    private static bool TryReadTellLine(string line, string prefix, int messageStartIndex, out string tellText)
    {
        tellText = string.Empty;
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        tellText = line[messageStartIndex..].Trim();
        return !string.IsNullOrWhiteSpace(tellText);
    }

    public static string Normalize(string text)
    {
        var normalized = text
            .Replace("<t>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<playername>", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public void Dispose() => DalamudServices.ChatGui.ChatMessage -= OnChatMessage;

    private readonly record struct PendingTell(VisitorKey Key, bool MarkVisitorGreeted);
}
