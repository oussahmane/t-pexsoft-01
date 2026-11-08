using Pexilio.Domain.Company;

namespace Pexilio.Application;

public sealed class SetupRequest
{
    public string? LegalName { get; set; }

    public string? TradeName { get; set; }

    public string? CurrencyCode { get; set; }

    public int? CurrencyScale { get; set; }

    public string? TimeZoneId { get; set; }

    public string? TaxMode { get; set; }

    public string? DefaultTaxRate { get; set; }

    public string? Rc { get; set; }

    public string? Nif { get; set; }

    public string? Nis { get; set; }

    public string? Ai { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    public CompanyInput ToCompanyInput() => new()
    {
        LegalName = LegalName,
        TradeName = TradeName,
        CurrencyCode = CurrencyCode,
        CurrencyScale = CurrencyScale,
        TimeZoneId = TimeZoneId,
        TaxMode = TaxMode,
        DefaultTaxRate = DefaultTaxRate,
        Rc = Rc,
        Nif = Nif,
        Nis = Nis,
        Ai = Ai,
    };
}

public sealed class LoginRequest
{
    public string? Email { get; set; }

    public string? Password { get; set; }
}

public sealed class RefreshRequest
{
    public string? RefreshToken { get; set; }
}

public sealed record SetupCreated(Guid CompanyId, Guid AdministratorId);

public sealed record TokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record CategoryRequest(string? Name, Guid? ParentId);

public sealed record CategoryResponse(Guid Id, string Name, Guid? ParentId);

public readonly struct OptionalText
{
    public bool Specified { get; init; }

    public string? Value { get; init; }
}

public sealed record ProductRequest(
    string? Sku,
    string? Name,
    string? Unit,
    string? UnitPrice,
    Guid? CategoryId,
    Guid? TaxRateId,
    bool? TrackStock,
    bool? IsActive,
    OptionalText WholesaleUnitPrice = default,
    OptionalText MinimumUnitPrice = default);

public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    string Unit,
    string UnitPrice,
    Guid? CategoryId,
    Guid TaxRateId,
    string TaxRate,
    bool TrackStock,
    bool IsActive,
    string? WholesaleUnitPrice = null,
    string? MinimumUnitPrice = null);

public sealed class StockQuantityRequest
{
    public Guid ProductId { get; set; }

    public string? Quantity { get; set; }

    public string? Reason { get; set; }

    public string? UnitCost { get; set; }
}

public sealed class MinimumStockRequest
{
    public string? MinimumQuantity { get; set; }
}

public sealed record StockLocationResponse(
    Guid Id,
    string Code,
    string Name,
    bool IsDefault,
    bool PreventNegativeStock);

public sealed record StockBalanceResponse(
    Guid ProductId,
    string Sku,
    string Name,
    string Unit,
    string Quantity,
    string? MinimumQuantity,
    bool BelowMinimum,
    bool AtMinimum);

public sealed record StockMovementResponse(
    Guid Id,
    Guid ProductId,
    Guid StockLocationId,
    string MovementType,
    string QuantityDelta,
    string Reason,
    string BalanceAfter,
    DateTimeOffset CreatedAt,
    Guid CreatedBy);

public sealed class SupplierRequest
{
    public string? Name { get; set; }

    public string? ContactName { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? TaxId { get; set; }

    public string? Notes { get; set; }

    public bool? IsActive { get; set; }
}

public sealed record SupplierResponse(
    Guid Id,
    string Name,
    string? ContactName,
    string? Phone,
    string? Email,
    string? Address,
    string? TaxId,
    string? Notes,
    bool IsActive);

public sealed class CustomerRequest
{
    public string? Type { get; set; }

    public string? Name { get; set; }

    public string? Phone { get; set; }

    public string? Address { get; set; }

    public string? Notes { get; set; }

    public string? Rc { get; set; }

    public string? Nif { get; set; }

    public string? Nis { get; set; }

    public string? Ai { get; set; }

    public string? CreditLimit { get; set; }

