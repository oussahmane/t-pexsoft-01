using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Tests;

public sealed class SaleBalanceTests
{
    [Fact]
    public void Zero_refunds_keep_the_existing_balance()
    {
        Assert.True(SaleBalances.TryDerive(10.0000m, 4.0000m, 3.0000m, 0m, out var balance));
        Assert.True(SaleBalances.TryDerive(10.0000m, 4.0000m, 3.0000m, out var previous));

        Assert.Equal(previous, balance);
        Assert.Equal(6.0000m, balance.Net);
        Assert.Equal(3.0000m, balance.Paid);
        Assert.Equal(3.0000m, balance.Outstanding);
        Assert.Equal(SalePaymentStates.Partial, balance.State);
    }

    [Fact]
    public void A_partial_refund_reduces_net_collections_and_leaves_the_remaining_debt()
    {
        Assert.True(SaleBalances.TryDerive(10.0000m, 4.0000m, 6.0000m, 2.0000m, out var balance));

        Assert.Equal(6.0000m, balance.Net);
        Assert.Equal(6.0000m, balance.Paid);
        Assert.Equal(2.0000m, balance.Outstanding);
        Assert.Equal(SalePaymentStates.Partial, balance.State);
    }

    [Fact]
    public void A_full_refund_of_a_returned_sale_settles_at_zero()
    {
        Assert.True(SaleBalances.TryDerive(10.0000m, 10.0000m, 10.0000m, 10.0000m, out var balance));

        Assert.Equal(0m, balance.Net);
        Assert.Equal(10.0000m, balance.Paid);
        Assert.Equal(0m, balance.Outstanding);
        Assert.Equal(SalePaymentStates.Paid, balance.State);
    }

    [Fact]
    public void A_refund_above_the_collected_payments_is_rejected()
    {
        Assert.False(SaleBalances.TryDerive(10.0000m, 6.0000m, 4.0000m, 4.0001m, out _));
    }

    [Fact]
    public void Net_payments_above_the_sale_net_are_rejected()
    {
        Assert.False(SaleBalances.TryDerive(10.0000m, 6.0000m, 10.0000m, 5.0000m, out _));
    }

    [Theory]
    [InlineData("-0.0001", "0.0000", "0.0000", "0.0000")]
    [InlineData("10.0000", "-0.0001", "0.0000", "0.0000")]
    [InlineData("10.0000", "0.0000", "-0.0001", "0.0000")]
    [InlineData("10.0000", "0.0000", "1.0000", "-0.0001")]
    [InlineData("10.0000", "10.0001", "0.0000", "0.0000")]
    [InlineData("10.00001", "0.0000", "0.0000", "0.0000")]
    public void Negative_off_scale_or_excessive_amounts_are_rejected(
        string total,
        string returned,
        string paid,
        string refunded)
    {
        Assert.False(SaleBalances.TryDerive(Money(total), Money(returned), Money(paid), Money(refunded), out _));
    }

    private static decimal Money(string text) =>
        decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
}
