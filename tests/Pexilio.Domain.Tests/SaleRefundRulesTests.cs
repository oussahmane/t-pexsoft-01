using Pexilio.Domain;
using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Tests;

public sealed class SaleRefundRulesTests
{
    [Fact]
    public void An_unpaid_sale_refunds_nothing_and_reduces_the_debt_by_the_return()
    {
        var quote = Quote(returnAmount: 4.0000m, outstanding: 10.0000m, collected: 0m);

        Assert.Equal(0m, quote.RequiredRefund);
        Assert.Equal(4.0000m, quote.NetAccountReduction);
    }

    [Fact]
    public void A_partial_return_inside_the_outstanding_debt_does_not_require_a_refund()
    {
        var quote = Quote(returnAmount: 4.0000m, outstanding: 6.0000m, collected: 4.0000m);

        Assert.Equal(0m, quote.RequiredRefund);
        Assert.Equal(4.0000m, quote.NetAccountReduction);
        Assert.True(quote.NetAccountReduction <= quote.OutstandingBefore);
    }

    [Fact]
    public void A_return_above_the_outstanding_debt_refunds_only_the_excess()
    {
        var quote = Quote(returnAmount: 6.0000m, outstanding: 4.0000m, collected: 6.0000m);

        Assert.Equal(2.0000m, quote.RequiredRefund);
        Assert.Equal(4.0000m, quote.NetAccountReduction);
    }

    [Fact]
    public void A_fully_paid_sale_refunds_the_return_and_does_not_reduce_debt()
    {
        var quote = Quote(returnAmount: 6.0000m, outstanding: 0m, collected: 10.0000m);

        Assert.Equal(6.0000m, quote.RequiredRefund);
        Assert.Equal(0m, quote.NetAccountReduction);
    }

    [Fact]
    public void A_walk_in_return_is_a_fully_paid_return()
    {
        var quote = Quote(returnAmount: 10.0000m, outstanding: 0m, collected: 10.0000m);

        Assert.Equal(10.0000m, quote.RequiredRefund);
        Assert.Equal(0m, quote.NetAccountReduction);
    }

    [Fact]
    public void The_required_refund_is_exact()
    {
        var cases = new (decimal ReturnAmount, decimal Outstanding, decimal Collected)[]
        {
            (0m, 10.0000m, 0m),
            (4.0000m, 10.0000m, 0m),
            (10.0000m, 10.0000m, 0m),
            (4.0000m, 4.0000m, 6.0000m),
            (6.0000m, 4.0000m, 6.0000m),
            (6.0000m, 0m, 6.0000m),
            (0.0001m, 0m, 0.0001m),
        };

        foreach (var item in cases)
        {
            var quote = Quote(item.ReturnAmount, item.Outstanding, item.Collected);
            var expected = item.ReturnAmount > item.Outstanding ? item.ReturnAmount - item.Outstanding : 0m;

            Assert.Equal(expected, quote.RequiredRefund);
            Assert.Equal(item.ReturnAmount - expected, quote.NetAccountReduction);
            Assert.Equal(item.ReturnAmount, quote.RequiredRefund + quote.NetAccountReduction);
            Assert.InRange(quote.NetAccountReduction, 0m, item.Outstanding);
        }
    }

    [Fact]
    public void A_refund_cannot_exceed_money_collected_and_not_yet_refunded()
    {
        var saved = SaleRefundRules.TryQuote(6.0000m, 0m, 5.9999m, out _, out var errors);

        Assert.False(saved);
        Assert.Contains(new FieldError("refund", "refund.exceeds"), errors);
    }

    [Fact]
    public void A_zero_return_amount_has_a_zero_refund_and_zero_debt_reduction()
    {
        var quote = Quote(0m, 8.0000m, 2.0000m);

        Assert.Equal(0m, quote.RequiredRefund);
        Assert.Equal(0m, quote.NetAccountReduction);
    }