    public bool? IsActive { get; set; }
}

public sealed record CustomerResponse(
    Guid Id,
    string Type,
    string Name,
    string? Phone,
    string? Address,
    string? Notes,
    string? Rc,
    string? Nif,
    string? Nis,
    string? Ai,
    string? CreditLimit,
    bool IsActive,
    string OutstandingAmount);

public sealed record CustomerStatementLineResponse(
    DateTimeOffset OccurredAt,
    string Kind,
    Guid DocumentId,
    string? DocumentNumber,
    string Amount,
    string RunningBalance,
    bool? CreditLimitOverridden = null,
    string? CreditOverrideReason = null,
    string? CreditLimitSnapshot = null,
    string? CreditOutstandingBefore = null,
    string? CreditProjectedOutstanding = null);

public sealed record CustomerStatementResponse(
    Guid CustomerId,
    string CustomerName,
    string TimeZoneId,
    string From,
    string To,
    string OpeningBalance,
    string ClosingBalance,
    string OutstandingAmount,
    IReadOnlyList<CustomerStatementLineResponse> Lines);

public sealed class PurchaseLineRequest
{
    public Guid ProductId { get; set; }

    public string? Quantity { get; set; }

    public string? UnitCost { get; set; }
}

public sealed class PurchaseDraftRequest
{
    public Guid SupplierId { get; set; }

    public string? SupplierDocumentRef { get; set; }

    public string? Notes { get; set; }

    public uint? Version { get; set; }

    public List<PurchaseLineRequest>? Lines { get; set; }
}

public sealed record PurchaseLineResponse(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    string ProductUnit,
    string QuantityOrdered,
    string QuantityReceived,
    string UnitCost,
    string SellingPrice);

public sealed record PurchaseSummaryResponse(
    Guid Id,
    string Number,
    string Status,
    Guid SupplierId,
    string SupplierName,
    string? SupplierDocumentRef,
    DateTimeOffset CreatedAt);

public sealed record PurchaseResponse(
    Guid Id,
    string Number,
    string Status,
    Guid SupplierId,
    string SupplierName,
    string? SupplierTaxId,
    string? SupplierDocumentRef,
    string? Notes,
    uint Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseLineResponse> Lines,
    DateTimeOffset? OrderedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<PurchaseReceiptResponse> Receipts);

public sealed class PurchaseVersionRequest
{
    public uint? Version { get; set; }
}

public sealed class PurchaseReceiptLineRequest
{
    public Guid PurchaseLineId { get; set; }

    public string? Quantity { get; set; }
}

public sealed class PurchaseReceiptRequest
{
    public uint? Version { get; set; }

    public string? DocumentRef { get; set; }

    public List<PurchaseReceiptLineRequest>? Lines { get; set; }
}

public sealed record PurchaseReceiptLineResponse(
    Guid Id,
    Guid PurchaseLineId,
    Guid ProductId,
    string Quantity,
    string UnitCost,
    Guid StockMovementId);

public sealed record PurchaseReceiptResponse(
    Guid Id,
    string Number,
    string DocumentRef,
    DateTimeOffset ReceivedAt,
    Guid ReceivedBy,
    IReadOnlyList<PurchaseReceiptLineResponse> Lines);

public sealed class SaleLineRequest
{
    public Guid ProductId { get; set; }

    public string? Quantity { get; set; }

    public string? UnitPrice { get; set; }

    public string? LineTotal { get; set; }

    public string? PriceKind { get; set; }

    public string? LineDiscount { get; set; }

    public string? DiscountKind { get; set; }

    public string? DiscountRate { get; set; }
}

public sealed class SalePaymentRequest
{
    public string? Method { get; set; }

    public string? Amount { get; set; }
}

public sealed class SaleFinalizeRequest
{
    public List<SaleLineRequest>? Lines { get; set; }

    public string? Total { get; set; }

    public string? DiscountAmount { get; set; }

    public string? DiscountKind { get; set; }

    public string? DiscountRate { get; set; }

    public List<SalePaymentRequest>? Payments { get; set; }

    public Guid? CustomerId { get; set; }

    public bool CreditOverride { get; set; }

