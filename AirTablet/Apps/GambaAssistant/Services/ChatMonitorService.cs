using System.Text.RegularExpressions;
using Dalamud.Game.Chat;

namespace GambaAssistant.Services;

/// <summary>
/// Watches live chat/log text for dealer dice results and conservative trade notifications.
/// This intentionally stays text-based so it fails safe when game/Dalamud internals change.
/// </summary>
public sealed class ChatMonitorService : IDisposable
{
    private readonly DiceService dice;
    private readonly TradeMonitorService trades;
    private readonly LogService log;
    private string? pendingTradePlayerName;
    private string? pendingTradeDirection;
    private DateTime pendingTradeStartedUtc;
    private long pendingTradeLastAmount;
    private TradeTransferDirection pendingTransferDirection;

    // FFXIV/Dalamud/Chat 2 can expose dice text with slightly different formatting,
    // for example "You roll a 7.", "FirstName LastName rolls a 7.",
    // "Random! FirstName LastName rolls 7 (1-13)", or localized payload fragments.
    // Keep the parser conservative around the word roll/random/dice, but avoid relying
    // on one exact sentence shape.
    private static readonly Regex DiceResultAfterRangeRegex = new(@"\(\s*\d+\s*-\s*\d+\s*\)\s*(?<value>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiceResultBeforeRangeRegex = new(@"(?<value>\d{1,3})\s*\(\s*\d+\s*-\s*\d+\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiceAfterRollRegex = new(@"(?:you\s+roll|rolls?|rolled|random|dice)[^0-9]{0,48}(?<value>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameBeforeRollRegex = new(@"(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\s+(?:rolls?|rolled)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncomingTradeWithNameRegex = new(@"(?:(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\s+(?:trades|gives|gave|pays|paid)\s+(?:you\s+)?(?<amount>[\d,]+)\s+gil|you\s+(?:receive|received|have\s+received|obtain|obtained|gain|gained)\s+(?<amount2>[\d,]+)\s+gil\s+from\s+(?<name2>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncomingTradeAmountOnlyRegex = new(@"\b(?:you\s+)?(?:receive|received|have\s+received|obtain|obtained|gain|gained)\s+(?<amount>[\d,]+)\s+gil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeWithNameRegex = new(@"\byou\s+(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\s+(?:to\s+)?(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeAmountOnlyRegex = new(@"\b(?:you\s+)?(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // FFXIV may render a World icon and world name immediately after a
    // two-part character name. Capture only the character name so current
    // party matching is stable on both local- and cross-world trades.
    private const string CharacterNameCapture = @"(?<name>[\p{L}][\p{L}'\-]*\s+[\p{L}][\p{L}'\-]*)";
    private const string OptionalWorldName = @"(?:\s+[\p{L}][\p{L}'\-]*)?";
    private static readonly Regex TradeRequestRegex = new($@"\b{CharacterNameCapture}{OptionalWorldName}\s+(?:wishes|wants|would\s+like|has\s+sent\s+you\s+a\s+request|sent\s+you\s+a\s+trade\s+request|offers)\s+(?:to\s+)?trade(?:\s+with\s+you)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncomingTradeRequestRegex = new($@"\b(?:trade\s+request\s+from\s+{CharacterNameCapture}{OptionalWorldName}|(?<name2>[\p{{L}}][\p{{L}}'\-]*\s+[\p{{L}}][\p{{L}}'\-]*){OptionalWorldName}\s+(?:has\s+)?sent\s+you\s+(?:a\s+)?trade\s+request)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeRequestRegex = new($@"\b(?:trade\s+request\s+sent\s+to|you\s+(?:have\s+)?sent\s+(?:a\s+)?trade\s+request\s+to)\s+{CharacterNameCapture}{OptionalWorldName}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeWindowWithRegex = new($@"\b(?:now\s+trading\s+with|trading\s+with|trade\s+window\s+with|trade\s+with)\s+{CharacterNameCapture}{OptionalWorldName}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AwaitingTradeRegex = new($@"\bawaiting\s+trade\s+confirmation\s+from\s+{CharacterNameCapture}{OptionalWorldName}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeCompleteRegex = new(@"\btrade\s+complete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeEndedWithoutCompletionRegex = new(@"\b(?:trade\s+(?:cancelled|canceled|declined|failed)|unable\s+to\s+complete\s+trade)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ChatMonitorService(DiceService dice, TradeMonitorService trades, LogService log)
    {
        this.dice = dice;
        this.trades = trades;
        this.log = log;
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        // Read the immutable game-authored payloads. This prevents another
        // plugin's chat edits from being mistaken for a trade notification.
        var sender = message.OriginalSender.ToString();
        var body = message.OriginalMessage.ToString();
        InspectMessage(sender, body, GetChatTypeName(message), LooksLikeRealDiceMessage(message), GetMessagePayloadSummary(message));
    }

    private static string GetChatTypeName(IHandleableChatMessage message)
    {
        try
        {
            return message.LogKind.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void InspectMessage(string sender, string body, string chatType, bool hasGameDicePayload, string payloadSummary)
    {
        if (string.IsNullOrWhiteSpace(sender) && string.IsNullOrWhiteSpace(body))
            return;

        TryConsumeDice(sender, body, chatType, hasGameDicePayload, payloadSummary);
        TryDetectTrade(sender, body, chatType);
    }

    private void TryConsumeDice(string sender, string body, string chatType, bool hasGameDicePayload, string payloadSummary)
    {
        if (dice.Pending is null)
            return;

        var combined = string.IsNullOrWhiteSpace(sender) ? body : $"{sender} {body}";
        var normalizedCombined = StripChatNoise(combined);
        var looksLikeDice = LooksLikeDiceLine(normalizedCombined, chatType);
        if (!looksLikeDice)
            return;

        if (!hasGameDicePayload)
        {
            log.Add(LogCategory.Dice, $"Pending dice ignored; dice-looking chat text did not contain the game's dice icon/autotranslate payloads. chatType='{chatType}', sender='{sender}', body='{body}', payloads='{payloadSummary}'.");
            return;
        }

        if (!TryParseDiceResult(normalizedCombined, out var value, out var parseSource))
        {
            log.Add(LogCategory.Dice, $"Pending dice ignored; could not parse result from chatType='{chatType}', sender='{sender}', body='{body}', normalized='{normalizedCombined}'.");
            return;
        }

        var rollerName = ExtractRollerName(sender, normalizedCombined);
        var identityConfidence = IsLikelyDealerRoll(sender, normalizedCombined, rollerName, chatType);
        if (!identityConfidence)
        {
            log.Add(LogCategory.Dice, $"Pending dice ignored; roll was not identified as dealer. chatType='{chatType}', sender='{sender}', body='{body}', parsedRoller='{rollerName}', value={value}.");
            return;
        }

        if (dice.TryConsumeDealerDice(value, rollerName, allowUnidentifiedDealer: true))
            log.Add(LogCategory.Dice, $"Live dice consumed: value={value}, parse='{parseSource}', chatType='{chatType}', sender='{sender}', body='{body}'.");
    }


    private static bool LooksLikeRealDiceMessage(IHandleableChatMessage message)
    {
        // Do not trust visible chat text alone. Players can type text such as
        // "Random! 729" or "You roll a 7." manually, but real FFXIV dice
        // messages include SeString icon/autotranslate payloads in the body.
        try
        {
            var sawPayload = false;
            foreach (var payload in message.Message.Payloads)
            {
                sawPayload = true;
                var typeName = payload.GetType().Name;
                if (typeName.Equals("TextPayload", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (typeName.Contains("Icon", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("AutoTranslate", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Bitmap", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return sawPayload && false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMessagePayloadSummary(IHandleableChatMessage message)
    {
        try
        {
            var types = message.Message.Payloads
                .Select(p => p.GetType().Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            return types.Length == 0 ? "none" : string.Join(", ", types);
        }
        catch
        {
            return "unavailable";
        }
    }

    private bool TryParseDiceResult(string normalizedCombined, out int value, out string parseSource)
    {
        value = 0;
        parseSource = string.Empty;

        // FFXIV party dice can arrive as:
        //   Random! (1-13) 10
        // The parenthesized values are only the dice range. Never let a generic
        // "Random" match consume the 1 from that range as the actual roll.
        // Try explicit range-aware parsing first, then generic parsing only
        // against a copy with ranges removed.
        var afterRange = DiceResultAfterRangeRegex.Match(normalizedCombined);
        if (afterRange.Success && int.TryParse(afterRange.Groups["value"].Value, out value) && IsValidDiceValue(value))
        {
            parseSource = "after-range";
            return true;
        }

        var beforeRange = DiceResultBeforeRangeRegex.Match(normalizedCombined);
        if (beforeRange.Success && int.TryParse(beforeRange.Groups["value"].Value, out value) && IsValidDiceValue(value))
        {
            parseSource = "before-range";
            return true;
        }

        var withoutRanges = RemoveDiceRanges(normalizedCombined);

        // For Random! lines, the roll value is the remaining standalone number.
        // This handles Chat 2/Dalamud payloads like "Random! (1-13) 10" even
        // when punctuation around the range is transformed by Dalamud.
        if (withoutRanges.Contains("random", StringComparison.OrdinalIgnoreCase))
        {
            var randomNumbers = Regex.Matches(withoutRanges, @"(?<![A-Za-z0-9])\d{1,3}(?![A-Za-z0-9])");
            for (var i = randomNumbers.Count - 1; i >= 0; i--)
            {
                if (int.TryParse(randomNumbers[i].Value, out value) && IsValidDiceValue(value))
                {
                    parseSource = "random-last-number";
                    return true;
                }
            }
        }

        var match = DiceAfterRollRegex.Match(withoutRanges);
        if (match.Success && int.TryParse(match.Groups["value"].Value, out value) && IsValidDiceValue(value))
        {
            parseSource = "after-roll-keyword";
            return true;
        }

        // Last-resort fallback for stripped dice lines: use the last standalone
        // number after removing ranges, never the first range number.
        var allNumbers = Regex.Matches(withoutRanges, @"(?<![A-Za-z0-9])\d{1,3}(?![A-Za-z0-9])");
        for (var i = allNumbers.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(allNumbers[i].Value, out value) && IsValidDiceValue(value))
            {
                parseSource = "last-number-no-range";
                return true;
            }
        }

        return false;
    }

    private static string RemoveDiceRanges(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Remove common range shapes after StripChatNoise, including:
        //   (1-13), 1-13, and variants with spaces.
        var cleaned = Regex.Replace(text, @"\(\s*\d+\s*-\s*\d+\s*\)", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)(random\s*!?\s*)\d+\s*-\s*\d+", "$1 ");
        // Some Dalamud payload conversions can drop the dash, resulting in
        // "Random 1 13 10". Treat the first two numbers after Random as the range
        // only when another number follows them.
        cleaned = Regex.Replace(cleaned, @"(?i)(random\s*!?\s*)\d+\s+\d+(?=\s+\d{1,3}(?![A-Za-z0-9]))", "$1 ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private bool IsValidDiceValue(int value)
    {
        var sides = Math.Max(1, dice.DiceSides);
        return value >= 1 && value <= sides;
    }

    private static bool LooksLikeDiceLine(string text, string chatType)
    {
        if (!string.IsNullOrWhiteSpace(chatType) && chatType.Contains("dice", StringComparison.OrdinalIgnoreCase))
            return true;

        return text.Contains("roll", StringComparison.OrdinalIgnoreCase)
            || text.Contains("random", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dice", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractRollerName(string sender, string normalizedCombined)
    {
        if (normalizedCombined.Contains("you roll", StringComparison.OrdinalIgnoreCase))
            return GetLocalCharacterName();

        var nameMatch = NameBeforeRollRegex.Match(normalizedCombined);
        if (nameMatch.Success)
            return nameMatch.Groups["name"].Value.Trim();

        var cleanedSender = StripChatNoise(sender).Trim();
        return string.IsNullOrWhiteSpace(cleanedSender) ? GetLocalCharacterName() : cleanedSender;
    }

    private static bool IsLikelyDealerRoll(string sender, string normalizedCombined, string rollerName, string chatType)
    {
        var localName = GetLocalCharacterName();
        if (string.IsNullOrWhiteSpace(localName))
            return true;

        if (normalizedCombined.Contains("you roll", StringComparison.OrdinalIgnoreCase))
            return true;

        if (NameMatchesDealer(rollerName, localName))
            return true;

        if (NameMatchesDealer(sender, localName))
            return true;

        if (normalizedCombined.Contains(localName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Some clients/chat replacements expose party dice result lines with no sender and
        // stripped player names. During a pending plugin-requested roll, consume dice chat
        // lines that cannot be attributed to anyone else so the live table does not deadlock.
        var senderMissing = string.IsNullOrWhiteSpace(StripChatNoise(sender));
        var rollerMissing = string.IsNullOrWhiteSpace(StripChatNoise(rollerName));
        var diceTyped = !string.IsNullOrWhiteSpace(chatType) && chatType.Contains("dice", StringComparison.OrdinalIgnoreCase);
        return senderMissing && (rollerMissing || diceTyped);
    }

    private static bool NameMatchesDealer(string value, string localName)
    {
        var cleanedValue = StripChatNoise(value);
        if (string.IsNullOrWhiteSpace(cleanedValue) || string.IsNullOrWhiteSpace(localName))
            return false;

        return string.Equals(cleanedValue, localName, StringComparison.OrdinalIgnoreCase)
            || cleanedValue.Contains(localName, StringComparison.OrdinalIgnoreCase)
            || localName.Contains(cleanedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLocalCharacterName()
    {
        try
        {
            return DalamudServices.PlayerState.IsLoaded ? DalamudServices.PlayerState.CharacterName.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string StripChatNoise(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Replace private-use World/icon glyphs with a separator instead of
        // deleting them. Deleting the glyph joined "Hartsblood" and "Maduin"
        // into one token and prevented current-party matching.
        var chars = value
            .Where(c => !char.IsControl(c))
            .Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '\'' or '-' or ',' or '.'
                ? c
                : ' ')
            .ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    private static bool IsSystemTradeChatLine(string chatType, string sender, string body)
    {
        var type = chatType.Trim();
        var cleanedSender = StripChatNoise(sender);
        var cleanedBody = StripChatNoise(body);

        foreach (var blocked in new[]
        {
            "Say", "Yell", "Shout", "Party", "CrossParty", "Tell", "FreeCompany",
            "Linkshell", "Ls1", "Ls2", "Ls3", "Ls4", "Ls5", "Ls6", "Ls7", "Ls8",
            "CrossLinkShell", "CrossWorldLinkshell", "Alliance", "NoviceNetwork",
            "PvpTeam", "Echo", "Emote", "CustomEmote", "StandardEmote", "Gm"
        })
        {
            if (type.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Trade notifications can use more than one non-player LogKind
        // (SystemMessage, Notice, Progress, or LootNotice depending on the
        // individual line). Player-authored channels were rejected above, and
        // genuine trade lines have no character sender.
        return (string.IsNullOrWhiteSpace(cleanedSender)
                || string.Equals(cleanedSender, "System", StringComparison.OrdinalIgnoreCase))
            && LooksLikeTradeSystemBody(cleanedBody);
    }

    private static bool LooksLikeTradeSystemBody(string body)
        => !string.IsNullOrWhiteSpace(body)
           && (body.Contains("trade", StringComparison.OrdinalIgnoreCase)
               || body.Contains("You receive", StringComparison.OrdinalIgnoreCase)
               || body.Contains("You hand over", StringComparison.OrdinalIgnoreCase));

    private void TryDetectTrade(string sender, string body, string chatType)
    {
        if (!trades.AutomaticDetectionEnabled)
        {
            ClearTradeConversationState();
            return;
        }

        if (!IsSystemTradeChatLine(chatType, sender, body))
        {
            if (LooksLikeTradeSystemBody(StripChatNoise(body)))
                log.Add(LogCategory.Trades, $"Ignored trade-looking line from non-system channel '{chatType}' with sender '{StripChatNoise(sender)}'.");
            return;
        }

        var cleanedSender = StripChatNoise(sender);
        var cleanedBody = StripChatNoise(body);
        var combined = string.IsNullOrWhiteSpace(cleanedSender) ? cleanedBody : $"{cleanedSender} {cleanedBody}";
        log.Add(LogCategory.Trades, $"Trusted FFXIV trade line [{chatType}]: {cleanedBody}");

        TrackTradeConversation(combined, sender, body);

        if (!combined.Contains("gil", StringComparison.OrdinalIgnoreCase))
            return;

        var namedMatch = IncomingTradeWithNameRegex.Match(combined);
        if (namedMatch.Success)
        {
            var rawAmount = namedMatch.Groups["amount"].Success ? namedMatch.Groups["amount"].Value : namedMatch.Groups["amount2"].Value;
            if (!TryParseGilAmount(rawAmount, out var namedAmount))
                return;

            var playerName = namedMatch.Groups["name"].Success ? namedMatch.Groups["name"].Value : namedMatch.Groups["name2"].Value;
            if (string.IsNullOrWhiteSpace(playerName) && !string.IsNullOrWhiteSpace(cleanedSender))
                playerName = cleanedSender;

            if (!string.IsNullOrWhiteSpace(playerName))
            {
                StageTradeTransfer(playerName, namedAmount, TradeTransferDirection.Incoming);
                return;
            }
        }

        var outgoingNamedMatch = OutgoingTradeWithNameRegex.Match(combined);
        if (outgoingNamedMatch.Success && TryParseGilAmount(outgoingNamedMatch.Groups["amount"].Value, out var outgoingNamedAmount))
        {
            StageTradeTransfer(outgoingNamedMatch.Groups["name"].Value, outgoingNamedAmount, TradeTransferDirection.Outgoing);
            return;
        }

        // Amount-only transfer lines are attributed only to the player captured
        // from the trusted trade request/confirmation sequence. Target and
        // single-player guesses are intentionally not used.
        var outgoingAmountOnlyMatch = OutgoingTradeAmountOnlyRegex.Match(combined);
        if (outgoingAmountOnlyMatch.Success && TryParseGilAmount(outgoingAmountOnlyMatch.Groups["amount"].Value, out var outgoingAmountOnly))
        {
            var outgoingPlayer = GetFreshTradePlayer();
            if (!string.IsNullOrWhiteSpace(outgoingPlayer))
            {
                StageTradeTransfer(outgoingPlayer, outgoingAmountOnly, TradeTransferDirection.Outgoing);
                return;
            }

            log.Add(LogCategory.Warnings, $"Detected outgoing gil amount {outgoingAmountOnly:N0}, but no current-party recipient was captured from the active trade sequence. Line: sender='{sender}', body='{body}'.");
            return;
        }

        // Many FFXIV system trade lines only expose the amount, for example
        // "You receive 50,000 gil." Attribute them to the current-party player
        // captured from the trusted trade sequence.
        var amountOnlyMatch = IncomingTradeAmountOnlyRegex.Match(combined);
        if (!amountOnlyMatch.Success || !TryParseGilAmount(amountOnlyMatch.Groups["amount"].Value, out var amountOnly))
        {
            if (combined.Contains("trade", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("receive", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("obtain", StringComparison.OrdinalIgnoreCase))
            {
                log.Add(LogCategory.Trades, $"Unmatched gil/trade chat line: sender='{sender}', body='{body}'.");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(cleanedSender)
            && !NameMatchesDealer(cleanedSender, GetLocalCharacterName())
            && TradeMonitorService.TryResolveCurrentPartyMember(cleanedSender, out _))
        {
            StageTradeTransfer(cleanedSender, amountOnly, TradeTransferDirection.Incoming);
            return;
        }

        var pendingPlayer = GetFreshTradePlayer();
        if (!string.IsNullOrWhiteSpace(pendingPlayer))
        {
            StageTradeTransfer(pendingPlayer, amountOnly, TradeTransferDirection.Incoming);
            return;
        }

        log.Add(LogCategory.Warnings, $"Detected incoming gil amount {amountOnly:N0}, but no current-party sender was captured from the active trade sequence. Line: sender='{sender}', body='{body}'.");
    }

    private void TrackTradeConversation(string combined, string sender, string body)
    {
        var outgoingRequestMatch = OutgoingTradeRequestRegex.Match(combined);
        if (outgoingRequestMatch.Success)
        {
            SetPendingTradePlayer(outgoingRequestMatch.Groups["name"].Value, "outgoing trade request", "outgoing", sender, body);
            return;
        }

        var requestMatch = TradeRequestRegex.Match(combined);
        if (requestMatch.Success)
        {
            SetPendingTradePlayer(requestMatch.Groups["name"].Value, "incoming trade request", "incoming", sender, body);
            return;
        }

        var incomingRequestMatch = IncomingTradeRequestRegex.Match(combined);
        if (incomingRequestMatch.Success)
        {
            var name = incomingRequestMatch.Groups["name"].Success ? incomingRequestMatch.Groups["name"].Value : incomingRequestMatch.Groups["name2"].Value;
            SetPendingTradePlayer(name, "incoming trade request", "incoming", sender, body);
            return;
        }

        var tradeWindowMatch = TradeWindowWithRegex.Match(combined);
        if (tradeWindowMatch.Success)
        {
            var direction = string.IsNullOrWhiteSpace(pendingTradeDirection) ? "active" : pendingTradeDirection!;
            SetPendingTradePlayer(tradeWindowMatch.Groups["name"].Value, "trade window", direction, sender, body);
            return;
        }

        var awaitingMatch = AwaitingTradeRegex.Match(combined);
        if (awaitingMatch.Success)
        {
            var awaitedName = awaitingMatch.Groups["name"].Value;
            var direction = string.Equals(pendingTradeDirection, "outgoing", StringComparison.OrdinalIgnoreCase)
                ? "outgoing"
                : string.Equals(pendingTradeDirection, "incoming", StringComparison.OrdinalIgnoreCase)
                    ? "incoming"
                    : "active";
            SetPendingTradePlayer(awaitedName, "trade confirmation", direction, sender, body);
            return;
        }

        if (TradeCompleteRegex.IsMatch(combined))
        {
            if (!string.IsNullOrWhiteSpace(pendingTradePlayerName))
            {
                var amountText = pendingTradeLastAmount > 0 ? $" after {pendingTradeLastAmount:N0} gil was detected" : string.Empty;
                var directionText = string.IsNullOrWhiteSpace(pendingTradeDirection) ? string.Empty : $" ({pendingTradeDirection})";
                log.Add(LogCategory.Trades, $"Trade sequence complete for {pendingTradePlayerName}{directionText}{amountText}.");
            }

            pendingTradePlayerName = null;
            pendingTradeDirection = null;
            pendingTradeLastAmount = 0;
            pendingTransferDirection = TradeTransferDirection.None;
            trades.MarkTradeWindowClosed();
            return;
        }

        if (TradeEndedWithoutCompletionRegex.IsMatch(combined))
        {
            log.Add(LogCategory.Trades, "Trade ended without completion; no staged bank adjustment was applied.");
            ClearTradeConversationState();
        }
    }

    private void SetPendingTradePlayer(string rawName, string source, string direction, string sender, string body)
    {
        var name = StripChatNoise(rawName).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!TradeMonitorService.TryResolveCurrentPartyMember(name, out var partyMember)
            && !TradeMonitorService.TryResolveCurrentPartyMemberFromTradeText(rawName, out partyMember))
        {
            ClearTradeConversationState();
            log.Add(LogCategory.Trades, $"Ignored {source} for {name}: the player is not currently in the party.");
            return;
        }

        pendingTradePlayerName = partyMember.Display;
        pendingTradeDirection = direction;
        pendingTradeStartedUtc = DateTime.UtcNow;
        pendingTradeLastAmount = 0;
        pendingTransferDirection = TradeTransferDirection.None;
        trades.MarkTradeWindowActive(partyMember.Display);
        log.Add(LogCategory.Trades, $"Detected {source} for current party member {partyMember.Display}; commands are paused until the verified trade completes. sender='{sender}', body='{body}'.");
    }

    private void ClearTradeConversationState()
    {
        pendingTradePlayerName = null;
        pendingTradeDirection = null;
        pendingTradeStartedUtc = DateTime.MinValue;
        pendingTradeLastAmount = 0;
        pendingTransferDirection = TradeTransferDirection.None;
        trades.MarkTradeWindowClosed();
    }

    private string? GetFreshTradePlayer()
    {
        // The request initiator does not determine gil direction. Either participant
        // can place gil in the window, so the trusted "receive" / "hand over" amount
        // line determines the bank operation while this state only identifies player.
        if (IsFreshTradeAttribution(pendingTradePlayerName, pendingTradeStartedUtc, TimeSpan.FromMinutes(2)))
            return pendingTradePlayerName;

        if (!string.IsNullOrWhiteSpace(pendingTradePlayerName) && DateTime.UtcNow - pendingTradeStartedUtc > TimeSpan.FromMinutes(2))
        {
            log.Add(LogCategory.Warnings, $"Ignored stale pending trade attribution for {pendingTradePlayerName}; no gil amount was detected within 2 minutes.");
            pendingTradePlayerName = null;
            pendingTradeDirection = null;
            pendingTradeLastAmount = 0;
            trades.MarkTradeWindowClosed();
        }

        return null;
    }

    private void StageTradeTransfer(string playerName, long amount, TradeTransferDirection direction)
    {
        if (amount <= 0 || direction == TradeTransferDirection.None)
            return;

        if (!TradeMonitorService.TryResolveCurrentPartyMember(playerName, out var partyMember))
        {
            log.Add(LogCategory.Trades, $"Ignored staged trade for {playerName}: the player is not currently in the party.");
            ClearTradeConversationState();
            return;
        }

        pendingTradePlayerName = partyMember.Display;
        pendingTradeLastAmount = amount;
        pendingTransferDirection = direction;
        trades.MarkTradeWindowActive(partyMember.Display);
        var applied = direction == TradeTransferDirection.Incoming
            ? trades.TryAddDetectedIncomingTrade(partyMember.Display, amount, "Verified incoming gil transfer from FFXIV system message")
            : trades.TryAddSystemConfirmedOutgoingTrade(partyMember.Display, amount, "Verified outgoing gil transfer from FFXIV system message");
        log.Add(
            applied ? LogCategory.Trades : LogCategory.Warnings,
            $"{(applied ? "Applied" : "Could not apply")} trusted system trade for {partyMember.Display}: {(direction == TradeTransferDirection.Incoming ? "+" : "-")}{amount:N0} gil. Waiting for Trade complete before resuming queued commands.");
    }

    private static bool IsFreshTradeAttribution(string? playerName, DateTime startedUtc, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return false;

        return DateTime.UtcNow - startedUtc <= maxAge;
    }

    private static bool NamesLooselyMatch(string? left, string? right)
    {
        var a = StripChatNoise(left ?? string.Empty).ToLowerInvariant();
        var b = StripChatNoise(right ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        return a == b || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseGilAmount(string rawAmount, out long amount)
    {
        amount = 0;
        return !string.IsNullOrWhiteSpace(rawAmount)
            && long.TryParse(rawAmount.Replace(",", string.Empty), out amount)
            && amount > 0;
    }

    public void Dispose()
    {
        DalamudServices.ChatGui.ChatMessage -= OnChatMessage;
    }

    private enum TradeTransferDirection
    {
        None,
        Incoming,
        Outgoing
    }
}
