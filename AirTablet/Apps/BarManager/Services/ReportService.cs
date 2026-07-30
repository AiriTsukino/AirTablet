using System.Text;
using BarManager.Models;

namespace BarManager.Services;

internal static class ReportService
{
    public static string BuildNightlyReport(Configuration config)
    {
        var venue = config.ActiveVenue;
        var audit = config.CurrentAudit;
        var sb = new StringBuilder();
        sb.AppendLine($"BarManager Nightly Audit Report - {venue.Name}");
        sb.AppendLine($"Date: {audit.BusinessDate:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(audit.BartenderName)) sb.AppendLine($"Bartender: {audit.BartenderName}");
        sb.AppendLine();
        sb.AppendLine($"Starting gil: {(audit.MyStartingGil + audit.VenuePrizeGil):N0}");
        sb.AppendLine($"  Personal starting gil: {audit.MyStartingGil:N0}");
        sb.AppendLine($"  Venue prize gil: {audit.VenuePrizeGil:N0}");
        sb.AppendLine();
        sb.AppendLine("Drinks sold:");
        foreach (var drink in venue.Drinks.Where(d => d.Enabled))
        {
            var sale = audit.DrinkSales.FirstOrDefault(s => s.DrinkId == drink.Id);
            var count = sale?.Count ?? 0;
            var covered = sale?.CountCoveredByBuyout ?? 0;
            var billable = BillableDrinkCount(audit, sale, drink);
            var coveredText = drink.IsGambaDrink ? "0" : covered.ToString("N0");
            sb.AppendLine($"  {drink.Name}: {count:N0} sold, {coveredText} covered by buyout, {billable:N0} billable x {drink.Price:N0} = {(billable * drink.Price):N0}");
        }
        sb.AppendLine($"Drink sales: {DrinkSales(venue, audit):N0}");
        sb.AppendLine($"Buyout sales: {BuyoutSales(venue, audit):N0}");
        if (audit.SubmittedBuyouts.Count > 0)
        {
            sb.AppendLine("Submitted buyouts:");
            foreach (var buyout in audit.SubmittedBuyouts)
            {
                var buyer = string.IsNullOrWhiteSpace(buyout.Buyer) ? "Unknown buyer" : buyout.Buyer;
                sb.AppendLine($"  {buyout.SubmittedAt:HH:mm} - {buyer} - {buyout.DisplayType} - {buyout.Total:N0}");
            }
        }
        sb.AppendLine($"Tips: {audit.Tips:N0}");
        sb.AppendLine($"Prizes paid: {audit.PrizesPaidOut:N0}");
        sb.AppendLine($"Total gil in: {(DrinkSales(venue, audit) + BuyoutSales(venue, audit) + audit.Tips):N0}");
        sb.AppendLine($"Total gil out: {audit.PrizesPaidOut:N0}");
        sb.AppendLine($"Current jackpot: {audit.JackpotCurrent:N0}");
        sb.AppendLine();
        sb.AppendLine($"Ending gil entered: {audit.EndingGilEntered:N0}");
        sb.AppendLine($"Nightly profit/loss excluding tips and starting gil: {NightlyProfitLoss(audit):N0}");
        return sb.ToString();
    }

    public static string BuildGambaReport(Configuration config)
    {
        var venue = config.ActiveVenue;
        var audit = config.CurrentAudit;
        var sb = new StringBuilder();
        sb.AppendLine($"BarManager Gamba Report - {venue.Name}");
        sb.AppendLine($"Date: {audit.BusinessDate:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(audit.BartenderName)) sb.AppendLine($"Bartender: {audit.BartenderName}");
        sb.AppendLine();
        sb.AppendLine($"Sessions: {audit.GambaSessions.Count:N0}");
        sb.AppendLine($"Total rolls: {audit.GambaSessions.Sum(s => s.Rolls.Count):N0}/{audit.GambaSessions.Sum(s => s.RollsAllowed):N0}");
        sb.AppendLine($"Total payout: {audit.GambaSessions.Sum(s => s.TotalPayout):N0}");
        sb.AppendLine($"Jackpot contributions: {audit.GambaSessions.Sum(s => s.TotalJackpotContributions):N0}");
        sb.AppendLine($"Jackpot wins: {audit.GambaSessions.Count(s => s.JackpotWon):N0}");
        sb.AppendLine();
        sb.AppendLine("Gamba sessions:");
        if (audit.GambaSessions.Count == 0)
        {
            sb.AppendLine("  None");
        }
        else
        {
            foreach (var session in audit.GambaSessions)
            {
                var ended = session.EndedAt is null ? "live" : session.EndedAt.Value.ToString("HH:mm");
                sb.AppendLine($"  {session.StartedAt:HH:mm}-{ended} {session.CustomerDisplay} - {session.DrinksPurchased} {venue.Gamba.DrinkName}(s), {session.Rolls.Count:N0}/{session.RollsAllowed:N0} rolls, payout {session.TotalPayout:N0}, jackpot +{session.TotalJackpotContributions:N0}");
                foreach (var roll in session.Rolls)
                {
                    var bonus = string.IsNullOrWhiteSpace(roll.BonusName) || roll.BonusMultiplier <= 1f ? string.Empty : $", {roll.BonusName} x{roll.BonusMultiplier:0.##} (base {roll.BasePayout:N0})";
                    sb.AppendLine($"    {roll.Roll} -> {roll.Tier} -> {roll.Payout:N0} (jackpot +{roll.JackpotContribution:N0}{bonus})");
                }
            }
        }
        return sb.ToString();
    }

    public static int DrinkSales(VenueProfile venue, BarAuditState audit)
    {
        var total = 0;
        foreach (var drink in venue.Drinks.Where(d => d.Enabled))
        {
            var sale = audit.DrinkSales.FirstOrDefault(s => s.DrinkId == drink.Id);
            if (sale is null) continue;
            total += BillableDrinkCount(audit, sale, drink) * drink.Price;
        }
        return total;
    }

    public static int BillableDrinkCount(BarAuditState audit, DrinkSale? sale, DrinkDefinition? drink = null)
    {
        if (sale is null) return 0;

        // Gamba drinks are paid roll purchases, so they should remain billable even during a bar buyout.
        if (drink?.IsGambaDrink == true)
            return Math.Max(0, sale.Count);

        var activeCovered = audit.BarBuyoutActive ? Math.Max(0, sale.Count - sale.CountBeforeBuyout) : 0;
        return Math.Max(0, sale.Count - sale.CountCoveredByBuyout - activeCovered);
    }

    public static int NightlyProfitLoss(BarAuditState audit) => audit.EndingGilEntered - (audit.MyStartingGil + audit.VenuePrizeGil) - audit.Tips;

    public static int BuyoutSales(VenueProfile venue, BarAuditState audit)
    {
        var submitted = audit.SubmittedBuyouts.Sum(b => b.Total);
        return submitted + CurrentBuyoutValue(venue, audit);
    }

    public static int CurrentBuyoutValue(VenueProfile venue, BarAuditState audit)
    {
        if (!audit.BarBuyoutActive) return 0;
        return audit.BarBuyoutType switch
        {
            "full" => venue.FullBuyoutPrice,
            "hourly" => (int)MathF.Round(audit.BarBuyoutHours * venue.HourlyBuyoutPrice),
            "custom" => audit.BarBuyoutCustomPrice,
            _ => 0,
        };
    }
}
