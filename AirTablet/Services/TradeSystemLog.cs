using Dalamud.Game.Chat;

namespace AirTablet.Services;

// Read-only observation of the pre-display game queue. Never unhide a message,
// retain its native wrappers, or run the debug formatter (which has side effects).
internal static class TradeSystemLog
{
    public static bool IsCompletion(ILogMessage message)
    {
        // Verified against the installed game's LogMessage sheet. IDs identify
        // the event independently of language and any rendered chat filtering.
        return message.LogMessageId == 38 && message.GameData.IsValid;
    }

    public static bool HasPartner(ILogMessage message, string name, string world)
        => Matches(message.SourceEntity, name, world) || Matches(message.TargetEntity, name, world);

    private static bool Matches(ILogMessageEntity? entity, string name, string world)
        => entity is { IsPlayer: true } && entity.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase)
            && entity.HomeWorld.IsValid && entity.HomeWorld.Value.Name.ExtractText().Equals(world, StringComparison.OrdinalIgnoreCase);
}
