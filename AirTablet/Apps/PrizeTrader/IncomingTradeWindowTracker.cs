namespace PrizeTrader;

// UI lifecycle only. Window closure never records a payment or adjusts a bank.
internal sealed class IncomingTradeWindowTracker
{
    private bool seenOpen;
    public bool AwaitingClosure { get; private set; }

    public bool Observe(bool? visible)
    {
        if (visible is null) return false;
        if (visible.Value)
        {
            if (!AwaitingClosure) seenOpen = true;
            return false;
        }
        var closed = seenOpen;
        seenOpen = false;
        AwaitingClosure = false;
        return closed;
    }

    public void Finish(bool? visible)
    {
        seenOpen = false;
        // A completion chat line can arrive before the addon disappears. Do not
        // re-adopt that same closing window as a fresh incoming trade.
        AwaitingClosure = visible != false;
    }

    public void Reset()
    {
        seenOpen = false;
        AwaitingClosure = false;
    }
}
