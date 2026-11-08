using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Customers;

public static class CustomerAccountMath
{
    public static bool TryOutstanding(
        decimal sales,
        decimal returns,
        decimal payments,
        decimal refunds,
        out decimal outstanding)
    {
        outstanding = 0m;
        if (!SaleRules.ExactlyScale4(sales)
            || !SaleRules.ExactlyScale4(returns)
            || !SaleRules.ExactlyScale4(payments)
            || !SaleRules.ExactlyScale4(refunds)
            || sales < 0m
            || returns < 0m
            || payments < 0m
            || refunds < 0m
            || returns > sales
            || refunds > payments)
        {
            return false;
        }

        var netSales = sales - returns;
        var netPayments = payments - refunds;
        if (netPayments > netSales)
        {
            return false;
        }

        outstanding = netSales - netPayments;
        return SaleRules.ExactlyScale4(outstanding);
    }
}
