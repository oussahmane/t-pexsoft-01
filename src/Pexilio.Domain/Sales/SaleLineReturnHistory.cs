namespace Pexilio.Domain.Sales;

public readonly record struct SaleLineReturnTotals(decimal Quantity, decimal Amount);

public readonly record struct SaleLineReturnHistory(
    decimal ReturnedQuantity,
    decimal RemainingReturnableQuantity,
    decimal ReturnedAmount,
    decimal RemainingReturnableAmount);

public static class SaleLineReturnHistoryRules
{
    public static SaleLineReturnHistory Summarize(
        decimal soldQuantity,
        decimal lineTotal,
        decimal allocatedSaleDiscount,
        decimal returnedQuantity,
        decimal returnedAmount)
    {
        var returnable = lineTotal - allocatedSaleDiscount;
        return new SaleLineReturnHistory(
            returnedQuantity,
            soldQuantity - returnedQuantity,
            returnedAmount,
            returnable - returnedAmount);
    }
}
