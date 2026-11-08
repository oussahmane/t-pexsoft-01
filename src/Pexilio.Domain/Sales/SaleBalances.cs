namespace Pexilio.Domain.Sales;

public readonly record struct SaleBalance(decimal Net, decimal Paid, decimal Outstanding, string State);

public static class SaleBalances
{
    public static bool TryDerive(decimal total, decimal returned, decimal paid, out SaleBalance balance) =>
        TryDerive(total, returned, paid, 0m, out balance);

    public static bool TryDerive(
        decimal total,
        decimal returned,
        decimal paid,
        decimal refunded,
        out SaleBalance balance)
    {
        balance = default;
        if (!SaleRules.ExactlyScale4(total)
            || !SaleRules.ExactlyScale4(returned)
            || !SaleRules.ExactlyScale4(paid)
            || !SaleRules.ExactlyScale4(refunded)
            || total < 0m
            || returned < 0m
            || paid < 0m
            || refunded < 0m
            || returned > total
            || refunded > paid)
        {
            return false;
        }

        var net = total - returned;
        var netPaid = paid - refunded;
        var outstanding = net - netPaid;
        if (outstanding < 0m || !SaleRules.ExactlyScale4(outstanding))
        {
            return false;
        }

        var state = outstanding == 0m
            ? SalePaymentStates.Paid
            : netPaid == 0m
                ? SalePaymentStates.Unpaid
                : SalePaymentStates.Partial;
        balance = new SaleBalance(net, paid, outstanding, state);
        return true;
    }
}