    public string? CreditOverrideReason { get; set; }
}

public sealed record SaleLineResponse(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    string ProductUnit,
    string Quantity,
    string ListUnitPrice,
    string UnitPrice,
    string LineTotal,
    bool TracksStock,
    Guid? StockMovementId,
    string TaxRate,
    string PriceKind,
    string LineDiscount,
    string AllocatedSaleDiscount,
    string DiscountKind,
    string? DiscountRate,
    bool BelowMinimumOverride,
    string ReturnedQuantity,
    string RemainingReturnableQuantity,
    string ReturnedAmount,
    string RemainingReturnableAmount);

public sealed class SalePaymentPostRequest
{
    public string? Method { get; set; }

    public string? Amount { get; set; }

    public string? Reference { get; set; }

    public string? Notes { get; set; }
}

public sealed class SaleReturnLineRequest
{
    public Guid SaleLineId { get; set; }

    public string? Quantity { get; set; }
}

public sealed class SaleRefundTenderRequest
{
    public string? Method { get; set; }

    public string? Amount { get; set; }

    public string? Reference { get; set; }

    public string? Notes { get; set; }
}

public sealed class SaleReturnPostRequest
{
    public string? Reason { get; set; }

    public List<SaleReturnLineRequest>? Lines { get; set; }

    public List<SaleRefundTenderRequest>? Refunds { get; set; }
}

public sealed record SalePaymentResponse(
    Guid Id,
    string Method,
    string Amount,
    DateTimeOffset PaidAt,
    string? ReceiptNumber,
    string? Reference,
    string? Notes,
    Guid ReceivedBy);

public sealed record SalePaymentPostResponse(
    Guid SaleId,
    string SaleNumber,
    string PaidAmount,
    string RefundedAmount,
    string OutstandingAmount,
    string PaymentState,
    SalePaymentResponse Payment);

public sealed record SaleReturnLineResponse(
    Guid Id,
    Guid SaleLineId,
    Guid ProductId,
    string Quantity,
    string Amount,
    bool TracksStock,
    Guid? StockMovementId);

public sealed record SaleRefundResponse(
    Guid Id,
    string Method,
    string Amount,
    string? Reference,
    string? Notes,
    DateTimeOffset RefundedAt,
    Guid RefundedBy);

public sealed record SaleReturnPostResponse(
    Guid Id,
    Guid SaleId,
    string Number,
    string Reason,
    DateTimeOffset ReturnedAt,
    string ReturnAmount,
    string RequiredRefund,
    string NetAccountReduction,
    string PaidAmount,
    string RefundedAmount,
    string OutstandingAmount,
    string PaymentState,
    IReadOnlyList<SaleReturnLineResponse> Lines,
    IReadOnlyList<SaleRefundResponse> Refunds);

public sealed record SaleResponse(
    Guid Id,
    string Number,
    string Status,
    string Subtotal,
    string DiscountAmount,
    string DiscountKind,
    string? DiscountRate,
    string Total,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    IReadOnlyList<SaleLineResponse> Lines,
    IReadOnlyList<SalePaymentResponse> Payments,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerType,
    string PaidAmount,
    string RefundedAmount,
    string OutstandingAmount,
    string PaymentState,
    bool CreditLimitOverridden,
    string? CreditOverrideReason,
    string? CreditLimitSnapshot,
    string? CreditOutstandingBefore,
    string? CreditProjectedOutstanding);

public sealed record SaleSummaryResponse(
    Guid Id,
    string Number,
    string Total,
    DateTimeOffset CreatedAt,
    Guid? CustomerId,
    string? CustomerName,
    string PaidAmount,
    string RefundedAmount,
    string OutstandingAmount,
    string PaymentState,
    bool CreditLimitOverridden,
    string? CreditOverrideReason,
    string? CreditLimitSnapshot,
    string? CreditOutstandingBefore,
    string? CreditProjectedOutstanding);

public sealed record CompanyResponse(
    Guid Id,
    string LegalName,
    string? TradeName,
    string CurrencyCode,
    int CurrencyScale,
    string TimeZoneId,
    string TaxMode,
    string DefaultTaxRate,
    string? Rc,
    string? Nif,
    string? Nis,
    string? Ai);
