using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Tests;

public sealed class SaleLineReturnHistoryTests
{
    [Fact]
    public void No_returns_leave_the_full_quantity_and_returnable_value()
    {
        var history = SaleLineReturnHistoryRules.Summarize(3.0000m, 30.0000m, 0m, 0m, 0m);

        Assert.Equal(0m, history.ReturnedQuantity);
        Assert.Equal(3.0000m, history.RemainingReturnableQuantity);
        Assert.Equal(0m, history.ReturnedAmount);
        Assert.Equal(30.0000m, history.RemainingReturnableAmount);
    }

    [Fact]
    public void A_partial_return_reduces_quantity_and_amount()
    {
        var history = SaleLineReturnHistoryRules.Summarize(3.0000m, 30.0000m, 0m, 1.0000m, 10.0000m);

        Assert.Equal(1.0000m, history.ReturnedQuantity);
        Assert.Equal(2.0000m, history.RemainingReturnableQuantity);
        Assert.Equal(10.0000m, history.ReturnedAmount);
        Assert.Equal(20.0000m, history.RemainingReturnableAmount);
    }

    [Fact]
    public void Several_partial_returns_are_summarized_from_their_cumulative_totals()
    {
        var history = SaleLineReturnHistoryRules.Summarize(3.0000m, 29.9999m, 0.0001m, 2.0000m, 19.9999m);

        Assert.Equal(2.0000m, history.ReturnedQuantity);
        Assert.Equal(1.0000m, history.RemainingReturnableQuantity);
        Assert.Equal(19.9999m, history.ReturnedAmount);
        Assert.Equal(9.9999m, history.RemainingReturnableAmount);
    }

    [Fact]
    public void A_fully_returned_line_has_nothing_left()
    {
        var history = SaleLineReturnHistoryRules.Summarize(2.0000m, 20.0000m, 0m, 2.0000m, 20.0000m);

        Assert.Equal(0m, history.RemainingReturnableQuantity);
        Assert.Equal(0m, history.RemainingReturnableAmount);
    }

    [Fact]
    public void A_fully_discounted_line_has_no_returnable_value()
    {
        var history = SaleLineReturnHistoryRules.Summarize(1.0000m, 0.0000m, 0m, 1.0000m, 0.0000m);

        Assert.Equal(0m, history.ReturnedAmount);
        Assert.Equal(0m, history.RemainingReturnableQuantity);
        Assert.Equal(0m, history.RemainingReturnableAmount);
    }

    [Fact]
    public void The_returnable_value_excludes_the_allocated_sale_discount()
    {
        var history = SaleLineReturnHistoryRules.Summarize(1.0000m, 10.0000m, 10.0000m, 0m, 0m);

        Assert.Equal(0m, history.RemainingReturnableAmount);
    }

    [Fact]
    public void Four_decimal_quantities_and_amounts_are_preserved()
    {
        var history = SaleLineReturnHistoryRules.Summarize(1.0000m, 0.0003m, 0m, 0.5000m, 0.0002m);

        Assert.Equal(0.5000m, history.ReturnedQuantity);
        Assert.Equal(0.5000m, history.RemainingReturnableQuantity);
        Assert.Equal(0.0002m, history.ReturnedAmount);
        Assert.Equal(0.0001m, history.RemainingReturnableAmount);
    }
}
