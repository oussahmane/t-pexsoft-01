namespace Pexilio.Domain;

public static class ErrorCodes
{
    public const string ValidationFailed = "validation.failed";
    public const string SetupAlreadyComplete = "setup.already_complete";
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string LockedOut = "auth.locked_out";
    public const string RefreshInvalid = "auth.refresh_invalid";
    public const string RefreshReused = "auth.refresh_reused";
    public const string Forbidden = "auth.forbidden";
    public const string AuthenticationRequired = "auth.required";
    public const string EmailTaken = "users.email_taken";
    public const string CompanyMissing = "company.missing";
    public const string SkuTaken = "products.sku_taken";
    public const string ProductNotFound = "products.not_found";
    public const string CategoryNotFound = "categories.not_found";
    public const string CategoryCycle = "categories.cycle";
    public const string CategoryNameTaken = "categories.name_taken";
    public const string StockNotTracked = "stock.not_tracked";
    public const string StockInsufficient = "stock.insufficient";
    public const string StockOpeningExists = "stock.opening_exists";
    public const string StockIdempotencyConflict = "stock.idempotency_conflict";
    public const string StockReconciliation = "stock.reconciliation";
    public const string CustomerNotFound = "customer.not_found";
    public const string CustomerInactive = "customer.inactive";
    public const string CustomerIdempotencyConflict = "customer.idempotency_conflict";
    public const string SupplierNotFound = "suppliers.not_found";
    public const string SupplierNameTaken = "suppliers.name_taken";
    public const string SupplierInactive = "suppliers.inactive";
    public const string PurchaseNotFound = "purchases.not_found";
    public const string PurchaseNotDraft = "purchase.not_draft";
    public const string PurchaseConflict = "purchase.conflict";
    public const string PurchaseIdempotencyConflict = "purchase.idempotency_conflict";
    public const string PurchaseProductInactive = "purchase.product_inactive";
    public const string PurchaseSupplierDocumentTaken = "purchase.supplier_document_taken";
    public const string PurchaseNotReceivable = "purchase.not_receivable";
    public const string PurchaseNotCancellable = "purchase.not_cancellable";
    public const string PurchaseReceiptExcess = "purchase.receipt_excess";
    public const string PurchaseReceiptDocumentTaken = "purchase.receipt_document_taken";
    public const string PurchaseReceiptIntegrity = "purchase.receipt_integrity";
    public const string PurchaseReceiptImmutable = "purchase.receipt_immutable";
    public const string SaleNotFound = "sale.not_found";
    public const string SaleIdempotencyConflict = "sale.idempotency_conflict";
    public const string SaleProductInactive = "sale.product_inactive";
    public const string SaleMovementIntegrity = "sale.movement_integrity";
    public const string SaleReturnMovementIntegrity = "sale.return_movement_integrity";
    public const string SaleTotalIntegrity = "sale.total_integrity";
    public const string SalePaymentIntegrity = "sale.payment_integrity";
    public const string SaleCreditOverrideIntegrity = "sale.credit_override_integrity";
    public const string SaleImmutable = "sale.immutable";
    public const string CreditLimitExceeded = "credit.limit_exceeded";
    public const string PaymentIdempotencyConflict = "payment.idempotency_conflict";
}

public sealed record FieldError(string Field, string Code);