    [Theory]
    [InlineData("-1.0000", "0.0000", "0.0000", "returnAmount", "amount.sign")]
    [InlineData("1.0000", "-0.0001", "1.0000", "outstanding", "amount.sign")]
    [InlineData("1.0000", "0.0000", "-1.0000", "collected", "amount.sign")]
    [InlineData("1.00001", "0.0000", "1.0000", "returnAmount", "amount.scale")]
    [InlineData("1.0000", "0.00001", "1.0000", "outstanding", "amount.scale")]
    [InlineData("1.0000", "0.0000", "1.00001", "collected", "amount.scale")]
    public void Negative_or_imprecise_financial_inputs_are_rejected(
        string returnAmount,
        string outstanding,
        string collected,
        string field,
        string code)
    {
        var saved = SaleRefundRules.TryQuote(
            decimal.Parse(returnAmount, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(outstanding, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(collected, System.Globalization.CultureInfo.InvariantCulture),
            out _,
            out var errors);

        Assert.False(saved);
        Assert.Contains(new FieldError(field, code), errors);
    }

    [Fact]
    public void Successive_returns_keep_the_refund_exact_against_the_reduced_debt()
    {
        var first = Quote(6.0000m, 4.0000m, 6.0000m);
        var outstanding = 4.0000m - first.NetAccountReduction;
        var collected = 6.0000m - first.RequiredRefund;
        var second = Quote(4.0000m, outstanding, collected);

        Assert.Equal(2.0000m, first.RequiredRefund);
        Assert.Equal(0m, outstanding);
        Assert.Equal(4.0000m, collected);
        Assert.Equal(4.0000m, second.RequiredRefund);
        Assert.Equal(0m, second.NetAccountReduction);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("transfer")]
    [InlineData("card")]
    public void One_allowed_tender_can_settle_the_exact_refund(string method)
    {
        var saved = SaleRefundRules.TryTenders(6.0000m, [(method, 6.0000m)], out var tenders, out var errors);

        Assert.True(saved);
        Assert.Empty(errors);
        var tender = Assert.Single(tenders);
        Assert.Equal(method, tender.Method);
        Assert.Equal(6.0000m, tender.Amount);
    }

    [Fact]
    public void A_refund_can_be_split_across_cash_transfer_and_card()
    {
        var saved = SaleRefundRules.TryTenders(
            6.0000m,
            [(" cash ", 2.5000m), ("TRANSFER", 2.5000m), ("Card", 1.0000m)],
            out var tenders,
            out var errors);

        Assert.True(saved);
        Assert.Empty(errors);
        Assert.Equal(["cash", "transfer", "card"], tenders.Select(tender => tender.Method).ToArray());
        Assert.Equal(6.0000m, tenders.Sum(tender => tender.Amount));
    }

    [Fact]
    public void A_tender_sum_below_or_above_the_required_refund_is_rejected()
    {
        var under = RejectTenders(6.0000m, [("cash", 2.0000m), ("card", 3.9999m)]);
        var over = RejectTenders(6.0000m, [("cash", 2.0000m), ("card", 4.0001m)]);

        Assert.Contains(new FieldError("refunds", "refund.under"), under);
        Assert.Contains(new FieldError("refunds", "refund.over"), over);
    }

    [Fact]
    public void Zero_and_negative_tenders_are_rejected()
    {
        var zero = RejectTenders(1.0000m, [("cash", 0m)]);
        var negative = RejectTenders(1.0000m, [("cash", -1.0000m)]);

        Assert.Contains(new FieldError("refunds[0].amount", "refund.positive"), zero);
        Assert.Contains(new FieldError("refunds[0].amount", "refund.sign"), negative);
    }

    [Fact]
    public void Other_and_unknown_methods_are_not_refund_methods()
    {
        var other = RejectTenders(1.0000m, [("other", 1.0000m)]);
        var blank = RejectTenders(1.0000m, [(" ", 1.0000m)]);

        Assert.Contains(new FieldError("refunds[0].method", "refund.method"), other);
        Assert.Contains(new FieldError("refunds[0].method", "refund.method"), blank);
    }

    [Fact]
    public void Excess_tender_precision_is_rejected()
    {
        var errors = RejectTenders(1.0000m, [("cash", 1.00001m)]);

        Assert.Contains(new FieldError("refunds[0].amount", "refund.scale"), errors);
    }

    [Fact]
    public void No_tender_is_accepted_when_the_required_refund_is_zero()
    {
        var empty = SaleRefundRules.TryTenders(0m, [], out var none, out var emptyErrors);
        var supplied = RejectTenders(0m, [("cash", 0.0001m)]);

        Assert.True(empty);
        Assert.Empty(emptyErrors);
        Assert.Empty(none);
        Assert.Contains(new FieldError("refunds", "refund.unexpected"), supplied);
    }

    [Fact]
    public void A_missing_tender_list_underpays_a_positive_refund()
    {
        var saved = SaleRefundRules.TryTenders(0.0001m, null, out _, out var errors);

        Assert.False(saved);
        Assert.Contains(new FieldError("refunds", "refund.under"), errors);
    }

    [Fact]
    public void The_same_refund_method_cannot_be_entered_twice()
    {
        var errors = RejectTenders(6.0000m, [("cash", 2.0000m), ("cash", 4.0000m)]);

        Assert.Contains(new FieldError("refunds[1].method", "refunds.duplicate"), errors);
    }

    private static SaleRefundQuote Quote(decimal returnAmount, decimal outstanding, decimal collected)
    {
        var saved = SaleRefundRules.TryQuote(returnAmount, outstanding, collected, out var quote, out var errors);
        Assert.True(saved);
        Assert.Empty(errors);
        return quote;
    }

    private static IReadOnlyList<FieldError> RejectTenders(
        decimal required,
        (string? Method, decimal Amount)[] tenders)
    {
        var saved = SaleRefundRules.TryTenders(required, tenders, out _, out var errors);
        Assert.False(saved);
        return errors;
    }
}
