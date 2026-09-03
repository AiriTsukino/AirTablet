using System.Globalization;
using System.Text.RegularExpressions;

namespace PrizeTrader;

internal enum TradeMessageKind { None, Handover, Complete, Failed }

internal static class TradeChatEvidence
{
    private static readonly Regex OutgoingGil = new(@"\b(?:you\s+)?(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeComplete = new(@"\btrade\s+complete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeFailed = new(@"\b(?:trade\s+(?:cancelled|canceled|declined|failed)|unable\s+to\s+complete\s+(?:the\s+)?trade|could\s+not\s+complete\s+(?:the\s+)?trade|other\s+player\s+is\s+busy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static TradeMessageKind Classify(string kind, string sender, string body, out long amount)
    {
        amount = 0;
        // An empty sender alone does not make a line trade evidence: music,
        // emotes and many other unrelated channels also have empty senders.
        if (!string.IsNullOrWhiteSpace(sender) ||
            !(kind.Contains("System", StringComparison.OrdinalIgnoreCase) ||
              kind.Contains("Log", StringComparison.OrdinalIgnoreCase) ||
              kind.Contains("Notice", StringComparison.OrdinalIgnoreCase)))
            return TradeMessageKind.None;

        if (TradeFailed.IsMatch(body)) return TradeMessageKind.Failed;
        var outgoing = OutgoingGil.Match(body);
        if (outgoing.Success && long.TryParse(outgoing.Groups["amount"].Value.Replace(",", string.Empty),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) && amount > 0)
            return TradeMessageKind.Handover;
        return TradeComplete.IsMatch(body) ? TradeMessageKind.Complete : TradeMessageKind.None;
    }
}
