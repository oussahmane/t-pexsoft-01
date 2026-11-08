using System.Globalization;
using Pexilio.Domain;
using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Customers;

public readonly record struct CustomerStatementWindow(
    DateOnly From,
    DateOnly To,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public readonly record struct CustomerStatementMovement(
    DateTimeOffset OccurredAt,
    string Kind,
    Guid DocumentId,
    decimal SignedAmount);

public static class CustomerStatementWindowRules
{
    public static bool Includes(DateTimeOffset instant, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        instant >= fromUtc && instant < toUtc;

    public static bool TryResolve(
        string? fromText,
        string? toText,
        string timeZoneId,
        DateTimeOffset utcNow,
        out CustomerStatementWindow window,
        out FieldError? error)
    {
        window = default;
        var fromBlank = string.IsNullOrWhiteSpace(fromText);
        var toBlank = string.IsNullOrWhiteSpace(toText);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
        {
            error = new FieldError("timeZoneId", "timeZoneId.invalid");
            return false;
        }

        DateOnly from;
        DateOnly to;
        if (fromBlank && toBlank)
        {
            var localNow = TimeZoneInfo.ConvertTime(utcNow, zone);
            from = new DateOnly(localNow.Year, localNow.Month, 1);
            to = from.AddMonths(1);
        }
        else if (fromBlank || !TryDate(fromText, out from))
        {
            error = new FieldError("from", "statement.date");
            return false;
        }
        else if (toBlank || !TryDate(toText, out to))
        {
            error = new FieldError("to", "statement.date");
            return false;
        }
        else if (from > to)
        {
            error = new FieldError("to", "statement.range");
            return false;
        }

        if (!TryMidnight(from, zone, out var fromUtc) || !TryMidnight(to, zone, out var toUtc))
        {
            error = new FieldError("from", "statement.date");
            return false;
        }

        window = new CustomerStatementWindow(from, to, fromUtc, toUtc);
        error = null;
        return true;
    }

    private static bool TryDate(string? text, out DateOnly date)
    {
        var trimmed = text?.Trim() ?? "";
        return DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && trimmed == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool TryMidnight(DateOnly date, TimeZoneInfo zone, out DateTimeOffset utc)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }

        try
        {
            utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            return true;
        }
        catch (ArgumentException)
        {
            utc = default;
            return false;
        }
    }
}

public static class CustomerStatementMath
{
    public const string SaleKind = "sale";

    public const string ReturnKind = "return";

    public const string PaymentKind = "payment";

    public static int KindRank(string kind) => kind switch
    {
        SaleKind => 0,
        ReturnKind => 1,
        PaymentKind => 2,
        _ => 9,
    };

    public static bool TryRefundTotal(IReadOnlyList<decimal>? tenders, out decimal refundAmount)
    {
        refundAmount = 0m;
        if (tenders is null || tenders.Count == 0)
        {
            return true;
        }

        decimal sum = 0m;
        foreach (var tender in tenders)
        {
            if (tender <= 0m || !SaleRules.ExactlyScale4(tender))
            {
                return false;
            }

            sum += tender;
        }

        if (!SaleRules.ExactlyScale4(sum))
        {
            return false;
        }

        refundAmount = sum;
        return true;
    }

    public static bool TryNetReturn(
        decimal returnAmount,
        decimal refundAmount,
        out decimal netAccountReduction,
        out decimal signedAmount)
    {
        netAccountReduction = 0m;
        signedAmount = 0m;
        if (returnAmount < 0m
            || refundAmount < 0m
            || refundAmount > returnAmount
            || !SaleRules.ExactlyScale4(returnAmount)
            || !SaleRules.ExactlyScale4(refundAmount))
        {
            return false;
        }

        netAccountReduction = returnAmount - refundAmount;
        signedAmount = -netAccountReduction;
        return SaleRules.ExactlyScale4(netAccountReduction);
    }

    public static bool TryProject(
        decimal opening,
        IReadOnlyList<CustomerStatementMovement> period,
        out decimal closing,
        out IReadOnlyList<decimal> running)
    {
        if (opening < 0m)
        {
            closing = 0m;
            running = [];
            return false;
        }

        var ordered = period
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => KindRank(item.Kind))
            .ThenBy(item => item.DocumentId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var balances = new decimal[ordered.Length];
        var cursor = opening;
        for (var index = 0; index < ordered.Length; index++)
        {
            cursor += ordered[index].SignedAmount;
            balances[index] = cursor;
        }

        closing = cursor;
        running = balances;
        return closing >= 0m;
    }
}
