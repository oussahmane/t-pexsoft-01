using Pexilio.Domain.Company;
using Pexilio.Domain.Stock;

namespace Pexilio.Application;

public interface ISetupService
{
    Task<bool> IsCompleteAsync(CancellationToken cancellationToken);

    Task<Result<SetupCreated>> SetupAsync(SetupRequest request, CancellationToken cancellationToken);
}

public interface ISessionService
{
    Task<Result<TokenPair>> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<Result<TokenPair>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken);

    Task LogoutAsync(RefreshRequest request, CancellationToken cancellationToken);
}

public interface ICompanyService
{
    Task<CompanyResponse?> GetAsync(CancellationToken cancellationToken);

    Task<Result<CompanyResponse>> UpdateAsync(CompanyInput request, CancellationToken cancellationToken);
}

public interface ICashierService
{
    Task<Result<Guid>> CreateAsync(LoginRequest request, CancellationToken cancellationToken);
}

public interface ICatalogService
{
    Task<Result<IReadOnlyList<CategoryResponse>>> ListCategoriesAsync(CancellationToken cancellationToken);

    Task<Result<CategoryResponse>> CreateCategoryAsync(CategoryRequest request, CancellationToken cancellationToken);

    Task<Result<CategoryResponse>> UpdateCategoryAsync(Guid id, CategoryRequest request, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<ProductResponse>>> ListProductsAsync(CancellationToken cancellationToken);

    Task<Result<ProductResponse>> CreateProductAsync(ProductRequest request, CancellationToken cancellationToken);

    Task<Result<ProductResponse>> UpdateProductAsync(Guid id, ProductRequest request, CancellationToken cancellationToken);
}

public interface IStockService
{
    Task<Result<IReadOnlyList<StockLocationResponse>>> ListLocationsAsync(CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<StockBalanceResponse>>> ListBalancesAsync(CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<StockMovementResponse>>> ListMovementsAsync(Guid? productId, CancellationToken cancellationToken);

    Task<Result<StockMovementResponse>> OpenAsync(
        StockQuantityRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken);

    Task<Result<StockMovementResponse>> ReceiveAsync(
        StockQuantityRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken);

    Task<Result<StockMovementResponse>> AdjustAsync(
        StockQuantityRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken);

    Task<Result<StockBalanceResponse>> SetMinimumAsync(
        Guid productId,
        MinimumStockRequest request,
        CancellationToken cancellationToken);

    Task<Result<StockMovement>> PostInCurrentTransactionAsync(
        string movementType,
        Guid productId,
        decimal quantity,
        string reason,
        decimal? unitCost,
        Guid userId,
        string sourceType,
        Guid? sourceId,
        CancellationToken cancellationToken,
        Guid? movementId = null);
}

public interface IPurchasingService
{
    Task<Result<IReadOnlyList<SupplierResponse>>> ListSuppliersAsync(CancellationToken cancellationToken);

    Task<Result<SupplierResponse>> GetSupplierAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<SupplierResponse>> CreateSupplierAsync(
        SupplierRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task<Result<SupplierResponse>> UpdateSupplierAsync(
        Guid id,
        SupplierRequest request,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<PurchaseSummaryResponse>>> ListPurchasesAsync(
        string? status,
        CancellationToken cancellationToken);

    Task<Result<PurchaseResponse>> GetPurchaseAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<PurchaseResponse>> CreatePurchaseAsync(
        PurchaseDraftRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken);

    Task<Result<PurchaseResponse>> UpdatePurchaseAsync(
        Guid id,
        PurchaseDraftRequest request,
        CancellationToken cancellationToken);

    Task<Result<Guid>> DeletePurchaseAsync(Guid id, uint? version, CancellationToken cancellationToken);

    Task<Result<PurchaseResponse>> OrderPurchaseAsync(
        Guid id,
        PurchaseVersionRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task<Result<PurchaseReceiptResponse>> ReceivePurchaseAsync(
        Guid id,
        PurchaseReceiptRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken);

    Task<Result<PurchaseResponse>> CancelPurchaseAsync(
        Guid id,
        PurchaseVersionRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}

public interface ICustomerService
{
    Task<Result<IReadOnlyList<CustomerResponse>>> ListAsync(string? query, CancellationToken cancellationToken);

    Task<Result<CustomerResponse>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<CustomerStatementResponse>> GetStatementAsync(
        Guid id,
        string? from,
        string? to,
        CancellationToken cancellationToken);

    Task<Result<CustomerResponse>> CreateAsync(
        CustomerRequest request,
        Guid idempotencyKey,
        bool administrator,
        CancellationToken cancellationToken);

    Task<Result<CustomerResponse>> UpdateAsync(
        Guid id,
        CustomerRequest request,
        bool administrator,
        CancellationToken cancellationToken);
}

public interface ISalesService
{
    Task<Result<IReadOnlyList<SaleSummaryResponse>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<SaleResponse>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<SaleResponse>> FinalizeCashSaleAsync(
        SaleFinalizeRequest request,
        Guid idempotencyKey,
        Guid userId,
        bool administrator,
        CancellationToken cancellationToken);

    Task<Result<SalePaymentPostResponse>> AddPaymentAsync(
        Guid saleId,
        SalePaymentPostRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken);

    Task<Result<SaleReturnPostResponse>> PostReturnAsync(
        Guid saleId,
        SaleReturnPostRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken);
}

public interface ISaleProductLock
{
    ValueTask AfterProductLockAsync(CancellationToken cancellationToken);
}
