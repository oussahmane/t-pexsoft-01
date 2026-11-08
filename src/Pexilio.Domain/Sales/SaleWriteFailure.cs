using Pexilio.Domain;

namespace Pexilio.Domain.Sales;

public static class SaleWriteFailure
{
    public const string UniqueViolation = "23505";

    public static bool TryMap(string? message, string? sqlState, string? constraintName, out string code)
    {
        if (Contains(message, ErrorCodes.SaleMovementIntegrity))
        {
            code = ErrorCodes.SaleMovementIntegrity;
            return true;
        }

        if (Contains(message, ErrorCodes.SaleReturnMovementIntegrity))
        {
            code = ErrorCodes.SaleReturnMovementIntegrity;
            return true;
        }

        if (Contains(message, ErrorCodes.SalePaymentIntegrity))
        {
            code = ErrorCodes.SalePaymentIntegrity;
            return true;
        }

        if (Contains(message, ErrorCodes.SaleCreditOverrideIntegrity))
        {
            code = ErrorCodes.SaleCreditOverrideIntegrity;
            return true;
        }

        if (Contains(message, ErrorCodes.SaleTotalIntegrity))
        {
            code = ErrorCodes.SaleTotalIntegrity;
            return true;
        }

        if (Contains(message, ErrorCodes.SaleImmutable))
        {
            code = ErrorCodes.SaleImmutable;
            return true;
        }

        if (Contains(message, ErrorCodes.StockReconciliation))
        {
            code = ErrorCodes.StockReconciliation;
            return true;
        }

        if (Contains(message, ErrorCodes.StockInsufficient))
        {
            code = ErrorCodes.StockInsufficient;
            return true;
        }

        if (sqlState == UniqueViolation
            && constraintName?.Contains("idempotency", StringComparison.OrdinalIgnoreCase) == true)
        {
            code = ErrorCodes.SaleIdempotencyConflict;
            return true;
        }

        code = "";
        return false;
    }

    private static bool Contains(string? message, string token) =>
        message?.Contains(token, StringComparison.Ordinal) == true;
}
