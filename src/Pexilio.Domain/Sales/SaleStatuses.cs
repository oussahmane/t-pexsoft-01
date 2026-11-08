namespace Pexilio.Domain.Sales;

public static class SaleStatuses
{
    public const string Posted = "posted";
}

public static class SaleOperations
{
    public const string SaleFinalize = "sale.finalize";

    public const string SalePayment = "sale.payment";

    public const string SaleReturn = "sale.return";
}

public static class SalePaymentStates
{
    public const string Unpaid = "unpaid";

    public const string Partial = "partial";

    public const string Paid = "paid";
}
