namespace GambaAssistant.Services;

// One instance per observed native Trade window. No timer or closed window is
// sufficient on its own: the signed balance delta must match the locked offer.
internal sealed class TradeBalanceEvidence(long startingGil)
{
    private long incoming;
    private long outgoing;
    private bool armed;
    private bool matched;
    private bool disabled;
    private DateTime? confirmationClosed;
    private bool sawFinalPrompt;
    public bool IncomingRecorded { get; private set; }
    public bool OutgoingRecorded { get; private set; }
    public bool Completed { get; private set; }
    public long Incoming => incoming;
    public long Outgoing => outgoing;
    public bool NeedsBalanceRead => armed && !matched && !disabled && !Completed;

    public void ObserveOffer(long offeredToOperator, long offeredByOperator, bool finalConfirmation)
    {
        if (Completed || disabled || matched) return;
        if (offeredToOperator is < 0 or > 1_000_000 || offeredByOperator is < 0 or > 1_000_000)
        {
            disabled = true;
            return;
        }
        incoming = offeredToOperator;
        outgoing = offeredByOperator;
        if (!finalConfirmation)
        {
            // Returning to offer editing dismisses this confirmation attempt.
            // A later confirmation in the same Trade window gets its own short
            // observation deadline, not the previous prompt's closing time.
            confirmationClosed = null;
            sawFinalPrompt = false;
        }
        armed = finalConfirmation;
    }

    public bool Observe(long? currentGil, bool? finalPromptVisible, bool? tradeVisible, DateTime now, bool finalAccepted = false)
    {
        if (disabled || Completed || !armed) return false;
        if (finalPromptVisible == true) sawFinalPrompt = true;
        if ((sawFinalPrompt && finalPromptVisible == false) || finalAccepted || tradeVisible == false) confirmationClosed ??= now;
        if (!matched && confirmationClosed is not null && now - confirmationClosed.Value > TimeSpan.FromSeconds(3))
        {
            disabled = true;
            return false;
        }
        if (!matched && currentGil is not null)
        {
            var delta = currentGil.Value - startingGil;
            var expected = incoming - outgoing;
            // Zero-net exchanges cannot be proven from a balance alone.
            if (expected != 0 && delta == expected) matched = true;
            else if (delta != 0) disabled = true;
        }
        if (!matched || disabled || tradeVisible != false) return false;
        Completed = true;
        return true;
    }

    public bool Matches(long amount, bool isIncoming) => amount > 0 && amount == (isIncoming ? incoming : outgoing);
    public void Record(bool isIncoming)
    {
        if (isIncoming) IncomingRecorded = true;
        else OutgoingRecorded = true;
    }
    public void Cancel() => disabled = true;
}
