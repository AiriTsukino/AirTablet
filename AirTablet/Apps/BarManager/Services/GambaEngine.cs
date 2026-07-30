using BarManager.Models;

namespace BarManager.Services;

internal static class GambaEngine
{
    public static RollResolution Resolve(int roll, int jackpotValue, GambaSettings settings)
    {
        foreach (var rule in settings.Rules.Where(r => r.Enabled))
        {
            if (!Matches(roll, rule))
                continue;

            var payout = rule.PaysJackpot ? jackpotValue : Math.Max(0, rule.Payout);
            return new RollResolution(rule.Tier, payout, rule.PaysJackpot, rule.GrantsFreeRoll);
        }

        return new RollResolution("NONE", 0, false, false);
    }

    private static bool Matches(int roll, GambaRule rule)
    {
        var s = roll.ToString();
        var matched = false;

        if (rule.EqualTo.HasValue)
            matched |= rule.ExactOnly ? roll == rule.EqualTo.Value : s.Contains(rule.EqualTo.Value.ToString(), StringComparison.Ordinal);
        if (rule.InValues.Count > 0)
            matched |= rule.ExactOnly
                ? rule.InValues.Contains(roll)
                : rule.InValues.Any(v => s.Contains(v.ToString(), StringComparison.Ordinal));
        if (rule.ContainsTokens.Count > 0)
            matched |= rule.ContainsTokens.Any(t => !string.IsNullOrWhiteSpace(t) && s.Contains(t.Trim(), StringComparison.OrdinalIgnoreCase));
        if (rule.ContainsAnyDigits.Count > 0)
            matched |= rule.ContainsAnyDigits.Any(d => s.Contains(Math.Abs(d % 10).ToString(), StringComparison.Ordinal));

        // These pattern checks intentionally work even when Winning roll(s) is empty.
        // That lets venues configure rules such as "Any triple" or "Adjacent doubles"
        // without manually listing every possible winning value.
        if (rule.Triples)
            matched |= s.Length == 3 && s.Distinct().Count() == 1;
        if (rule.AdjacentDoubles)
            matched |= s.Length >= 2 && Enumerable.Range(0, s.Length - 1).Any(i => s[i] == s[i + 1]);

        return matched;
    }
}
