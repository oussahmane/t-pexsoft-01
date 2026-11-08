using Pexilio.Domain.Customers;

namespace Pexilio.Domain.Tests;

public sealed class CustomerAccountMathTests
{
    [Fact]
    public void Zero_refunds_leave_outstanding_as_sales_minus_returns_minus_payments()
    {
        var saved = CustomerAccountMath.TryOutstanding(30.0000m, 5.0000m, 10.0000m, 0m, out var outstanding);

        Assert.True(saved);
        Assert.Equal(15.0000m, outstanding);
    }

    [Fact]
    public void Refunds_increase_outstanding_by_the_money_returned_to_the_customer()
    {
        var saved = CustomerAccountMath.TryOutstanding(10.0000m, 6.0000m, 10.0000m, 6.0000m, out var outstanding);

        Assert.True(saved);
        Assert.Equal(0m, outstanding);
    }

    [Fact]
    public void A_refund_above_collected_payments_is_rejected()
    {
        Assert.False(CustomerAccountMath.TryOutstanding(10.0000m, 4.0000m, 3.0000m, 3.0001m, out _));
    }

    [Fact]
    public void Net_payments_above_the_net_sales_are_rejected()
    {
        Assert.False(CustomerAccountMath.TryOutstanding(10.0000m, 6.0000m, 10.0000m, 5.0000m, out _));
    }
}
