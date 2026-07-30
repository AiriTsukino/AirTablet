using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ShiftKeeper.Models;

namespace ShiftKeeper.Services;

public sealed unsafe class TradePaymentService : IDisposable
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private DateTimeOffset nextTradePollUtc = DateTimeOffset.MinValue;
    private bool tradeWindowWasOpen;
    private Guid pendingStaffId;
    private string pendingStaffDisplay = string.Empty;
    private DateTimeOffset pendingStaffUtc = DateTimeOffset.MinValue;
    private Guid recentlyCompletedStaffId;
    private DateTimeOffset recentlyCompletedStaffUtc = DateTimeOffset.MinValue;
    private string lastProcessedSignature = string.Empty;
    private DateTimeOffset lastProcessedUtc = DateTimeOffset.MinValue;
    private bool disposed;

    private static readonly Regex OutgoingTradeWithNameRegex = new(@"\byou\s+(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\s+(?:to\s+)?(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeAmountOnlyRegex = new(@"\b(?:you\s+)?(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeRequestRegex = new(@"\b(?:trade\s+request\s+sent\s+to|you\s+(?:have\s+)?sent\s+(?:a\s+)?trade\s+request\s+to)\s+(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncomingTradeRequestRegex = new(@"\b(?:trade\s+request\s+from\s+(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)|(?<name2>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\s+(?:has\s+)?sent\s+you\s+(?:a\s+)?trade\s+request)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeWindowWithRegex = new(@"\b(?:now\s+trading\s+with|trading\s+with|trade\s+window\s+with|trade\s+with)\s+(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AwaitingTradeRegex = new(@"\bawaiting\s+trade\s+confirmation\s+from\s+(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeCompleteRegex = new(@"\btrade\s+complete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string DetectionStatus { get; private set; } = "Waiting for an outgoing gil trade to a staff member on the active venue roster.";

    public TradePaymentService(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed || !config.AutoDetectTradePayments || DateTimeOffset.UtcNow < nextTradePollUtc) return;
        nextTradePollUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);

        var isOpen = TryGetTradeAddon(out var addon);
        if (isOpen && !tradeWindowWasOpen)
            ClearPendingStaff(clearRecentlyCompleted: true);
        if (isOpen && pendingStaffId == Guid.Empty)
        {
            if (!TryCaptureCurrentTarget("Trade window target"))
                TryCaptureStaffFromTradeWindow(addon);
        }
        if (isOpen && pendingStaffId != Guid.Empty)
            pendingStaffUtc = DateTimeOffset.UtcNow;
        tradeWindowWasOpen = isOpen;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (disposed || !config.AutoDetectTradePayments) return;
        var sender = message.Sender.ToString();
        var body = message.Message.ToString();
        var chatType = GetChatTypeName(message);
        if (!IsSystemTradeChatLine(chatType, sender, body)) return;

        var cleanedSender = StripChatNoise(sender);
        var cleanedBody = StripChatNoise(body);
        var combined = string.IsNullOrWhiteSpace(cleanedSender) ? cleanedBody : $"{cleanedSender} {cleanedBody}";
        TrackTradeConversation(combined);

        if (!combined.Contains("gil", StringComparison.OrdinalIgnoreCase)) return;

        var named = OutgoingTradeWithNameRegex.Match(combined);
        if (named.Success && TryParseGilAmount(named.Groups["amount"].Value, out var namedAmount))
        {
            if (TryResolveStaffByName(named.Groups["name"].Value, out var namedStaff))
                ApplyOutgoingPayment(namedStaff, namedAmount, "named outgoing system trade line");
            else
                DetectionStatus = $"Ignored {namedAmount:N0} gil outgoing trade: {named.Groups["name"].Value.Trim()} did not resolve to one exact active-roster staff identity.";
            return;
        }

        var amountOnly = OutgoingTradeAmountOnlyRegex.Match(combined);
        if (!amountOnly.Success || !TryParseGilAmount(amountOnly.Groups["amount"].Value, out var amount)) return;

        StaffMember? staff;
        if (!TryGetFreshPendingStaff(out staff))
        {
            TryCaptureCurrentTarget("Outgoing gil trade target");
            TryGetFreshPendingStaff(out staff);
        }

        if (staff is null)
        {
            DetectionStatus = $"Ignored {amount:N0} gil outgoing trade because no exact staff recipient and home world could be confirmed.";
            return;
        }

        ApplyOutgoingPayment(staff, amount, "outgoing system trade line with confirmed trade recipient");
    }

    private void TrackTradeConversation(string combined)
    {
        var outgoing = OutgoingTradeRequestRegex.Match(combined);
        if (outgoing.Success) { TrySetPendingByName(outgoing.Groups["name"].Value, "outgoing trade request"); return; }

        var incoming = IncomingTradeRequestRegex.Match(combined);
        if (incoming.Success)
        {
            var name = incoming.Groups["name"].Success ? incoming.Groups["name"].Value : incoming.Groups["name2"].Value;
            TrySetPendingByName(name, "incoming staff trade request");
            return;
        }

        var tradeWindow = TradeWindowWithRegex.Match(combined);
        if (tradeWindow.Success) { TrySetPendingByName(tradeWindow.Groups["name"].Value, "trade window message"); return; }

        var awaiting = AwaitingTradeRegex.Match(combined);
        if (awaiting.Success) { TrySetPendingByName(awaiting.Groups["name"].Value, "trade confirmation message"); return; }

        if (TradeCompleteRegex.IsMatch(combined) && DateTimeOffset.UtcNow - lastProcessedUtc > TimeSpan.FromSeconds(2))
        {
            if (pendingStaffId != Guid.Empty)
            {
                recentlyCompletedStaffId = pendingStaffId;
                recentlyCompletedStaffUtc = DateTimeOffset.UtcNow;
            }
            ClearPendingStaff();
        }
    }

    private bool TryCaptureCurrentTarget(string source)
    {
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter player) return false;
        var name = player.Name.ToString().Trim();
        string world;
        try { world = player.HomeWorld.Value.Name.ToString().Trim(); }
        catch { return false; }
        if (!TryMatchStaff(name, world, out var staff)) return false;
        SetPendingStaff(staff, source);
        return true;
    }

    private void TryCaptureStaffFromTradeWindow(AtkUnitBase* addon)
    {
        if (addon is null) return;
        var nodeList = addon->UldManager.NodeList;
        var nodeCount = Math.Min((int)addon->UldManager.NodeListCount, 512);
        if (nodeList is null || nodeCount <= 0) return;

        var text = new List<string>();
        for (var i = 0; i < nodeCount; i++)
        {
            var node = nodeList[i];
            if (node is null || (uint)node->Type != 3) continue;
            var textNode = node->GetAsAtkTextNode();
            if (textNode is null) continue;
            var value = StripChatNoise(textNode->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(value)) text.Add(value);
        }

        var combined = string.Join(" ", text);
        var nameMatches = persistence.ActiveVenue.Staff
            .Where(x => x.Enabled && Regex.IsMatch(combined, $@"(?<![\p{{L}}'\-]){Regex.Escape(x.Name.Trim())}(?![\p{{L}}'\-])", RegexOptions.IgnoreCase))
            .Select(x => NormalizeName(x.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (nameMatches.Count == 1)
            TrySetPendingByName(nameMatches[0], "Trade window text");
    }

    private bool TrySetPendingByName(string rawName, string source)
    {
        if (!TryResolveStaffByName(rawName, out var staff)) return false;
        SetPendingStaff(staff, source);
        return true;
    }

    private bool TryResolveStaffByName(string rawName, out StaffMember staff)
    {
        staff = null!;
        var normalized = NormalizeName(rawName);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var visibleWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter player || NormalizeName(player.Name.ToString()) != normalized) continue;
            try
            {
                var world = player.HomeWorld.Value.Name.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(world)) visibleWorlds.Add(world);
            }
            catch { }
        }

        if (visibleWorlds.Count > 0)
            return visibleWorlds.Count == 1 && TryMatchStaff(rawName, visibleWorlds.First(), out staff);

        var candidates = GetEligibleStaff().Where(x => NormalizeName(x.Name) == normalized).ToList();
        if (candidates.Count != 1) return false;
        staff = candidates[0];
        return true;
    }

    private bool TryMatchStaff(string name, string world, out StaffMember staff)
    {
        staff = null!;
        var normalizedName = NormalizeName(name);
        var candidates = GetEligibleStaff()
            .Where(x => NormalizeName(x.Name) == normalizedName && x.World.Trim().Equals(world.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count != 1) return false;
        staff = candidates[0];
        return true;
    }

    private IEnumerable<StaffMember> GetEligibleStaff()
    {
        var venue = persistence.ActiveVenue;
        return venue.Staff.Where(member => member.Enabled && venue.CurrentNight.GetOrCreate(member.Id).Scheduled);
    }

    private void SetPendingStaff(StaffMember staff, string source)
    {
        pendingStaffId = staff.Id;
        pendingStaffDisplay = staff.TellRecipient;
        pendingStaffUtc = DateTimeOffset.UtcNow;
        DetectionStatus = $"Confirmed trade recipient {pendingStaffDisplay} from {source}; waiting for outgoing gil confirmation.";
    }

    private bool TryGetFreshPendingStaff(out StaffMember? staff)
    {
        staff = null;
        if (pendingStaffId == Guid.Empty || DateTimeOffset.UtcNow - pendingStaffUtc > TimeSpan.FromSeconds(15))
        {
            ClearPendingStaff();
            if (recentlyCompletedStaffId == Guid.Empty || DateTimeOffset.UtcNow - recentlyCompletedStaffUtc > TimeSpan.FromSeconds(10))
            {
                recentlyCompletedStaffId = Guid.Empty;
                recentlyCompletedStaffUtc = DateTimeOffset.MinValue;
                return false;
            }

            staff = GetEligibleStaff().FirstOrDefault(x => x.Id == recentlyCompletedStaffId);
            return staff is not null;
        }

        staff = GetEligibleStaff().FirstOrDefault(x => x.Id == pendingStaffId);
        return staff is not null;
    }

    private void ApplyOutgoingPayment(StaffMember staff, long tradedGil, string source)
    {
        var venue = persistence.ActiveVenue;
        var record = venue.CurrentNight.GetOrCreate(staff.Id);
        var signature = $"{venue.CurrentNight.Id}:{staff.Id}:{tradedGil}";
        if (signature == lastProcessedSignature && DateTimeOffset.UtcNow - lastProcessedUtc < TimeSpan.FromSeconds(8))
            return;

        var maximum = PayCalculator.CalculateMaximumShiftPay(staff, venue, config);
        var availableCredit = Math.Max(0, maximum - record.TotalPaidGil);
        var applied = Math.Min(Math.Max(0, tradedGil), availableCredit);
        if (applied <= 0)
        {
            DetectionStatus = $"Detected {tradedGil:N0} gil paid to {staff.TellRecipient}, but their tracked shift pay already has full payment credit.";
            ClearPendingStaff(keepStatus: true, clearRecentlyCompleted: true);
            return;
        }

        record.TotalPaidGil += applied;
        record.PaidAmount = (float)Math.Min(float.MaxValue, (double)record.TotalPaidGil);
        record.LastTradePaymentGil = applied;
        record.LastTradePaymentUtc = DateTimeOffset.UtcNow;
        var remaining = PayCalculator.CalculateRemainingDue(staff, record, venue, config);
        var workComplete = PayCalculator.IsWorkComplete(staff, record, venue);
        if (!record.Paid || record.PaidAutomatically)
        {
            record.Paid = remaining == 0 && workComplete;
            record.PaidAutomatically = record.Paid;
            record.PaidUtc = record.Paid ? DateTimeOffset.UtcNow : null;
        }
        persistence.SaveNow();

        lastProcessedSignature = signature;
        lastProcessedUtc = DateTimeOffset.UtcNow;
        var excess = tradedGil - applied;
        DetectionStatus = excess > 0
            ? $"Applied {applied:N0} gil to {staff.TellRecipient}; {excess:N0} gil exceeded their maximum tracked shift pay. Remaining due: {remaining:N0} gil."
            : $"Applied {applied:N0} gil to {staff.TellRecipient}. Remaining due: {remaining:N0} gil{(record.Paid ? "; marked Paid." : ".")}";
        DalamudServices.Log.Information("ShiftKeeper {Source}: applied {Applied} gil trade payment to {Staff}; remaining due {Remaining} gil.", source, applied, staff.TellRecipient, remaining);
        ClearPendingStaff(keepStatus: true, clearRecentlyCompleted: true);
    }

    private void ClearPendingStaff(bool keepStatus = false, bool clearRecentlyCompleted = false)
    {
        pendingStaffId = Guid.Empty;
        pendingStaffDisplay = string.Empty;
        pendingStaffUtc = DateTimeOffset.MinValue;
        if (clearRecentlyCompleted)
        {
            recentlyCompletedStaffId = Guid.Empty;
            recentlyCompletedStaffUtc = DateTimeOffset.MinValue;
        }
        if (!keepStatus) DetectionStatus = "Waiting for an outgoing gil trade to a staff member on the active venue roster.";
    }

    private static bool TryGetTradeAddon(out AtkUnitBase* addon)
    {
        addon = null;
        try
        {
            var addonPtr = DalamudServices.GameGui.GetAddonByName("Trade");
            if (addonPtr.Address == nint.Zero) return false;
            addon = (AtkUnitBase*)addonPtr.Address;
            return addon is not null && addon->IsVisible && addon->IsReady;
        }
        catch { addon = null; return false; }
    }

    private static string GetChatTypeName(IHandleableChatMessage message)
    {
        try { return message.GetType().GetProperty("Type")?.GetValue(message)?.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool IsSystemTradeChatLine(string chatType, string sender, string body)
    {
        var type = chatType.Trim();
        var cleanedSender = StripChatNoise(sender);
        var cleanedBody = StripChatNoise(body);
        foreach (var blocked in new[] { "Say", "Yell", "Shout", "Party", "Tell", "FreeCompany", "Linkshell", "CrossLinkShell", "CrossWorldLinkshell", "Alliance", "NoviceNetwork", "PvpTeam", "Echo", "Emote", "CustomEmote" })
            if (type.Contains(blocked, StringComparison.OrdinalIgnoreCase)) return false;

        if (type.Contains("System", StringComparison.OrdinalIgnoreCase) || type.Contains("Log", StringComparison.OrdinalIgnoreCase) || type.Contains("Notice", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(cleanedSender) && !cleanedSender.Equals("System", StringComparison.OrdinalIgnoreCase))
            return false;
        return cleanedBody.Contains("trade", StringComparison.OrdinalIgnoreCase) || cleanedBody.Contains("You hand over", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripChatNoise(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(c => !char.IsControl(c) && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '\'' or '-' or ',' or '.' or '@')).ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var name = value.Trim();
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        return new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool TryParseGilAmount(string raw, out long amount) =>
        long.TryParse(raw.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) && amount > 0;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        DalamudServices.ChatGui.ChatMessage -= OnChatMessage;
    }
}
