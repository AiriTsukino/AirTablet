namespace PrizeTrader;

// One tracker is armed only for a submitted final confirmation. A closed window
// is corroborating evidence, never proof of payment on its own.
internal sealed class TradeCompletionTracker
{
    private long expectedAmount;
    private long? startingBalance;
    private DateTimeOffset? confirmationClosedAt;
    private bool balanceMatches;
    private bool balanceConfirmationDisabled;
    public long ReportedAmount { get; private set; }
    public bool SawCompletionMessage { get; private set; }
    public bool HasUnexpectedBalanceChange { get; private set; }
    public bool HasFailure { get; private set; }
    public bool BalanceCheckExpired { get; private set; }
    public bool HasBalanceConfirmation => balanceMatches && !balanceConfirmationDisabled;
    public bool NeedsBalanceObservation => expectedAmount > 0 && startingBalance is not null
        && !balanceConfirmationDisabled && !balanceMatches;
    public long? ObservedDecrease { get; private set; }

    public void Begin(long amount, long? balance = null)
    {
        Reset();
        expectedAmount = amount;
        startingBalance = balance;
    }

    public bool ObserveHandover(long amount)
    {
        if (expectedAmount <= 0) return true;
        if (amount != expectedAmount)
        {
            HasFailure = true;
            return false;
        }
        ReportedAmount = amount;
        return true;
    }

    public void ObserveCompletion()
    {
        if (expectedAmount > 0) SawCompletionMessage = true;
    }

    public void ObserveFailure() => HasFailure = true;

    public void ObserveBalance(long? balance, bool? finalConfirmationVisible, DateTimeOffset now)
    {
        if (!NeedsBalanceObservation) return;
        if (finalConfirmationVisible == false) confirmationClosedAt ??= now;
        // Check briefly after the secondary Yes/No closes, not after the main
        // Trade window closes. A match is latched, ending further balance reads.
        if (confirmationClosedAt is not null && now - confirmationClosedAt.Value > TimeSpan.FromSeconds(3))
        {
            BalanceCheckExpired = true;
            balanceConfirmationDisabled = true;
            return;
        }
        if (balance is null) return;
        ObservedDecrease = startingBalance!.Value - balance.Value;
        if (ObservedDecrease == expectedAmount)
            balanceMatches = true;
        else if (ObservedDecrease != 0)
        {
            HasUnexpectedBalanceChange = true;
            balanceConfirmationDisabled = true;
        }
    }

    public bool TryConsume(bool? tradeWindowVisible, out long amount)
    {
        amount = 0;
        // Unknown UI state (including lookup errors) must not mean closed.
        if (tradeWindowVisible != false || expectedAmount <= 0 || HasFailure || HasUnexpectedBalanceChange ||
            (ReportedAmount != expectedAmount && !HasBalanceConfirmation))
            return false;
        amount = expectedAmount;
        Reset();
        return true;
    }

    public void Reset()
    {
        expectedAmount = 0;
        startingBalance = null;
        confirmationClosedAt = null;
        balanceMatches = false;
        balanceConfirmationDisabled = false;
        ReportedAmount = 0;
        SawCompletionMessage = false;
        HasUnexpectedBalanceChange = false;
        HasFailure = false;
        BalanceCheckExpired = false;
        ObservedDecrease = null;
    }
}
