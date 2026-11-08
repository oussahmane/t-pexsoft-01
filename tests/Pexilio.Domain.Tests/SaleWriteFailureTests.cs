using Pexilio.Domain;
using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Tests;

public sealed class SaleWriteFailureTests
{
    [Fact]
    public void Known_integrity_failures_keep_their_business_codes()
    {
        Assert.Equal(ErrorCodes.SaleMovementIntegrity, Mapped(ErrorCodes.SaleMovementIntegrity));
        Assert.Equal(ErrorCodes.SaleReturnMovementIntegrity, Mapped(ErrorCodes.SaleReturnMovementIntegrity));
        Assert.Equal(ErrorCodes.SalePaymentIntegrity, Mapped(ErrorCodes.SalePaymentIntegrity));
        Assert.Equal(ErrorCodes.SaleCreditOverrideIntegrity, Mapped(ErrorCodes.SaleCreditOverrideIntegrity));
        Assert.Equal(ErrorCodes.SaleTotalIntegrity, Mapped(ErrorCodes.SaleTotalIntegrity));
        Assert.Equal(ErrorCodes.SaleImmutable, Mapped(ErrorCodes.SaleImmutable));
        Assert.Equal(ErrorCodes.StockReconciliation, Mapped(ErrorCodes.StockReconciliation));
        Assert.Equal(ErrorCodes.StockInsufficient, Mapped(ErrorCodes.StockInsufficient));
    }

    [Fact]
    public void An_idempotency_unique_violation_is_a_conflict()
    {
        var mapped = SaleWriteFailure.TryMap(
            "duplicate key value violates unique constraint \"UX_sale_idempotency_company_operation_key\"",
            SaleWriteFailure.UniqueViolation,
            "UX_sale_idempotency_company_operation_key",
            out var code);

        Assert.True(mapped);
        Assert.Equal(ErrorCodes.SaleIdempotencyConflict, code);
    }

    [Fact]
    public void Unexpected_database_failures_stay_unmapped()
    {
        Assert.False(SaleWriteFailure.TryMap(
            "duplicate key value violates unique constraint \"UX_sale_company_number\"",
            SaleWriteFailure.UniqueViolation,
            "UX_sale_company_number",
            out _));
        Assert.False(SaleWriteFailure.TryMap(
            "null value in column \"Number\" of relation \"sale\" violates not-null constraint",
            "23502",
            null,
            out _));
        Assert.False(SaleWriteFailure.TryMap(
            "Host=127.0.0.1;Database=pexilio_test;Password=secret",
            "08006",
            null,
            out _));
        Assert.False(SaleWriteFailure.TryMap(
            "SELECT nextval('pexilio.sale_number_seq')",
            "42601",
            null,
            out _));
    }

    private static string Mapped(string message)
    {
        Assert.True(SaleWriteFailure.TryMap(message, "23514", null, out var code));
        return code;
    }
}
